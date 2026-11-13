using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Emits source-generated COM interfaces for vtable and dual interfaces, and the extension blocks that give their accessors property syntax.
/// </summary>
internal sealed partial class InterfaceEmitter
{
    private const int DispIdValue = 0;
    private const int DispIdNewEnum = -4;

    // The receiver parameter of the extension blocks, which index parameters cannot shadow, and the expression that refers to it
    // in the members of the blocks (@this).
    private const string Receiver = "this";
    private static readonly string ReceiverExpression = Syntax.Escape(Receiver);

    private readonly EmitContext _context;

    public InterfaceEmitter(EmitContext context)
    {
        _context = context;
    }

    public IEnumerable<MemberDeclarationSyntax> Emit(InterfaceDefinition definition, string @namespace)
    {
        if (definition.Guid is not { } guid)
        {
            LogMissingGuid(_context.Logger, definition.Name);
            yield break;
        }

        var mapper = new TypeMapper(_context, @namespace);
        var signatures = new SignatureBuilder(_context, mapper, @namespace);

        bool isUnsafe = false;
        var members = new List<MemberDeclarationSyntax>();
        int slot = GetFirstSlot(definition);
        foreach (var method in definition.Methods)
        {
            // Placeholders for the missing vtable slots keep the following methods at the right offsets.
            for (; method.VtableSlot >= 0 && slot < method.VtableSlot; slot++)
            {
                members.Add(Placeholder(slot));
            }

            slot = Math.Max(slot, method.VtableSlot + 1);
            members.Add(signatures.BuildComMethod(method, ref isUnsafe));
        }

        // The COM generator re-implements the members of base interfaces defined in the same assembly in the derived ones,
        // so interfaces derived from IDispatch, whose signatures use pointers, have to be unsafe as well.
        var modifiers = new List<SyntaxKind> { _context.Accessibility };
        if (isUnsafe || definition.BaseInterface is not null)
        {
            modifiers.Add(SyntaxKind.UnsafeKeyword);
        }

        modifiers.Add(SyntaxKind.PartialKeyword);

        var declaration = InterfaceDeclaration(Syntax.Identifier(definition.Name))
            .WithModifiers(Syntax.Modifiers([.. modifiers]))
            .WithAttributeLists(List(
            [
                Syntax.AttributeList(Syntax.Attribute(
                    $"{Syntax.MarshallingNamespace}.GeneratedComInterface",
                    $"StringMarshalling = {Syntax.InteropNamespace}.StringMarshalling.Custom, StringMarshallingCustomType = typeof({Syntax.MarshallingNamespace}.BStrStringMarshaller)")),
                Syntax.AttributeList(Syntax.Attribute($"{Syntax.InteropNamespace}.Guid", Syntax.GuidLiteral(guid))),
            ]))
            .WithMembers(List(members))
            .WithDocumentation(definition.Documentation);

        if (GetBaseType(definition, @namespace) is { } baseType)
        {
            declaration = declaration.WithBaseList(BaseList(SingletonSeparatedList<BaseTypeSyntax>(SimpleBaseType(Syntax.Type(baseType)))));
        }

        yield return _context.Annotations.Annotate(declaration, definition);

        if (EmitExtensions(definition, @namespace, signatures) is { } extensions)
        {
            yield return extensions;
        }
    }

    /// <summary>
    /// Gets the vtable slots of the interface that are not described by the type library, for which <see cref="Emit"/> emits placeholders.
    /// </summary>
    public static List<int> GetVtableGaps(InterfaceDefinition definition)
    {
        var gaps = new List<int>();
        int slot = GetFirstSlot(definition);
        foreach (var method in definition.Methods)
        {
            for (; method.VtableSlot >= 0 && slot < method.VtableSlot; slot++)
            {
                gaps.Add(slot);
            }

            slot = Math.Max(slot, method.VtableSlot + 1);
        }

        return gaps;
    }

