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
        int slot = definition.BaseVtableSlots;
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
        int slot = definition.BaseVtableSlots;
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

    /// <summary>The name of the placeholder method of a vtable slot.</summary>
    public static string GetPlaceholderName(int slot) => $"_VtblGap{slot}";

    private string? GetBaseType(InterfaceDefinition definition, string @namespace) => definition.BaseInterface switch
    {
        null => null,
        UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: not InterfaceKind.Dispatch } baseInterface } =>
            _context.GetTypeName(baseInterface, @namespace),
        UserDefinedTypeReference or PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => $"{_context.Support}.IDispatch",
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
        bool hasIndexer = false;

        foreach (var group in definition.Methods.Where(static m => m.IsProperty && m.MemberId != DispIdNewEnum).GroupBy(static m => m.Name))
        {
            var getter = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyGet);
            var put = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPut);
            var putRef = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPutRef);

            var getterSignature = getter is null ? null : AnalyzeGetter(getter, signatures);
            var setterMethod = put ?? putRef;
            var setterSignature = setterMethod is null ? null : AnalyzeSetter(setterMethod, signatures);
            if (getterSignature is not null && setterSignature is not null && !getterSignature.Matches(setterSignature))
            {
                setterSignature = null;
            }

            var property = getterSignature ?? setterSignature;
            if (property is null)
            {
                continue;
            }

            string? documentation = (getter ?? setterMethod)!.Documentation;
            string? getExpression = getterSignature is null ? null : $"@this.{Syntax.Escape(SignatureBuilder.GetMethodName(getter!))}({Arguments(property.Index)})";
            string? setExpression = setterSignature is null ? null : $"@this.{Syntax.Escape(SignatureBuilder.GetMethodName(setterMethod!))}({Arguments(property.Index, "value")})";
            MethodDefinition?[] accessors = [getterSignature is null ? null : getter, setterSignature is null ? null : setterMethod];

            if (property.Index.Count == 0)
            {
                if (methodNames.Contains(group.Key))
                {
                    continue;
                }

                members.Add(AnnotateAccessors(Property(property.Type, group.Key, getExpression, setExpression, documentation), accessors));
            }
            else if (_context.Options.SupportsExtensionIndexers && !hasIndexer && IsDefaultMember(definition, group))
            {
                members.Add(AnnotateAccessors(Indexer(property.Type, property.Index, getExpression, setExpression, documentation), accessors));
                hasIndexer = true;
            }
        }

        var newEnum = definition.Methods.FirstOrDefault(static m =>
            m.MemberId == DispIdNewEnum && m.InvokeKind is InvokeKind.Method or InvokeKind.PropertyGet && m.Parameters.Count == 1 && SignatureBuilder.HasRetval(m, out _));
        if (newEnum is not null && !methodNames.Contains("GetEnumerator"))
        {
            members.Add(_context.Annotations.Annotate(
                MethodDeclaration(Syntax.Type($"{_context.Support}.ComEnumerator"), Syntax.Identifier("GetEnumerator"))
                    .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
                    .WithExpressionBody(Syntax.Arrow($"{_context.Support}.ComEnumerator.FromObject(@this.{Syntax.Escape(SignatureBuilder.GetMethodName(newEnum))}())"))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                    .WithDocumentation("Returns an enumerator that iterates through the collection, so that it can be used in foreach loops."),
                newEnum,
                SyntaxRole.Enumerator));
        }

        if (members.Count == 0)
        {
            return null;
        }

        var receiver = Syntax.Parameter(_context.GetTypeName(definition, @namespace), "this");
        var extension = ExtensionBlockDeclaration()
            .WithParameterList(Syntax.Parameters([receiver]))
            .WithOpenBraceToken(Token(SyntaxKind.OpenBraceToken))
            .WithMembers(List(members))
            .WithCloseBraceToken(Token(SyntaxKind.CloseBraceToken));

        return _context.Annotations.Annotate(
            ClassDeclaration(Syntax.Identifier(EmitContext.GetExtensionsClassName(definition)))
                .WithModifiers(Syntax.Modifiers(_context.Accessibility, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword))
                .WithMembers(SingletonList<MemberDeclarationSyntax>(extension))
                .WithDocumentation($"Provides the properties of the {definition.Name} interface."),
            definition,
            SyntaxRole.Extensions);
    }

    // Associates a property or an indexer with the accessors it calls.
    private TNode AnnotateAccessors<TNode>(TNode node, IEnumerable<MethodDefinition?> accessors)
        where TNode : SyntaxNode =>
        node.WithAdditionalAnnotations(accessors.OfType<MethodDefinition>().Select(a => _context.Annotations.GetAnnotation(a, SyntaxRole.Property)));

    private static bool IsDefaultMember(InterfaceDefinition definition, IGrouping<string, MethodDefinition> group) =>
        group.Any(static m => m.MemberId == DispIdValue)
        || (group.Key == "Item" && !definition.Methods.Any(static m => m.IsProperty && m.MemberId == DispIdValue && m.Parameters.Count > 1));

    private static HashSet<string> GetMethodNamesInHierarchy(InterfaceDefinition definition)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var current = definition; current is not null; current = (current.BaseInterface as UserDefinedTypeReference)?.Definition as InterfaceDefinition)
        {
            foreach (var method in current.Methods)
            {
                names.Add(SignatureBuilder.GetMethodName(method));
            }
        }

        if (definition.BaseInterface is not null)
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

        var index = signatures.MapParameters(getter.Parameters.Take(getter.Parameters.Count - 1));
        if (index.Any(static p => p.IsByRef))
        {
            return null;
        }

        var type = signatures.Mapper.WithMarshalling(getter.RetvalParameter!.Marshalling).MapValue(retvalType);
        return type.IsVoid || type.IsPointer ? null : new PropertySignature(type.Name, index);
    }

    private static PropertySignature? AnalyzeSetter(MethodDefinition setter, SignatureBuilder signatures)
    {
        if (!setter.ReturnsHResult || setter.RetvalParameter is not null || setter.Parameters.Count == 0)
        {
            return null;
        }

        var parameters = signatures.MapParameters(setter.Parameters);
        if (parameters.Any(static p => p.IsByRef) || parameters[^1].TypeName.EndsWith('*'))
        {
            return null;
        }

        return new PropertySignature(parameters[^1].TypeName, parameters.Take(parameters.Count - 1).ToList());
    }

    private static string Arguments(IReadOnlyList<SignatureParameter> index, string? value = null)
    {
        var arguments = index.Select(static p => IndexParameterName(p.Definition.Name)).ToList();
        if (value is not null)
        {
            arguments.Add(value);
        }

        return string.Join(", ", arguments);
    }

    // The implicit setter parameter and the receiver parameter cannot be shadowed by index parameters.
    private static string IndexParameterName(string name) => name is "value" or "this" ? name + "_" : Syntax.Escape(name);

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
        var parameters = index.Select(static p => p.Annotate(Syntax.Parameter(p.TypeName, IndexParameterName(p.Definition.Name).TrimStart('@'))));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping the interface '{Name}', which has no GUID")]
    private static partial void LogMissingGuid(ILogger logger, string name);

    private sealed record PropertySignature(string Type, IReadOnlyList<SignatureParameter> Index)
    {
        public bool Matches(PropertySignature other) =>
            Type == other.Type && Index.Count == other.Index.Count && Index.Zip(other.Index).All(static pair => pair.First.TypeName == pair.Second.TypeName);
    }
}
