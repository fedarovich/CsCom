using CsCom.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Emits the proxies that give indexer syntax to the parameterized properties other than the indexer, with
/// <see cref="CodeGenerationOptions.NamedIndexers"/>: a member named after the property returns a proxy whose indexer calls
/// the accessor methods, e.g. <c>sheet.Range["A1"]</c> or <c>sheet.Range()["A1"]</c>.
/// </summary>
internal static class NamedIndexerEmitter
{
    // The JIT can only allocate the proxy class on the stack when the member that creates it and the members that use it are
    // inlined into the caller.
    private const string Inlining =
        "global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)";

    private const string Target = "_target";

    /// <summary>
    /// Gets a name for the proxy type of a property that is not in <paramref name="takenNames"/>, and adds it.
    /// </summary>
    public static string GetTypeName(string property, ISet<string> takenNames)
    {
        string name = property + "Indexer";
        for (int i = 2; !takenNames.Add(name); i++)
        {
            name = $"{property}Indexer{i}";
        }

        return name;
    }

    /// <summary>
    /// Creates the proxy of a parameterized property.
    /// </summary>
    /// <param name="context">The context of the emitter.</param>
    /// <param name="property">The name of the property, which the member that returns the proxy gets.</param>
    /// <param name="typeName">The name of the proxy type, from <see cref="GetTypeName"/>.</param>
    /// <param name="owner">The name of the interface, for the documentation.</param>
    /// <param name="targetType">The type of the object whose accessor methods the proxy calls.</param>
    /// <param name="receiver">The expression of that object in the member that returns the proxy, e.g. <c>@this</c>.</param>
    /// <param name="valueType">The type of the property.</param>
    /// <param name="index">The index parameters, with the default values of the optional ones.</param>
    /// <param name="getter">The name of the getter method, if the proxy has a getter.</param>
    /// <param name="setter">The name of the setter method, if the proxy has a setter.</param>
    /// <param name="documentation">The documentation of the property.</param>
    /// <param name="accessors">The accessor methods, which the nodes are associated with.</param>
    /// <returns>The member that returns the proxy, and the proxy type.</returns>
    public static (MemberDeclarationSyntax Member, TypeDeclarationSyntax Type) Create(
        EmitContext context,
        string property,
        string typeName,
        string owner,
        string targetType,
        string receiver,
        string valueType,
        IReadOnlyList<SignatureParameter> index,
        string? getter,
        string? setter,
        string? documentation,
        IReadOnlyList<MethodDefinition> accessors)
    {
        bool isClass = context.Options.NamedIndexers == NamedIndexers.ClassProperty;
        string inlining = isClass ? $"[{Inlining}] " : string.Empty;
        // The implicit setter parameter and the field cannot be shadowed by index parameters.
        string[] names = SignatureParameter.GetIndexNames(index, Target);
        var arguments = names.Select(Syntax.Escape).ToList();
        string? get = getter is null ? null : $"{Target}.{Syntax.Escape(getter)}({string.Join(", ", arguments)})";
        string? set = setter is null ? null : $"{Target}.{Syntax.Escape(setter)}({string.Join(", ", [.. arguments, "value"])})";

        var parameters = index.Select((p, i) =>
        {
            var parameter = p.Annotate(Syntax.Parameter(p.TypeName, names[i]));
            return p.DefaultValue is null ? parameter : parameter.WithDefault(EqualsValueClause(Syntax.Expression(p.DefaultValue)));
        });
        var indexer = IndexerDeclaration(Syntax.Type(valueType))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
            .WithParameterList(Syntax.BracketedParameters(parameters));
        indexer = !isClass && set is null
            ? indexer.WithExpressionBody(Syntax.Arrow(get!)).WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            : indexer.WithAccessorList(AccessorList(List(
                new[] { (Kind: SyntaxKind.GetAccessorDeclaration, Expression: get), (Kind: SyntaxKind.SetAccessorDeclaration, Expression: set) }
                    .Where(static a => a.Expression is not null)
                    .Select(a => Accessor(a.Kind, a.Expression!, isClass)))));

        string modifiers = $"{SyntaxFacts.GetText(context.Accessibility)} {(isClass ? "sealed class" : "readonly record struct")}";
        var type = ((TypeDeclarationSyntax)Syntax.Member($"{modifiers} {typeName} {{ }}"))
            .WithMembers(List<MemberDeclarationSyntax>(
            [
                Syntax.Member($"private readonly {targetType} {Target};"),
                Syntax.Member($"{inlining}internal {typeName}({targetType} target) => {Target} = target;"),
                Annotate(context, indexer.WithDocumentation(documentation), accessors, SyntaxRole.NamedIndexer),
            ]))
            .WithDocumentation($"Gives indexer syntax to the parameterized property {property} of {owner}.");

        string name = Syntax.Escape(property);
        var member = isClass
            ? Syntax.Member($"public {typeName} {name} {{ {inlining}get => new({receiver}); }}")
            : Syntax.Member($"public {typeName} {name}() => new({receiver});");

        return (
            Annotate(context, member.WithDocumentation(documentation), accessors, SyntaxRole.Property),
            Annotate(context, type, accessors, SyntaxRole.NamedIndexer));
    }

    private static AccessorDeclarationSyntax Accessor(SyntaxKind kind, string expression, bool inline)
    {
        var accessor = AccessorDeclaration(kind)
            .WithExpressionBody(Syntax.Arrow(expression))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        return inline ? accessor.WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute(Inlining)))) : accessor;
    }

    private static TNode Annotate<TNode>(EmitContext context, TNode node, IEnumerable<MethodDefinition> accessors, SyntaxRole role)
        where TNode : SyntaxNode =>
        node.WithAdditionalAnnotations(accessors.Select(a => context.Annotations.GetAnnotation(a, role)));
}
