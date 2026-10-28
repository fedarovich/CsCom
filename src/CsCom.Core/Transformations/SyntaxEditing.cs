using CsCom.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.Transformations;

/// <summary>
/// Edits the generated syntax trees, which are already formatted, keeping their layout.
/// </summary>
internal static class SyntaxEditing
{
    /// <summary>
    /// Adds an attribute list on its own line before the attribute lists and the modifiers of a declaration, below its
    /// documentation comment.
    /// </summary>
    public static TNode AddAttributeList<TNode>(TNode declaration, AttributeListSyntax attributeList)
        where TNode : MemberDeclarationSyntax
    {
        // The leading trivia (blank lines, documentation comment and indentation) moves to the attribute list, and the
        // declaration keeps the indentation: the text after the last line break, which can be part of the structured
        // trivia of the documentation comment.
        var leading = declaration.GetLeadingTrivia();
        string text = leading.ToFullString();
        string indentation = text[(text.LastIndexOf('\n') + 1)..];
        var list = attributeList.WithLeadingTrivia(leading).WithTrailingTrivia(CarriageReturnLineFeed);
        var stripped = declaration.WithLeadingTrivia(indentation.Length > 0 && string.IsNullOrWhiteSpace(indentation) ? TriviaList(Whitespace(indentation)) : TriviaList());
        return (TNode)stripped.WithAttributeLists(stripped.AttributeLists.Insert(0, list));
    }

    /// <summary>
    /// Adds an attribute list to the declarations selected in the documents, except to those that already have the attribute.
    /// </summary>
    /// <param name="documents">The documents.</param>
    /// <param name="select">Selects the declarations in the root of a document.</param>
    /// <param name="attributeList">The attribute list.</param>
    /// <param name="hasAttribute">Whether a declaration already has the attribute.</param>
    /// <param name="adding">Called for each declaration that gets the attribute.</param>
    /// <param name="matched">Whether any declaration was selected, including those that already have the attribute.</param>
    /// <returns>The documents with the attribute.</returns>
    public static IReadOnlyList<GeneratedDocument> AddAttribute(
        IReadOnlyList<GeneratedDocument> documents,
        Func<SyntaxNode, IEnumerable<MemberDeclarationSyntax>> select,
        AttributeListSyntax attributeList,
        Func<MemberDeclarationSyntax, bool> hasAttribute,
        Action<MemberDeclarationSyntax> adding,
        out bool matched)
    {
        matched = false;
        var result = new List<GeneratedDocument>(documents.Count);
        foreach (var document in documents)
        {
            var selected = select(document.Root).Distinct().ToList();
            matched |= selected.Count > 0;
            var targets = selected.Where(d => !hasAttribute(d)).ToList();
            if (targets.Count == 0)
            {
                result.Add(document);
                continue;
            }

            targets.ForEach(adding);
            result.Add(document with { Root = document.Root.ReplaceNodes(targets, (_, rewritten) => AddAttributeList(rewritten, attributeList)) });
        }

        return result;
    }

    /// <summary>
    /// Whether a declaration has an attribute, whose name is given without the <c>Attribute</c> suffix and the namespace.
    /// </summary>
    public static bool HasAttribute(MemberDeclarationSyntax declaration, string @namespace, string name) =>
        declaration.AttributeLists.SelectMany(static l => l.Attributes).Any(attribute =>
        {
            string text = attribute.Name.ToString();
            text = text.StartsWith("global::", StringComparison.Ordinal) ? text["global::".Length..] : text;
            text = text.StartsWith(@namespace + ".", StringComparison.Ordinal) ? text[(@namespace.Length + 1)..] : text;
            return text == name || text == name + "Attribute";
        });
}
