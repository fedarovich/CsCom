using System.Globalization;
using System.Runtime.InteropServices;
using CsCom.Model;
using CsCom.Transformations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Emits late-bound wrappers for dispinterfaces, which can only be invoked via <c>IDispatch::Invoke</c>.
/// </summary>
internal sealed class DispatchWrapperEmitter
{
    private const int DispIdValue = 0;
    private const int DispIdNewEnum = -4;

    private readonly EmitContext _context;

    public DispatchWrapperEmitter(EmitContext context)
    {
        _context = context;
    }

    public ClassDeclarationSyntax Emit(InterfaceDefinition definition, string @namespace)
    {
        var mapper = new TypeMapper(_context, @namespace);
        var signatures = new SignatureBuilder(_context, mapper, @namespace);
        string self = _context.GetTypeName(definition, @namespace);
        string support = _context.Support;

        var members = new List<MemberDeclarationSyntax>();
        if (definition.Guid is { } guid)
        {
            members.Add(Syntax.Member($"public static readonly global::System.Guid Iid = new({Syntax.GuidLiteral(guid)});")
                .WithDocumentation($"The interface identifier (IID) of the {definition.OriginalName} dispinterface."));
        }

        members.Add(Syntax.Member($"private {Syntax.Escape(definition.Name)}({support}.IDispatch dispatch) : base(dispatch) {{ }}"));
        members.Add(Syntax.Member($"public static {self} Create({support}.IDispatch dispatch) => new(dispatch);")
            .WithDocumentation("Creates a late-bound wrapper for the specified IDispatch interface.", ("param name=\"dispatch\"", "The IDispatch interface of the object.")));

        foreach (var property in definition.Properties.Where(static p => p.DispId != DispIdNewEnum))
        {
            var type = mapper.WithMarshalling(property.Marshalling).MapLateBound(property.Type);
            string id = DispId(property.DispId);
            string get = type.FromObject($"InvokeGet({id})");
            string? set = property.IsReadOnly ? null : $"InvokePut({id}, value)";
            members.Add(_context.Annotations.Annotate(Property(type.Name, property.Name, get, set, property.Documentation), property));
        }

        bool hasIndexer = false;
        var memberNames = new HashSet<string>(definition.Methods.Select(SignatureBuilder.GetMethodName).Concat(definition.Properties.Select(static p => p.Name)), StringComparer.Ordinal);
        foreach (var method in definition.Methods.Where(static m => !m.IsProperty))
        {
            members.Add(Method(method, signatures));
            if (_context.Options.OverloadsForDefaultValues)
            {
                var (returnType, parameters) = signatures.MapLateBoundSignature(method);
                string name = SignatureBuilder.GetMethodName(method);
                members.AddRange(signatures.GetDefaultOverloads(method, parameters, lateBound: true).Select(overload => _context.Annotations.Annotate(
                    SignatureBuilder.BuildOverload(overload, returnType.Name, name, Syntax.Escape(name), SyntaxKind.PublicKeyword).WithDocumentation(method.Documentation),
                    method,
                    SyntaxRole.Overload)));
            }
        }

        // _NewEnum is exposed via GetEnumerator. The parameterized properties that are not the indexer can get proxies.
        var proxyTypes = new List<MemberDeclarationSyntax>();
        var takenNames = new HashSet<string>(memberNames.Append(definition.Name).Concat(definition.Methods.Select(static m => m.Name)), StringComparer.Ordinal);
        foreach (var group in definition.Methods.Where(static m => m.IsProperty && m.MemberId != DispIdNewEnum).GroupBy(static m => m.Name))
        {
            if (!EmitProperty(group, signatures, members, ref hasIndexer)
                && _context.Options.NamedIndexers != NamedIndexers.None
                && !memberNames.Contains(group.Key)
                && EmitNamedIndexer(group, signatures, definition.Name, self, takenNames) is { } proxy)
            {
                members.Add(proxy.Member);
                proxyTypes.Add(proxy.Type);
            }
        }

        NameIndexer(definition, members);

        object? newEnum = (object?)definition.Methods.FirstOrDefault(static m => m.MemberId == DispIdNewEnum)
            ?? definition.Properties.FirstOrDefault(static p => p.DispId == DispIdNewEnum);
        if (newEnum is not null && !memberNames.Contains("GetEnumerator"))
        {
            members.Add(_context.Annotations.Annotate(
                Syntax.Member($"public {support}.ComEnumerator GetEnumerator() => GetEnumeratorCore();")
                    .WithDocumentation("Returns an enumerator that iterates through the collection, so that it can be used in foreach loops."),
                newEnum,
                SyntaxRole.Enumerator));
        }

        members.AddRange(proxyTypes);
        return _context.Annotations.Annotate(
            ClassDeclaration(Syntax.Identifier(definition.Name))
                .WithModifiers(Syntax.Modifiers(_context.Accessibility, SyntaxKind.SealedKeyword, SyntaxKind.PartialKeyword))
                .WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute(
                    $"{Syntax.MarshallingNamespace}.NativeMarshalling",
                    $"typeof({support}.DispatchWrapperMarshaller<{self}>)"))))
                .WithBaseList(BaseList(SeparatedList<BaseTypeSyntax>(
                [
                    SimpleBaseType(Syntax.Type($"{support}.DispatchObject")),
                    SimpleBaseType(Syntax.Type($"{support}.IDispatchWrapper<{self}>")),
                ])))
                .WithMembers(List(members))
                .WithDocumentation(definition.Documentation ?? $"A late-bound wrapper of the {definition.OriginalName} dispinterface."),
            definition);
    }

    private MemberDeclarationSyntax Method(MethodDefinition method, SignatureBuilder signatures)
    {
        var (returnType, parameters) = signatures.MapLateBoundSignature(method);
        string id = DispId(method.MemberId);

        var declaration = MethodDeclaration(Syntax.Type(returnType.Name), Syntax.Identifier(SignatureBuilder.GetMethodName(method)))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
            .WithParameterList(Syntax.Parameters(parameters.Select(static p => p.ToSyntax(includeMarshalling: false))));

        if (!parameters.Any(static p => p.IsByRef))
        {
            // The accessors of properties that are not properties or the indexer of the wrapper are invoked with the flags of
            // properties, since objects reject DISPATCH_METHOD for them. The value of a setter is passed first.
            string call = method.InvokeKind switch
            {
                InvokeKind.PropertyGet => $"InvokeGet({string.Join(", ", [id, .. Arguments(parameters)])})",
                InvokeKind.PropertyPut or InvokeKind.PropertyPutRef when parameters.Count > 0 =>
                    $"{(method.InvokeKind == InvokeKind.PropertyPut ? "InvokePut" : "InvokePutRef")}({string.Join(", ", [id, Argument(parameters[^1]), .. Arguments(parameters[..^1])])})",
                _ => $"InvokeMethod({string.Join(", ", [id, .. Arguments(parameters)])})",
            };
            return _context.Annotations.Annotate(
                declaration
                    .WithExpressionBody(Syntax.Arrow(returnType.IsVoid ? call : returnType.FromObject(call)))
                    .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                    .WithDocumentation(method.Documentation),
                method);
        }

        var statements = new List<StatementSyntax>
        {
            Syntax.Statement($"object?[] __args = [{string.Join(", ", parameters.Select(static p => p.Modifier == SyntaxKind.OutKeyword ? "null" : Argument(p)))}];"),
        };

        // Accessors are invoked with the flags of properties too. The arguments keep the declaration order, in which the value of
        // a setter is the last one: the runtime passes it as the DISPID_PROPERTYPUT named argument. Dispatch is qualified, since
        // a parameter can have its name. The references have the types of the parameters, which the objects that implement
        // IDispatch::Invoke with their type information require: they reject a reference to a VARIANT for a long*.
        string byRef = $"[{string.Join(", ", parameters.Select(static p => ReferenceType(p)))}]";
        string invoke = PropertyFlags(method.InvokeKind) is { } flags
            ? $"InvokeByRef(this.Dispatch, {id}, {flags}, __args, {byRef})"
            : $"InvokeMethodByRef({id}, __args, {byRef})";
        statements.Add(Syntax.Statement(returnType.IsVoid ? $"{invoke};" : $"object? __result = {invoke};"));

        var lateBoundTypes = parameters.Select(p => signatures.Mapper.WithMarshalling(p.Definition.Marshalling).MapLateBound(p.ValueType)).ToList();
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].IsByRef)
            {
                statements.Add(Syntax.Statement($"{parameters[i].Name} = {lateBoundTypes[i].FromObject($"__args[{i}]")};"));
            }
        }

        if (!returnType.IsVoid)
        {
            statements.Add(Syntax.Statement($"return {returnType.FromObject("__result")};"));
        }

        return _context.Annotations.Annotate(declaration.WithBody(Block(statements)).WithDocumentation(method.Documentation), method);
    }

    // The flags of the invocation of a property accessor, the same as InvokeGet, InvokePut and InvokePutRef use, or null for a method.
    private string? PropertyFlags(InvokeKind kind)
    {
        string flags = $"{_context.Support}.DispatchFlags";
        return kind switch
        {
            InvokeKind.PropertyGet => $"{flags}.PropertyGet | {flags}.Method",
            InvokeKind.PropertyPut => $"{flags}.PropertyPut",
            InvokeKind.PropertyPutRef => $"{flags}.PropertyPutRef",
            _ => null,
        };
    }

    // Emits a property or the indexer, and the accessors that they do not cover as methods. Returns whether a property or the
    // indexer was emitted.
    private bool EmitProperty(IGrouping<string, MethodDefinition> group, SignatureBuilder signatures, List<MemberDeclarationSyntax> members, ref bool hasIndexer)
    {
        var getter = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyGet);
        var put = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPut);
        var putRef = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPutRef);
        var setter = put ?? putRef;

        // Accessors that cannot be represented as a property or an indexer are emitted as methods.
        var getterSignature = getter is null ? null : AnalyzeGetter(getter, signatures);
        var setterSignature = setter is null ? null : AnalyzeSetter(setter, signatures);
        if (getterSignature is not null && setterSignature is not null && !getterSignature.Matches(setterSignature))
        {
            setterSignature = null;
        }

        var property = getterSignature ?? setterSignature;
        string? documentation = (getter ?? setter)?.Documentation;
        bool emitted = false;
        if (property is not null)
        {
            int dispId = (getter ?? setter)!.MemberId;
            string id = DispId(dispId);
            string[] indexNames = SignatureParameter.GetIndexNames(property.Index);
            string index = string.Concat(Arguments(property.Index, indexNames.Select(Syntax.Escape).ToList()).Select(static a => ", " + a));
            string? get = getterSignature is null ? null : property.Type.FromObject($"InvokeGet({id}{index})");
            string? set = setterSignature is null ? null : $"{(setter == put ? "InvokePut" : "InvokePutRef")}({id}, value{index})";
            var accessors = new[] { getterSignature is null ? null : getter, setterSignature is null ? null : setter }
                .OfType<MethodDefinition>()
                .Select(a => _context.Annotations.GetAnnotation(a, SyntaxRole.Property))
                .ToArray();

            if (property.Index.Count == 0)
            {
                members.Add(Property(property.Type.Name, group.Key, get, set, documentation).WithAdditionalAnnotations(accessors));
                emitted = true;
            }
            else if (!hasIndexer && dispId == DispIdValue)
            {
                var parameters = property.Index.Select((p, i) => p.Annotate(Syntax.Parameter(p.TypeName, indexNames[i])));
                var indexer = IndexerDeclaration(Syntax.Type(property.Type.Name))
                    .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
                    .WithParameterList(Syntax.BracketedParameters(parameters));
                indexer = set is null
                    ? indexer.WithExpressionBody(Syntax.Arrow(get!)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                    : indexer.WithAccessorList(InterfaceEmitter.Accessors(get, set));
                members.Add(indexer.WithDocumentation(documentation).WithAdditionalAnnotations(accessors));
                hasIndexer = true;
                emitted = true;
            }
        }

        foreach (var accessor in group)
        {
            bool covered = emitted && (accessor == getter && getterSignature is not null || accessor == setter && setterSignature is not null);
            if (!covered)
            {
                members.Add(Method(accessor, signatures));
            }
        }

        return emitted;
    }

    // The proxy of a parameterized property that is not the indexer, whose indexer calls the accessor methods emitted by
    // EmitProperty. It has their signatures, so optional parameters keep their default values, which the methods replace with
    // Missing.Value.
    private (MemberDeclarationSyntax Member, TypeDeclarationSyntax Type)? EmitNamedIndexer(
        IGrouping<string, MethodDefinition> group, SignatureBuilder signatures, string owner, string self, ISet<string> takenNames)
    {
        var getter = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyGet);
        var setter = group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPut) ?? group.FirstOrDefault(static m => m.InvokeKind == InvokeKind.PropertyPutRef);

        LateBoundType? valueType = null;
        List<SignatureParameter>? index = null;
        if (getter is not null && signatures.MapLateBoundSignature(getter) is var (returnType, getterParameters)
            && !returnType.IsVoid && getterParameters.Count > 0 && !getterParameters.Any(static p => p.IsByRef))
        {
            valueType = returnType;
            index = getterParameters;
        }
        else
        {
            getter = null;
        }

        if (setter is not null && signatures.MapLateBoundSignature(setter).Parameters is var setterParameters
            && setterParameters.Count > 1 && !setterParameters.Any(static p => p.IsByRef)
            && (index is null || (setterParameters[^1].TypeName == valueType!.Name && SignatureParameter.HaveSameTypes(index, setterParameters[..^1]))))
        {
            index ??= setterParameters[..^1];
            valueType ??= new LateBoundType(setterParameters[^1].TypeName, static x => x);
        }
        else
        {
            setter = null;
        }

        if (index is null)
        {
            return null;
        }

        return NamedIndexerEmitter.Create(
            _context,
            group.Key,
            NamedIndexerEmitter.GetTypeName(group.Key, takenNames),
            owner,
            self,
            "this",
            valueType!.Name,
            index,
            getter is null ? null : SignatureBuilder.GetMethodName(getter),
            setter is null ? null : SignatureBuilder.GetMethodName(setter),
            (getter ?? setter)!.Documentation,
            [.. new[] { getter, setter }.OfType<MethodDefinition>()]);
    }

    // An indexer is compiled into a property named Item, which conflicts with the members named Item, get_Item or set_Item,
    // e.g. the Item method next to the _Default indexer of Excel's collections. The indexer is then named after the default
    // member, unless its accessors are methods too, as for Excel's Range.
    private static void NameIndexer(InterfaceDefinition definition, List<MemberDeclarationSyntax> members)
    {
        int index = members.FindIndex(static m => m is IndexerDeclarationSyntax);
        if (index < 0)
        {
            return;
        }

        var names = members.Select(static m => m switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            _ => null,
        }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        bool Conflicts(string name) => names.Contains(name) || names.Contains("get_" + name) || names.Contains("set_" + name);
        if (!Conflicts("Item"))
        {
            return;
        }

        string defaultMember = definition.Methods.First(static m => m.IsProperty && m.MemberId == DispIdValue).Name;
        string name = new[] { defaultMember, defaultMember + "Indexer", "DefaultIndexer" }.First(n => n != "Item" && !Conflicts(n));
        var attribute = Syntax.AttributeList(Syntax.Attribute("global::System.Runtime.CompilerServices.IndexerName", Syntax.Literal(name)));
        members[index] = SyntaxEditing.AddAttributeList((IndexerDeclarationSyntax)members[index], attribute);
    }

    private static PropertySignature? AnalyzeGetter(MethodDefinition getter, SignatureBuilder signatures)
    {
        bool hasRetval = SignatureBuilder.HasRetval(getter, out _);
        if (!hasRetval && getter.ReturnsHResult)
        {
            return null;
        }

        var index = signatures.MapLateBoundParameters(hasRetval ? getter.Parameters.Take(getter.Parameters.Count - 1) : getter.Parameters);
        if (index.Any(static p => p.IsByRef || p.DefaultValue is not null))
        {
            return null;
        }

        var type = signatures.MapLateBoundGetterType(getter);
        return type.IsVoid ? null : new PropertySignature(type, index);
    }

    private static PropertySignature? AnalyzeSetter(MethodDefinition setter, SignatureBuilder signatures)
    {
        if (setter.Parameters.Count == 0 || setter.RetvalParameter is not null)
        {
            return null;
        }

        var parameters = signatures.MapLateBoundParameters(setter.Parameters);
        if (parameters.Any(static p => p.IsByRef))
        {
            return null;
        }

        var value = signatures.Mapper.WithMarshalling(parameters[^1].Definition.Marshalling).MapLateBound(parameters[^1].ValueType);
        return new PropertySignature(value, parameters.Take(parameters.Count - 1).ToList());
    }

    private static PropertyDeclarationSyntax Property(string type, string name, string? get, string? set, string? documentation)
    {
        var property = PropertyDeclaration(Syntax.Type(type), Syntax.Identifier(name))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword));

        property = set is null
            ? property.WithExpressionBody(Syntax.Arrow(get!)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            : property.WithAccessorList(InterfaceEmitter.Accessors(get, set));

        return property.WithDocumentation(documentation);
    }

    private static string Argument(SignatureParameter parameter) =>
        parameter.IsOptionalVariant ? $"{parameter.Name} ?? global::System.Reflection.Missing.Value" : parameter.Name;

    // The arguments passed to the params parameter of InvokeMethod, InvokeGet, InvokePut and InvokePutRef. A single array is
    // passed as an object, since C# would take an array of reference types for the list of the arguments itself.
    private static IEnumerable<string> Arguments(IReadOnlyList<SignatureParameter> parameters, IReadOnlyList<string>? expressions = null)
    {
        expressions ??= [.. parameters.Select(Argument)];
        return parameters.Count == 1 && parameters[0].TypeName.TrimEnd('?').EndsWith(']') ? [$"(object?){expressions[0]}"] : expressions;
    }

    // How an argument is passed to InvokeMethodByRef and InvokeByRef: VT_EMPTY by value, otherwise the type that the
    // reference points to, which is VT_VARIANT for the types that have no typed references.
    private static string ReferenceType(SignatureParameter parameter)
    {
        VarEnum type = parameter.IsByRef ? GetVarType(parameter.ValueType) : VarEnum.VT_EMPTY;
        return $"{Syntax.InteropNamespace}.VarEnum.{type}";
    }

    // The variant type of a value passed by reference. SAFEARRAYs stay references to VARIANTs, in which VB and script
    // servers store SAFEARRAYs of VARIANTs, whatever the element type that the type library declares.
    private static VarEnum GetVarType(TypeReference type)
    {
        switch (type)
        {
            case PrimitiveTypeReference
            {
                VarType: VarEnum.VT_I1 or VarEnum.VT_UI1 or VarEnum.VT_I2 or VarEnum.VT_UI2 or VarEnum.VT_I4 or VarEnum.VT_UI4
                    or VarEnum.VT_I8 or VarEnum.VT_UI8 or VarEnum.VT_INT or VarEnum.VT_UINT or VarEnum.VT_R4 or VarEnum.VT_R8
                    or VarEnum.VT_CY or VarEnum.VT_DATE or VarEnum.VT_BSTR or VarEnum.VT_BOOL or VarEnum.VT_ERROR
                    or VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN
            } primitive:
                return primitive.VarType;
            case UserDefinedTypeReference { Definition: EnumDefinition }:
                return VarEnum.VT_I4;
            case UserDefinedTypeReference { Definition: AliasDefinition alias }:
                return GetVarType(alias.Target);
            case PointerTypeReference pointer when TypeMapper.IsInterfaceLike(pointer.ElementType):
                return IsDispatch(pointer.ElementType) ? VarEnum.VT_DISPATCH : VarEnum.VT_UNKNOWN;
            default:
                return VarEnum.VT_VARIANT;
        }
    }

    // Whether the pointers to the interface or the class are IDispatch pointers.
    private static bool IsDispatch(TypeReference type) => type switch
    {
        PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => true,
        UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: InterfaceKind.Dual or InterfaceKind.Dispatch } } => true,
        UserDefinedTypeReference { Definition: InterfaceDefinition { BaseInterface: { } baseInterface } } => IsDispatch(baseInterface),
        UserDefinedTypeReference { Definition: CoClassDefinition { DefaultInterface.Interface: { } defaultInterface } } => IsDispatch(defaultInterface),
        _ => false,
    };

    private static string DispId(int dispId) => dispId.ToString(CultureInfo.InvariantCulture);

    private sealed record PropertySignature(LateBoundType Type, IReadOnlyList<SignatureParameter> Index)
    {
        public bool Matches(PropertySignature other) => Type.Name == other.Type.Name && SignatureParameter.HaveSameTypes(Index, other.Index);
    }
}
