using System.Globalization;
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
            members.Add(ParseMember($"public static readonly global::System.Guid Iid = new({Syntax.GuidLiteral(guid)});")
                .WithDocumentation($"The interface identifier (IID) of the {definition.OriginalName} dispinterface."));
        }

        members.Add(ParseMember($"private {Syntax.Escape(definition.Name)}({support}.IDispatch dispatch) : base(dispatch) {{ }}"));
        members.Add(ParseMember($"public static {self} Create({support}.IDispatch dispatch) => new(dispatch);")
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

        // _NewEnum is exposed via GetEnumerator.
        foreach (var group in definition.Methods.Where(static m => m.IsProperty && m.MemberId != DispIdNewEnum).GroupBy(static m => m.Name))
        {
            EmitProperty(group, signatures, members, ref hasIndexer);
        }

        NameIndexer(definition, members);

        object? newEnum = (object?)definition.Methods.FirstOrDefault(static m => m.MemberId == DispIdNewEnum)
            ?? definition.Properties.FirstOrDefault(static p => p.DispId == DispIdNewEnum);
        if (newEnum is not null && !memberNames.Contains("GetEnumerator"))
        {
            members.Add(_context.Annotations.Annotate(
                ParseMember($"public {support}.ComEnumerator GetEnumerator() => GetEnumeratorCore();")
                    .WithDocumentation("Returns an enumerator that iterates through the collection, so that it can be used in foreach loops."),
                newEnum,
                SyntaxRole.Enumerator));
        }

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
            string call = $"InvokeMethod({string.Join(", ", [id, .. parameters.Select(Argument)])})";
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

        string invoke = $"InvokeMethodByRef({id}, __args, [{string.Join(", ", parameters.Select(static p => p.IsByRef ? "true" : "false"))}])";
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

    private void EmitProperty(IGrouping<string, MethodDefinition> group, SignatureBuilder signatures, List<MemberDeclarationSyntax> members, ref bool hasIndexer)
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
            string index = string.Concat(property.Index.Select(static p => ", " + IndexArgument(p)));
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
                var parameters = property.Index.Select(static p => p.Annotate(Syntax.Parameter(p.TypeName, IndexParameterName(p.Definition.Name).TrimStart('@'))));
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

    private static string IndexArgument(SignatureParameter parameter) => IndexParameterName(parameter.Definition.Name);

    private static string IndexParameterName(string name) => name is "value" ? name + "_" : Syntax.Escape(name);

    private static string DispId(int dispId) => dispId.ToString(CultureInfo.InvariantCulture);

    private static MemberDeclarationSyntax ParseMember(string text)
    {
        var member = ParseMemberDeclaration(text, options: Syntax.ParseOptions);
        if (member is null || member.ContainsDiagnostics)
        {
            throw new InvalidOperationException($"Invalid member generated: {text}");
        }

        return member;
    }

    private sealed record PropertySignature(LateBoundType Type, IReadOnlyList<SignatureParameter> Index)
    {
        public bool Matches(PropertySignature other) =>
            Type.Name == other.Type.Name && Index.Count == other.Index.Count && Index.Zip(other.Index).All(static pair => pair.First.TypeName == pair.Second.TypeName);
    }
}
