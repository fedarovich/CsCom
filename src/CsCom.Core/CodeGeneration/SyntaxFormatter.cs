using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Formats the generated syntax trees.
/// </summary>
internal static class SyntaxFormatter
{
    public const string EndOfLine = "\r\n";

    public static CompilationUnitSyntax Format(CompilationUnitSyntax root, string header)
    {
        var normalized = root.NormalizeWhitespace(indentation: "    ", eol: EndOfLine);
        var spaced = (CompilationUnitSyntax)new FormattingRewriter().Visit(normalized)!;
        return AddHeader(spaced, header);
    }

    public static CompilationUnitSyntax AddHeader(CompilationUnitSyntax root, string header)
    {
        string text = header.ReplaceLineEndings(EndOfLine);
        return root.WithLeadingTrivia(ParseLeadingTrivia(text).AddRange(root.GetLeadingTrivia()));
    }

    /// <summary>
    /// Fixes the output of NormalizeWhitespace: separates members with blank lines and removes the spaces around
    /// the equal signs of XML documentation attributes.
    /// </summary>
    private sealed class FormattingRewriter() : CSharpSyntaxRewriter(visitIntoStructuredTrivia: true)
    {
        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            return visited.WithMembers(Separate(visited.Members, includeFirst: visited.Usings.Count > 0));
        }

        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
            return visited.WithMembers(Separate(visited.Members, includeFirst: true));
        }

        public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;
            return visited.WithMembers(Separate(visited.Members, includeFirst: visited.Usings.Count > 0));
        }

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            return visited.WithMembers(Separate(visited.Members, includeFirst: false));
        }

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        {
            var visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node)!;
            return visited.WithMembers(Separate(visited.Members, includeFirst: false));
        }

        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        {
            var visited = (InterfaceDeclarationSyntax)base.VisitInterfaceDeclaration(node)!;
            return visited.WithMembers(Separate(visited.Members, includeFirst: false));
        }

        public override SyntaxNode? VisitExtensionBlockDeclaration(ExtensionBlockDeclarationSyntax node)
        {
            var visited = (ExtensionBlockDeclarationSyntax)base.VisitExtensionBlockDeclaration(node)!;
            return visited.WithMembers(Separate(visited.Members, includeFirst: false));
        }

        public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node)
        {
            var visited = (EnumDeclarationSyntax)base.VisitEnumDeclaration(node)!;
            var members = visited.Members;
            for (int i = 1; i < members.Count; i++)
            {
                if ((members[i].HasStructuredTrivia || members[i - 1].HasStructuredTrivia) && !HasBlankLineBefore(members[i]))
                {
                    members = members.Replace(members[i], AddBlankLine(members[i]));
                }
            }

            return visited.WithMembers(members);
        }


        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var visited = base.VisitToken(token);
            var next = token.GetNextToken();

            // NormalizeWhitespace writes "string? []" and "(string? )x" for nullable types not followed by a name.
            if (token.IsKind(SyntaxKind.QuestionToken) && token.Parent is NullableTypeSyntax && !IsNameLike(next))
            {
                return visited.WithTrailingTrivia();
            }

            // NormalizeWhitespace writes "[a, b ]" for collection expressions.
            if (next.IsKind(SyntaxKind.CloseBracketToken) && next.Parent is CollectionExpressionSyntax)
            {
                return visited.WithTrailingTrivia();
            }

            // NormalizeWhitespace writes "[, ]" for the rank specifiers of multidimensional arrays.
            if (token.IsKind(SyntaxKind.CommaToken) && token.Parent is ArrayRankSpecifierSyntax)
            {
                return visited.WithTrailingTrivia();
            }

            return visited;
        }

        private static bool IsNameLike(SyntaxToken token) =>
            token.IsKind(SyntaxKind.IdentifierToken) || token.IsKind(SyntaxKind.ThisKeyword) || token.IsKind(SyntaxKind.OperatorKeyword) || SyntaxFacts.IsKeywordKind(token.Kind()) && token.Parent is not TypeSyntax;

        public override SyntaxNode? VisitXmlTextAttribute(XmlTextAttributeSyntax node)
        {
            var visited = (XmlTextAttributeSyntax)base.VisitXmlTextAttribute(node)!;
            return visited.WithName(visited.Name.WithoutTrailingTrivia()).WithEqualsToken(visited.EqualsToken.WithoutTrivia());
        }

        public override SyntaxNode? VisitXmlNameAttribute(XmlNameAttributeSyntax node)
        {
            var visited = (XmlNameAttributeSyntax)base.VisitXmlNameAttribute(node)!;
            return visited.WithName(visited.Name.WithoutTrailingTrivia()).WithEqualsToken(visited.EqualsToken.WithoutTrivia());
        }

        public override SyntaxNode? VisitXmlCrefAttribute(XmlCrefAttributeSyntax node)
        {
            var visited = (XmlCrefAttributeSyntax)base.VisitXmlCrefAttribute(node)!;
            return visited.WithName(visited.Name.WithoutTrailingTrivia()).WithEqualsToken(visited.EqualsToken.WithoutTrivia());
        }

        private static SyntaxList<T> Separate<T>(SyntaxList<T> members, bool includeFirst) where T : SyntaxNode
        {
            var result = new List<T>(members.Count);
            for (int i = 0; i < members.Count; i++)
            {
                bool needed = (i > 0 || includeFirst) && !HasBlankLineBefore(members[i]);
                result.Add(needed ? AddBlankLine(members[i]) : members[i]);
            }

            return List(result);
        }

        private static bool HasBlankLineBefore(SyntaxNode node)
        {
            var previous = node.GetFirstToken().GetPreviousToken();
            int lines = previous.TrailingTrivia.Count(static t => t.IsKind(SyntaxKind.EndOfLineTrivia));
            foreach (var trivia in node.GetLeadingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.EndOfLineTrivia))
                {
                    lines++;
                }
                else if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    break;
                }
            }

            return lines >= 2;
        }

        private static T AddBlankLine<T>(T node) where T : SyntaxNode =>
            node.WithLeadingTrivia(node.GetLeadingTrivia().Insert(0, CarriageReturnLineFeed));
    }
}
