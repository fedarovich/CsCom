using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsCom.CodeGeneration;

/// <summary>
/// The layout of the support code files, which <c>SupportCodeProvider</c> can adapt to a generated project. The file is
/// also compiled into CsCom.SupportCode, whose task checks the files when CsCom.Core is built.
/// </summary>
internal static class SupportCodeLayout
{
    /// <summary>The namespace of the support code, which is renamed in generated projects.</summary>
    public const string RuntimeNamespace = "CsCom.Runtime";

    /// <summary>The prefix of the logical names of the embedded files of CsCom.Runtime.</summary>
    public const string RuntimePrefix = "CsCom.Runtime/";

    /// <summary>The prefix of the logical names of the embedded files of CsCom.Runtime.Dynamic.</summary>
    public const string DynamicPrefix = "CsCom.Runtime.Dynamic/";

    /// <summary>Checks the layout of a support code file.</summary>
    /// <exception cref="InvalidOperationException">The file is not laid out like a support code file.</exception>
    public static void Check(string fileName, CompilationUnitSyntax root)
    {
        if (FindViolation(fileName, root) is { } violation)
        {
            throw new InvalidOperationException(violation.Message);
        }
    }

    // The single-file mode merges the files into one namespace, with the using directives that the rewriter moves into the
    // namespace of each file, and the rewriter renames that namespace: anything else at the top of a file would be lost
    // there, e.g. a directive of a block-scoped namespace, or keep the namespace of CsCom.Runtime. A using directive in the
    // namespace would be looked up in other enclosing namespaces once it is renamed, and its names can be relative to
    // CsCom.Runtime, so that they cannot be qualified either. A preprocessor directive is rejected anywhere: in front of a
    // member, the single-file mode would apply it to the files that follow too, e.g. #nullable disable or a #pragma, one
    // at the end of a file would be lost with the end of the file, e.g. its #endif, and one among the using directives
    // would be moved or dropped with them. The code that a directive excludes is not parsed either, so the rewriter would
    // not adapt it, e.g. keep it public in the internal mode, and SupportCodeQualifier would not qualify it. The namespace
    // is renamed too, so the file must not name it: CsCom.Runtime.X, also in a using directive, which the rewriter
    // qualifies with global::, fails to compile in a generated project, or names a type of the package CsCom.Runtime in
    // one that references it too, e.g. through an interop library of its own. The types of the support code need no such
    // name: they are all in its namespace (see FindNameOfTheNamespace). The other conflicts that the move and the merge
    // of the using directives can cause are not checked, see docs/known-issues.md. The task of CsCom.SupportCode checks
    // the files when CsCom.Core is built, before it qualifies their names, so that a file that breaks the rule fails the
    // build. SupportCodeProvider checks them again when it first emits them, which costs a walk of the trees that it
    // parses anyway, and checks the files that it adapts for the tests.
    /// <summary>
    /// Finds the first part of a support code file that breaks its layout, with the message of the violation.
    /// </summary>
    /// <returns>
    /// The message and the node of the violation, the root for the layout of the file; <see langword="null"/> if none.
    /// </returns>
    public static (string Message, SyntaxNode Node)? FindViolation(string fileName, CompilationUnitSyntax root)
    {
        // First, since a directive can also exclude the parts of the layout.
        if (root.GetFirstDirective() is { } directive)
        {
            return (
                $"The support code file {fileName} has the preprocessor directive '{directive}' at line {LineOf(directive)}: the single-file mode merges the support code into one namespace, where a directive would apply to the files that follow, or be lost at the end of its file, and the code that a directive excludes would not be adapted.",
                directive);
        }

        if (root is not
            {
                Externs.Count: 0,
                AttributeLists.Count: 0,
                Members: [FileScopedNamespaceDeclarationSyntax { Name: var name, Externs.Count: 0, Usings.Count: 0 }],
            }
            || name.ToString() != RuntimeNamespace
            || root.Usings.Any(static u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
        {
            return (
                $"The support code file {fileName} must consist of using directives that are not global, at the top of the file, and of the file-scoped namespace {RuntimeNamespace}, which holds its types and no using directives or extern aliases.",
                root);
        }

        if (FindNameOfTheNamespace(root, name) is { } reference)
        {
            return (
                $"The support code file {fileName} names its namespace with '{reference}' at line {LineOf(reference)}: {RuntimeNamespace} is renamed in the generated projects, so the support code must name its own types without it, and cannot import them with using directives.",
                reference);
        }

        return null;
    }

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    // The first name of the file that names its namespace, apart from its declaration, also in the cref attributes of the
    // documentation comments. CsCom does wherever it is looked up from where it is: as the qualifier of CsCom.Runtime,
    // after global::, or alone, e.g. in using CsCom;, but not after a dot, as the name of an argument or a member, or as
    // the member of a qualified cref. Runtime does as a qualifier, e.g. Runtime.ComBstr, since the namespace finds it in
    // its enclosing namespace CsCom: alone, it can only name the namespace in nameof(Runtime), whose text is the same, so a
    // member called Runtime is only rejected as a qualifier, which this.Runtime avoids. FindViolation rejects preprocessor
    // directives first, so no code is excluded from the parse. A string holds no names: the support code names its
    // namespace in none.
    private static SyntaxNode? FindNameOfTheNamespace(CompilationUnitSyntax root, NameSyntax declaration)
    {
        foreach (var name in root.DescendantNodes(node => node != declaration, descendIntoTrivia: true).OfType<IdentifierNameSyntax>())
        {
            bool qualifier = name.Parent switch
            {
                QualifiedNameSyntax qualified => qualified.Left == name,
                MemberAccessExpressionSyntax access => access.IsKind(SyntaxKind.SimpleMemberAccessExpression) && access.Expression == name,
                QualifiedCrefSyntax cref => cref.Container == name,
                _ => false,
            };

            bool namesNamespace = name.Identifier.ValueText switch
            {
                "CsCom" => qualifier || name.Parent is not (QualifiedNameSyntax or MemberAccessExpressionSyntax or MemberBindingExpressionSyntax
                    or NameColonSyntax or NameEqualsSyntax or XmlNameAttributeSyntax or NameMemberCrefSyntax { Parent: QualifiedCrefSyntax }),
                "Runtime" => qualifier,
                _ => false,
            };

            if (namesNamespace)
            {
                // The name that it starts, e.g. CsCom.Runtime or global::CsCom, for the message.
                return name.Parent is QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax or QualifiedCrefSyntax ? name.Parent : name;
            }
        }

        return null;
    }
}
