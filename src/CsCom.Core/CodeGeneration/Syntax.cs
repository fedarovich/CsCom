using System.Globalization;
using System.Security;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Helpers to build syntax nodes.
/// </summary>
internal static class Syntax
{
    public const string InteropNamespace = "global::System.Runtime.InteropServices";
    public const string MarshallingNamespace = "global::System.Runtime.InteropServices.Marshalling";

    public static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    public static string Escape(string name) => NeedsEscaping(name) ? "@" + name : name;

    public static SyntaxToken Identifier(string name) =>
        NeedsEscaping(name) ? SyntaxFactory.Identifier(default, SyntaxKind.IdentifierToken, "@" + name, name, default) : SyntaxFactory.Identifier(name);

    public static TypeSyntax Type(string text) => ParseTypeName(text, options: ParseOptions);

    public static ExpressionSyntax Expression(string text)
    {
        var expression = ParseExpression(text, options: ParseOptions);
        if (expression.ContainsDiagnostics)
        {
            throw new InvalidOperationException($"Invalid expression generated: {text}");
        }

        return expression;
    }

    public static StatementSyntax Statement(string text)
    {
        var statement = ParseStatement(text, options: ParseOptions);
        if (statement.ContainsDiagnostics)
        {
            throw new InvalidOperationException($"Invalid statement generated: {text}");
        }

        return statement;
    }

    /// <summary>
    /// Creates an attribute. When <paramref name="arguments"/> is <see langword="null"/>, <paramref name="name"/> can include the argument list, e.g. <c>Foo(1, 2)</c>.
    /// </summary>
    public static AttributeSyntax Attribute(string name, string? arguments = null)
    {
        int parenthesis = name.IndexOf('(', StringComparison.Ordinal);
        if (arguments is null && parenthesis > 0 && name.EndsWith(')'))
        {
            arguments = name[(parenthesis + 1)..^1];
            name = name[..parenthesis];
        }

        var attribute = SyntaxFactory.Attribute(ParseName(name));
        if (arguments is not null)
        {
            attribute = attribute.WithArgumentList(ParseAttributeArgumentList($"({arguments})", options: ParseOptions));
        }

        return attribute;
    }

    public static AttributeListSyntax AttributeList(AttributeSyntax attribute) => SyntaxFactory.AttributeList(SingletonSeparatedList(attribute));

    public static AttributeListSyntax ReturnAttributeList(AttributeSyntax attribute) =>
        SyntaxFactory.AttributeList(AttributeTargetSpecifier(Token(SyntaxKind.ReturnKeyword)), SingletonSeparatedList(attribute));

    public static SyntaxTokenList Modifiers(params SyntaxKind[] kinds) => TokenList(kinds.Select(Token));

    public static ArrowExpressionClauseSyntax Arrow(string expression) => ArrowExpressionClause(Expression(expression));

    public static ParameterListSyntax Parameters(IEnumerable<ParameterSyntax> parameters) => ParameterList(SeparatedList(parameters));

    public static BracketedParameterListSyntax BracketedParameters(IEnumerable<ParameterSyntax> parameters) => BracketedParameterList(SeparatedList(parameters));

    public static ParameterSyntax Parameter(string type, string name) => SyntaxFactory.Parameter(Identifier(name)).WithType(Type(type));

    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    public static string GuidLiteral(Guid guid) => Literal(guid.ToString("D").ToUpperInvariant());

    public static string IntegerLiteral(long value) =>
        value is >= int.MinValue and <= int.MaxValue
            ? value.ToString(CultureInfo.InvariantCulture)
            : value is > int.MaxValue and <= uint.MaxValue
                ? $"unchecked((int)0x{value:X8})"
                : $"unchecked((int){value.ToString(CultureInfo.InvariantCulture)}L)";

    /// <summary>
    /// Adds an XML documentation comment with the specified summary to the node.
    /// </summary>
    public static T WithDocumentation<T>(this T node, string? summary, params (string Name, string Text)[] extra) where T : SyntaxNode
    {
        if (string.IsNullOrWhiteSpace(summary) && extra.Length == 0)
        {
            return node;
        }

        // NormalizeWhitespace does not change the line breaks inside documentation comments.
        const string eol = SyntaxFormatter.EndOfLine;
        var builder = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            builder.Append("/// <summary>").Append(eol);
            foreach (string line in summary.Trim().Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
            {
                builder.Append("/// ").Append(SecurityElement.Escape(line.Trim())).Append(eol);
            }

            builder.Append("/// </summary>").Append(eol);
        }

        foreach (var (element, text) in extra)
        {
            int space = element.IndexOf(' ', StringComparison.Ordinal);
            string name = space < 0 ? element : element[..space];
            builder.Append("/// <").Append(element).Append('>').Append(SecurityElement.Escape(text)).Append("</").Append(name).Append('>').Append(eol);
        }

        return node.WithLeadingTrivia(node.GetLeadingTrivia().AddRange(ParseLeadingTrivia(builder.ToString())));
    }

    private static bool NeedsEscaping(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None || name == "field";
}