    // The first vtable slot of the members of the generated interface: the slot after those of its base interfaces, or an
    // earlier one when the generated base interface has fewer slots, e.g. when a base interface without a GUID is skipped.
    private static int GetFirstSlot(InterfaceDefinition definition)
    {
        int covered = definition.BaseInterface switch
        {
            null => 3,
            PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } or WellKnownTypeReference { Type: WellKnownType.IEnumVariant } => 7,
            UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } } => 7,
            UserDefinedTypeReference { Definition: InterfaceDefinition @base } when @base.Methods.All(static m => m.VtableSlot >= 0) =>
                Math.Max(@base.BaseVtableSlots, @base.Methods.Select(static m => m.VtableSlot + 1).DefaultIfEmpty(0).Max()),
            _ => definition.BaseVtableSlots,
        };

        return Math.Min(definition.BaseVtableSlots, covered);
    }

    /// <summary>The name of the placeholder method of a vtable slot.</summary>
    public static string GetPlaceholderName(int slot) => $"_VtblGap{slot}";

    private string? GetBaseType(InterfaceDefinition definition, string @namespace) => definition.BaseInterface switch
    {
        null => null,
        UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: not InterfaceKind.Dispatch } baseInterface } =>
            _context.GetTypeName(baseInterface, @namespace),
        UserDefinedTypeReference or PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => $"{_context.Support}.IDispatch",
        // The methods of the interface follow those of IEnumVARIANT in the vtable.
        WellKnownTypeReference { Type: WellKnownType.IEnumVariant } => $"{_context.Support}.IEnumVARIANT",
        _ => null,
    };

    private static MethodDeclarationSyntax Placeholder(int slot) =>
        MethodDeclaration(Syntax.Type("int"), Syntax.Identifier(GetPlaceholderName(slot)))
            .WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute($"{Syntax.InteropNamespace}.PreserveSig"))))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .WithDocumentation($"A placeholder for the vtable slot {slot}, which is not described by the type library. Do not call it.");

    private ClassDeclarationSyntax? EmitExtensions(InterfaceDefinition definition, string @namespace, SignatureBuilder signatures)
    {
        var methodNames = GetMethodNamesInHierarchy(definition);
        var members = new List<MemberDeclarationSyntax>();
        var namedIndexers = new List<NamedIndexer>();
        bool hasIndexer = false;

        foreach (var group in definition.Methods.Where(static m => m.IsProperty && m.MemberId != DispIdNewEnum).GroupBy(static m => m.Name))
        {
            // An interface can add accessors to a property of its base interfaces, e.g. a later version a setter to a read-only
            // property. Its extension block wins for receivers of its type, so it also takes the accessors it inherits.
            var getter = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyGet)
                ?? FindInheritedAccessor(definition, group.Key, InvokeKind.PropertyGet);
            var put = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPut);
            var putRef = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPutRef);
            if (put is null && putRef is null)
            {
                put = FindInheritedAccessor(definition, group.Key, InvokeKind.PropertyPut);
                putRef = FindInheritedAccessor(definition, group.Key, InvokeKind.PropertyPutRef);
            }

            var getterSignature = getter is null ? null : AnalyzeGetter(getter, signatures);
            var setterMethod = put ?? putRef;
            var setterSignature = setterMethod is null ? null : AnalyzeSetter(setterMethod, signatures);
            if (getterSignature is not null && setterSignature is not null && !getterSignature.Matches(setterSignature))
            {
                // The accessors of the interface win over those of a different property of a base interface.
                if (group.Contains(getter!))
                {
                    setterSignature = null;
                }
                else
                {
                    getterSignature = null;
                }
            }

            var property = getterSignature ?? setterSignature;
            if (property is null)
            {
                continue;
            }

            string? documentation = (getter ?? setterMethod)!.Documentation;
            string? getExpression = getterSignature is null ? null : $"{ReceiverExpression}.{Syntax.Escape(SignatureBuilder.GetMethodName(getter!))}({Arguments(getterSignature, property.Index)})";
            string? setExpression = setterSignature is null ? null : $"{ReceiverExpression}.{Syntax.Escape(SignatureBuilder.GetMethodName(setterMethod!))}({Arguments(setterSignature, property.Index, "value")})";
            MethodDefinition?[] accessors = [getterSignature is null ? null : getter, setterSignature is null ? null : setterMethod];

            if (property.Index.Count == 0)
            {
                if (methodNames.Contains(group.Key))
                {
                    continue;
                }

                members.Add(AnnotateAccessors(Property(property.Type, group.Key, getExpression, setExpression, documentation), accessors));
            }
            else if (_context.Options.SupportsExtensionIndexers && !hasIndexer && IsDefaultMember(definition, group.Key, accessors))
            {
                members.Add(AnnotateAccessors(Indexer(property.Type, property.Index, getExpression, setExpression, documentation), accessors));
                hasIndexer = true;
            }
            else if (_context.Options.NamedIndexers != NamedIndexers.None && !methodNames.Contains(group.Key))
            {
                namedIndexers.Add(new NamedIndexer(
                    group.Key,
                    property,
                    getterSignature is null ? null : getter,
                    setterSignature is null ? null : setterMethod,
                    documentation,
                    getterSignature?.Parameters,
                    setterSignature?.Parameters));
            }
        }

        // The proxies of the other parameterized properties, except one named Item next to the extension indexer, which is
        // compiled into a property named Item as well.
        var proxyTypes = new List<MemberDeclarationSyntax>();
        var takenNames = new HashSet<string>(methodNames.Concat(definition.Methods.Select(static m => m.Name)), StringComparer.Ordinal);
        foreach (var namedIndexer in namedIndexers.Where(n => !(hasIndexer && n.Name == "Item")))
        {
            var (member, type) = NamedIndexerEmitter.Create(
                _context,
                namedIndexer.Name,
                NamedIndexerEmitter.GetTypeName(namedIndexer.Name, takenNames),
                definition.Name,
                _context.GetTypeName(definition, @namespace),
                ReceiverExpression,
                namedIndexer.Property.Type,
                namedIndexer.Property.Index,
                namedIndexer.Getter is { } getter ? SignatureBuilder.GetMethodName(getter) : null,
                namedIndexer.Setter is { } setter ? SignatureBuilder.GetMethodName(setter) : null,
                namedIndexer.Documentation,
                [.. new[] { namedIndexer.Getter, namedIndexer.Setter }.OfType<MethodDefinition>()],
                namedIndexer.GetterParameters,
                namedIndexer.SetterParameters);
            members.Add(member);
            proxyTypes.Add(type);
        }

        var newEnum = definition.Methods.FirstOrDefault(static m =>
            m.MemberId == DispIdNewEnum && m.InvokeKind is InvokeKind.Method or InvokeKind.PropertyGet && m.Parameters.Count == 1 && SignatureBuilder.HasRetval(m, out _));
        if (newEnum is not null && !methodNames.Contains("GetEnumerator"))
        {
            members.Add(_context.Annotations.Annotate(
                MethodDeclaration(Syntax.Type($"{_context.Support}.ComEnumerator"), Syntax.Identifier("GetEnumerator"))
                    .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
                    .WithExpressionBody(Syntax.Arrow($"{_context.Support}.ComEnumerator.FromObject({ReceiverExpression}.{Syntax.Escape(SignatureBuilder.GetMethodName(newEnum))}())"))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                    .WithDocumentation("Returns an enumerator that iterates through the collection, so that it can be used in foreach loops."),
                newEnum,
                SyntaxRole.Enumerator));
        }

        // Instance methods win over extension methods when they are applicable, so the overloads only take the shorter calls.
        if (_context.Options.OverloadsForDefaultValues)
        {
            foreach (var method in definition.Methods.Where(static m => m.InvokeKind == InvokeKind.Method))
            {
                var (returnType, parameters) = signatures.MapComSignature(method);
                if (returnType.IsPointer)
                {
                    continue;
                }

                string name = SignatureBuilder.GetMethodName(method);
                foreach (var overload in signatures.GetDefaultOverloads(method, parameters, lateBound: false))
                {
                    members.Add(_context.Annotations.Annotate(
                        SignatureBuilder.BuildOverload(overload, returnType.Name, name, $"{ReceiverExpression}.{Syntax.Escape(name)}", SyntaxKind.PublicKeyword)
                            .WithDocumentation(method.Documentation),
                        method,
                        SyntaxRole.Overload));
                }
            }
        }

        if (members.Count == 0)
        {
            return null;
        }

        var receiver = Syntax.Parameter(_context.GetTypeName(definition, @namespace), Receiver);
        var extension = ExtensionBlockDeclaration()
            .WithParameterList(Syntax.Parameters([receiver]))
            .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken))
            .WithMembers(List(members))
            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken));

        return _context.Annotations.Annotate(
            ClassDeclaration(Syntax.Identifier(EmitContext.GetExtensionsClassName(definition)))
                .WithModifiers(Syntax.Modifiers(_context.Accessibility, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword))
                .WithMembers(List<MemberDeclarationSyntax>([extension, .. proxyTypes]))
                .WithDocumentation($"Provides the properties of the {definition.Name} interface."),
            definition,
            SyntaxRole.Extensions);
    }

    // Associates a property or an indexer with the accessors it calls.
    private TNode AnnotateAccessors<TNode>(TNode node, IEnumerable<MethodDefinition?> accessors)
        where TNode : SyntaxNode =>
        node.WithAdditionalAnnotations(accessors.OfType<MethodDefinition>().Select(a => _context.Annotations.GetAnnotation(a, SyntaxRole.Property)));

    private static bool IsDefaultMember(InterfaceDefinition definition, string name, IEnumerable<MethodDefinition?> accessors) =>
        accessors.Any(static m => m?.MemberId == DispIdValue)
        || (name == "Item" && !definition.Methods.Any(static m => m.IsProperty && m.MemberId == DispIdValue && m.Parameters.Count > 1));

    // The accessor of a property with the specified invoke kind that the nearest base interface declares.
    private static MethodDefinition? FindInheritedAccessor(InterfaceDefinition definition, string name, InvokeKind invokeKind)
    {
        for (var current = BaseInterfaceOf(definition); current is not null; current = BaseInterfaceOf(current))
        {
            if (current.Methods.FirstOrDefault(m => m.IsProperty && m.InvokeKind == invokeKind && string.Equals(m.Name, name, StringComparison.Ordinal)) is { } accessor)
            {
                return accessor;
            }
        }

        return null;
    }

    private static InterfaceDefinition? BaseInterfaceOf(InterfaceDefinition definition) =>
        (definition.BaseInterface as UserDefinedTypeReference)?.Definition as InterfaceDefinition;

    private static HashSet<string> GetMethodNamesInHierarchy(InterfaceDefinition definition)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        TypeReference? root = null;
        for (var current = definition; current is not null; current = (current.BaseInterface as UserDefinedTypeReference)?.Definition as InterfaceDefinition)
        {
            root = current.BaseInterface;
            foreach (var method in current.Methods)
            {
                names.Add(SignatureBuilder.GetMethodName(method));
            }
        }

        if (root is WellKnownTypeReference { Type: WellKnownType.IEnumVariant })
        {
            names.UnionWith(["Next", "Skip", "Reset", "Clone"]);
        }
        else if (definition.BaseInterface is not null)
        {
            names.UnionWith(["GetTypeInfoCount", "GetTypeInfo", "GetIDsOfNames", "Invoke"]);
        }

        return names;
    }

    private static PropertySignature? AnalyzeGetter(MethodDefinition getter, SignatureBuilder signatures)
    {
        if (!SignatureBuilder.HasRetval(getter, out var retvalType))
        {
            return null;
        }

        var parameters = signatures.MapParameters(getter.Parameters.Take(getter.Parameters.Count - 1));
        if (parameters.Any(static p => p.IsByRef))
        {
            return null;
        }

        var type = signatures.Mapper.WithMarshalling(getter.RetvalParameter!.Marshalling).MapValue(retvalType);
        return type.IsVoid || type.IsPointer ? null : PropertySignature.Create(type.Name, parameters);
    }

    private static PropertySignature? AnalyzeSetter(MethodDefinition setter, SignatureBuilder signatures)
    {
        if (!setter.ReturnsHResult || setter.RetvalParameter is not null || setter.Parameters.Count == 0)
        {
            return null;
        }

        var parameters = signatures.MapParameters(setter.Parameters, setter: true);
        if (parameters.Any(static p => p.IsByRef) || parameters[^1].TypeName.EndsWith('*'))
        {
            return null;
        }

        return PropertySignature.Create(parameters[^1].TypeName, parameters.Take(parameters.Count - 1).ToList());
    }

    // The arguments of the call of an accessor from the property or the indexer with the index parameters.
    private static string Arguments(PropertySignature accessor, IReadOnlyList<SignatureParameter> index, string? value = null)
    {
        var arguments = SignatureBuilder.GetAccessorArguments(accessor.Parameters, SignatureParameter.GetIndexNames(index, Receiver).Select(Syntax.Escape).ToList());
        if (value is not null)
        {
            arguments.Add(value);
        }

        return string.Join(", ", arguments);
    }

    private static PropertyDeclarationSyntax Property(string type, string name, string? getExpression, string? setExpression, string? documentation)
    {
        var property = PropertyDeclaration(Syntax.Type(type), Syntax.Identifier(name))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword));

        property = setExpression is null
            ? property.WithExpressionBody(Syntax.Arrow(getExpression!)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            : property.WithAccessorList(Accessors(getExpression, setExpression));

        return property.WithDocumentation(documentation);
    }

    private static IndexerDeclarationSyntax Indexer(string type, IReadOnlyList<SignatureParameter> index, string? getExpression, string? setExpression, string? documentation)
    {
        string[] names = SignatureParameter.GetIndexNames(index, Receiver);
        var parameters = index.Select((p, i) => p.Annotate(Syntax.Parameter(p.TypeName, names[i])));
        var indexer = IndexerDeclaration(Syntax.Type(type))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
            .WithParameterList(Syntax.BracketedParameters(parameters));

        indexer = setExpression is null
            ? indexer.WithExpressionBody(Syntax.Arrow(getExpression!)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            : indexer.WithAccessorList(Accessors(getExpression, setExpression));

        return indexer.WithDocumentation(documentation);
    }

    internal static AccessorListSyntax Accessors(string? getExpression, string? setExpression)
    {
        var accessors = new List<AccessorDeclarationSyntax>();
        if (getExpression is not null)
        {
            accessors.Add(AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithExpressionBody(Syntax.Arrow(getExpression))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));
        }

        if (setExpression is not null)
        {
            accessors.Add(AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                .WithExpressionBody(Syntax.Arrow(setExpression))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken)));
        }

        return AccessorList(List(accessors));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping the interface '{Name}', which has no GUID; the pointers to it are generated as IUnknown or IDispatch pointers")]
    private static partial void LogMissingGuid(ILogger logger, string name);

    // A parameterized property that gets a proxy, with the accessors that the proxy calls.
    private sealed record NamedIndexer(
        string Name,
        PropertySignature Property,
        MethodDefinition? Getter,
        MethodDefinition? Setter,
        string? Documentation,
        IReadOnlyList<SignatureParameter>? GetterParameters,
        IReadOnlyList<SignatureParameter>? SetterParameters);

    // The type and the index parameters of a property as an accessor declares them. Parameters are the parameters of the
    // accessor without the value: the index parameters and the [lcid] parameters, for which the property passes the default.
    private sealed record PropertySignature(string Type, IReadOnlyList<SignatureParameter> Index, IReadOnlyList<SignatureParameter> Parameters)
    {
        public static PropertySignature Create(string type, IReadOnlyList<SignatureParameter> parameters) =>
            new(type, parameters.Where(static p => !SignatureBuilder.IsLcid(p)).ToList(), parameters);

        public bool Matches(PropertySignature other) => Type == other.Type && SignatureParameter.HaveSameTypes(Index, other.Index);
    }
}
