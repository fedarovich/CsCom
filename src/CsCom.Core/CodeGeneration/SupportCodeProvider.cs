using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Provides the sources of CsCom.Runtime and, with <see cref="CodeGenerationOptions.Dynamic"/>, of CsCom.Runtime.Dynamic, adapted
/// to be emitted into a generated project. Both use the <c>CsCom.Runtime</c> namespace. The names of .NET in them are
/// qualified with <c>global::</c> when CsCom.Core is built: it embeds the copies that the <c>QualifySupportCode</c>
/// task of CsCom.SupportCode writes (see <c>SupportCodeQualifier</c> there).
/// </summary>
internal static class SupportCodeProvider
{
    // The embedded files, parsed and checked once per process: they do not change. The files of CsCom.Runtime.Dynamic are
    // only read when the support code of dynamic is emitted. The options only change what the rewriter makes of them.
    private static readonly Lazy<IReadOnlyList<(string FileName, CompilationUnitSyntax Root)>> RuntimeFiles = new(() => ReadFiles(SupportCodeLayout.RuntimePrefix));
    private static readonly Lazy<IReadOnlyList<(string FileName, CompilationUnitSyntax Root)>> DynamicFiles = new(() => ReadFiles(SupportCodeLayout.DynamicPrefix));

    /// <summary>
    /// Gets the support code files with the namespace and, in the internal mode, the accessibility adapted to the options.
    /// </summary>
    public static IReadOnlyList<(string FileName, CompilationUnitSyntax Root)> GetFiles(CodeGenerationOptions options)
    {
        var rewriter = new Rewriter(options);
        var files = options.Dynamic
            ? RuntimeFiles.Value.Concat(DynamicFiles.Value).OrderBy(static f => f.FileName, StringComparer.Ordinal)
            : RuntimeFiles.Value.AsEnumerable();
        return [.. files.Select(f => (f.FileName, (CompilationUnitSyntax)rewriter.Visit(f.Root)))];
    }

    /// <summary>
    /// Adapts the text of a support code file to the options, like <see cref="GetFiles"/>, but without qualifying its
    /// names of .NET, which CsCom.SupportCode does when CsCom.Core is built.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is not laid out like a support code file.</exception>
    internal static CompilationUnitSyntax Adapt(string fileName, string text, CodeGenerationOptions options) =>
        (CompilationUnitSyntax)new Rewriter(options).Visit(Parse(fileName, text));

    // Reads, parses and checks the embedded files of a project, in the order of their names.
    private static List<(string FileName, CompilationUnitSyntax Root)> ReadFiles(string prefix)
    {
        var assembly = typeof(SupportCodeProvider).Assembly;
        var files = new List<(string FileName, CompilationUnitSyntax Root)>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            files.Add((resource[prefix.Length..], Parse(resource, reader.ReadToEnd())));
        }

        return [.. files.OrderBy(static f => f.FileName, StringComparer.Ordinal)];
    }

    // The embedded files were checked when CsCom.Core was built, and are checked again here, before they are adapted: the
    // check costs a walk of the trees that are parsed anyway, and it checks the files that Adapt adapts for the tests (see
    // SupportCodeLayout.FindViolation).
    private static CompilationUnitSyntax Parse(string fileName, string text)
    {
        // The line breaks of the embedded sources depend on how they were checked out.
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text.ReplaceLineEndings(SyntaxFormatter.EndOfLine), Syntax.ParseOptions).GetRoot();
        SupportCodeLayout.Check(fileName, root);
        return root;
    }

    private sealed class Rewriter(CodeGenerationOptions options) : CSharpSyntaxRewriter
    {
        // The using directives move into the namespace, so that the types that they import take precedence over the types of
        // the enclosing namespaces, which hide them otherwise: e.g. the types of a library named Action, Exception or Task
        // around <Namespace>.ComSupport. The names of .NET in the code are qualified and their extension methods called
        // through their classes already (SupportCodeQualifier of CsCom.SupportCode), so the directives serve the names
        // that it could not bind, and the aliases. Their names are qualified with global::, from where they were looked up
        // at the top of the file: in a namespace, System would be looked up in the enclosing namespaces first too.
        // SupportCodeLayout has made sure that the file has a single file-scoped namespace, without using directives of
        // its own.
        public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        {
            var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
            if (visited.Usings.Count == 0)
            {
                return visited;
            }

            var @namespace = (FileScopedNamespaceDeclarationSyntax)visited.Members[0];
            var usings = visited.Usings
                .Select(static u => u.WithNamespaceOrType((TypeSyntax)GlobalQualifier.Instance.Visit(u.NamespaceOrType)!))
                .ToList();

            // A blank line between the namespace and the directives, and the trivia of the first directive, e.g. a comment
            // at the top of the file, before the namespace.
            usings[0] = usings[0].WithLeadingTrivia(EndOfLine(SyntaxFormatter.EndOfLine));
            var moved = @namespace
                .WithLeadingTrivia(visited.Usings[0].GetLeadingTrivia())
                .WithUsings(List(usings));
            var result = visited.WithUsings(default).WithMembers(SingletonList<MemberDeclarationSyntax>(moved));

            // The comments between the using directives and the namespace stay between the using directives and what
            // follows them, so that they keep their order; only the blank lines before them are dropped. They precede the
            // first token after the moved using directives, usually the first member, which the single-file mode keeps
            // too. SupportCodeLayout rejects preprocessor directives, which that mode could not keep.
            var between = @namespace.GetLeadingTrivia()
                .SkipWhile(static t => t.IsKind(SyntaxKind.WhitespaceTrivia) || t.IsKind(SyntaxKind.EndOfLineTrivia))
                .ToList();
            if (between.Count == 0)
            {
                return result;
            }

            var next = ((BaseNamespaceDeclarationSyntax)result.Members[0]).Usings[usings.Count - 1].GetLastToken().GetNextToken(includeZeroWidth: true);
            return result.ReplaceToken(next, next.WithLeadingTrivia(TriviaList(between).AddRange(next.LeadingTrivia)));
        }

        // SupportCodeLayout has made sure that it is the namespace of CsCom.Runtime.
        public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;
            return visited.WithName(ParseName(options.SupportNamespace).WithTriviaFrom(visited.Name));
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

    // Qualifies with global:: the names of the target of a using directive that are looked up from where it is: the name
    // that it starts with, and those of its type arguments, tuple elements and the like. The names after a dot are found in
    // the name before it, a name that is qualified with an alias already, e.g. global::System, keeps it, and nint and nuint
    // are no names of the global namespace. Types that are no names, e.g. int, need nothing.
    private sealed class GlobalQualifier : CSharpSyntaxRewriter
    {
        public static readonly GlobalQualifier Instance = new();

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            node.Identifier.ValueText is "nint" or "nuint" ? node : Qualify(node, base.VisitIdentifierName(node));

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node) => Qualify(node, base.VisitGenericName(node));

        // The visited node has the original one's parent.
        private static SyntaxNode? Qualify(SimpleNameSyntax original, SyntaxNode? visited)
        {
            bool first = original.Parent switch
            {
                QualifiedNameSyntax qualified => qualified.Left == original,
                AliasQualifiedNameSyntax => false,
                _ => true,
            };

            return first && visited is SimpleNameSyntax name
                ? AliasQualifiedName(IdentifierName(Token(SyntaxKind.GlobalKeyword)), name.WithoutLeadingTrivia()).WithLeadingTrivia(name.GetLeadingTrivia())
                : visited;
        }
    }
}
