using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Provides the sources of CsCom.Runtime, adapted to be emitted into a generated project.
/// </summary>
internal static class SupportCodeProvider
{
    private const string ResourcePrefix = "CsCom.Runtime/";
    private const string RuntimeNamespace = "CsCom.Runtime";

    /// <summary>
    /// Gets the support code files with the namespace and, in the internal mode, the accessibility adapted to the options.
    /// </summary>
    public static IReadOnlyList<(string FileName, CompilationUnitSyntax Root)> GetFiles(CodeGenerationOptions options)
    {
        var assembly = typeof(SupportCodeProvider).Assembly;
        var rewriter = new Rewriter(options);
        var files = new List<(string, CompilationUnitSyntax)>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(static n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);

            // The line breaks of the embedded sources depend on how they were checked out.
            string text = reader.ReadToEnd().ReplaceLineEndings(SyntaxFormatter.EndOfLine);
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, Syntax.ParseOptions).GetRoot();
            files.Add((resource[ResourcePrefix.Length..], (CompilationUnitSyntax)rewriter.Visit(root)));
        }

        return files;
    }

    private sealed class Rewriter(CodeGenerationOptions options) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
            return visited.Name.ToString() == RuntimeNamespace
                ? visited.WithName(ParseName(options.SupportNamespace).WithTriviaFrom(visited.Name))
                : visited;
        }

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) => MakeInternal(node, base.VisitClassDeclaration(node));

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) => MakeInternal(node, base.VisitStructDeclaration(node));

        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => MakeInternal(node, base.VisitInterfaceDeclaration(node));

        public override SyntaxNode? VisitEnumDeclaration(EnumDeclarationSyntax node) => MakeInternal(node, base.VisitEnumDeclaration(node));

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) => MakeInternal(node, base.VisitRecordDeclaration(node));

        // Only the top-level types are made internal; their members keep their accessibility.
        private SyntaxNode? MakeInternal(SyntaxNode original, SyntaxNode? node)
        {
            if (options.SupportCode != SupportCodeMode.Internal
                || original.Parent is not (BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
                || node is not BaseTypeDeclarationSyntax type)
            {
                return node;
            }

            var modifiers = type.Modifiers;
            int index = modifiers.IndexOf(SyntaxKind.PublicKeyword);
            return index < 0
                ? node
                : type.WithModifiers(modifiers.Replace(modifiers[index], Token(SyntaxKind.InternalKeyword).WithTriviaFrom(modifiers[index])));
        }
    }
}
