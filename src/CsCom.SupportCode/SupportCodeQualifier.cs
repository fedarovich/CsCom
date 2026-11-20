using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.SupportCode;

/// <summary>
/// Qualifies the names of .NET in the support code with <c>global::</c>, and calls the extension methods through their
/// classes, so that the types of its namespace in a generated project, e.g. a <c>Task</c> of the application or of a
/// library, cannot hide them: C# finds the types of a namespace before its using directives, and looks the extension
/// methods up in the namespace before the namespaces that the directives import.
/// </summary>
/// <remarks>
/// <see cref="SupportCodeBuilder"/> qualifies the support code when CsCom.Core is built, which embeds the qualified
/// copies. The names are bound with Roslyn as C# 14, the version of the support code and the lowest of the generated
/// projects, whose later versions only add to what a call can bind to, e.g. conversions that make another overload
/// applicable. They are bound against the compile references of the net10.0 build of CsCom.Runtime: the reference
/// assemblies of .NET 10, the lowest .NET of the generated projects, whose later versions only add to them, so that a
/// project for .NET 11 calls the extension methods of .NET 10 (see docs/known-issues.md). The support code has no
/// preprocessor directives, and the code of the COM generators is missing, which only makes errors that do not change
/// what the names bind to: the others are returned, and fail the build. A name that does not bind to a type or
/// namespace of .NET stays as it is, and so does an alias. An extension method is called through its class where it is
/// called through a receiver, <c>receiver.Method(…)</c>; the support code uses them no other way, e.g. after <c>?.</c>,
/// as a method group or in <c>foreach</c>, which <c>SupportCodeQualificationTests</c> checks.
/// </remarks>
internal static class SupportCodeQualifier
{
    /// <summary>The version of C# that the support code is bound as, that of the projects for .NET 10.</summary>
    public const LanguageVersion BindingLanguageVersion = LanguageVersion.CSharp14;

    /// <summary>
    /// Qualifies the names of .NET in support code files, which are bound together and with the files that they use.
    /// </summary>
    /// <param name="roots">The roots of the files, in the namespace of CsCom.Runtime, as parsed.</param>
    /// <param name="context">
    /// The roots of the files that they use, which are not qualified, e.g. those of CsCom.Runtime for
    /// CsCom.Runtime.Dynamic: only the bodies that the qualification walks are bound.
    /// </param>
    /// <param name="references">The assemblies that the files are bound against.</param>
    /// <returns>
    /// The texts of the qualified files, in the same order, and the errors of the files, without those of the missing
    /// code of the COM generators: CS8795 of the <c>[LibraryImport]</c> methods without implementations, and CS0165 and
    /// CS0177 of the out arguments of their calls, which count as unassigned then.
    /// </returns>
    public static QualifiedFiles Qualify(
        IReadOnlyList<CompilationUnitSyntax> roots, IReadOnlyList<CompilationUnitSyntax> context, IEnumerable<MetadataReference> references)
    {
        // The trees of the roots with C# 14, whose roots are copies that the model binds.
        var trees = roots.Concat(context)
            .Select(static r => r.SyntaxTree.WithRootAndOptions(r, ((CSharpParseOptions)r.SyntaxTree.Options).WithLanguageVersion(BindingLanguageVersion)))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "CsComSupportCode",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // The diagnostics bind the whole tree, whose symbols the rewriter finds bound then. The rewritten tree is only
        // printed.
        var texts = new List<string>(roots.Count);
        var errors = new List<Diagnostic>();
        for (int i = 0; i < roots.Count; i++)
        {
            var model = compilation.GetSemanticModel(trees[i]);
            errors.AddRange(model.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error && d.Id is not ("CS8795" or "CS0165" or "CS0177")));
            texts.Add(new Rewriter(model).Visit(trees[i].GetRoot()).ToFullString());
        }

        return new QualifiedFiles(texts, errors);
    }

    // Whether a name is the first of a qualified name or member access, or stands alone: the names after a dot are found
    // in the one before it, also the member of a qualified cref, and a name that is qualified with an alias already, e.g.
    // global::System, keeps it.
    private static bool IsFirstName(SimpleNameSyntax name) => name.Parent switch
    {
        QualifiedNameSyntax qualified => qualified.Left == name,
        MemberAccessExpressionSyntax access => access.Expression == name,
        QualifiedCrefSyntax cref => cref.Container == name,
        NameMemberCrefSyntax { Parent: QualifiedCrefSyntax } => false,
        AliasQualifiedNameSyntax or MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax => false,
        _ => true,
    };

    // The symbol that a node binds to, or its single candidate, e.g. when the missing generated code makes an overload
    // fail: a node that is ambiguous between symbols is left alone. The name of an attribute finds its constructor, which
    // stands for its type here.
    private static ISymbol? GetSymbol(SemanticModel model, SyntaxNode node)
    {
        var info = model.GetSymbolInfo(node);
        var symbols = (info.Symbol is { } bound ? [bound] : info.CandidateSymbols)
            .Select(s => s is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor && node.Parent is AttributeSyntax ? constructor.ContainingType : s)
            .Distinct<ISymbol>(SymbolEqualityComparer.Default)
            .ToList();
        return symbols is [var symbol] ? symbol : null;
    }

    // The qualifier of a name of .NET: a top-level public type of an assembly of .NET, also of an attribute, or a
    // namespace that only those assemblies declare, e.g. System. The support code cannot use the internal types of an
    // assembly that .NET runs, which the tests bind against, and which the reference assemblies of the build do not
    // declare: a name that only finds one is left alone. A nested type that a name finds is a member of an enclosing or
    // base type, which is found before the types of the namespace (the support code has no static imports, see
    // docs/known-issues.md), and var, nint, nuint and dynamic are keywords where they find a type. An
    // alias stands for its target under a name of its own, which the namespace of the target need not declare, or may
    // declare as another type, e.g. Task for ValueTask: it stays as it is. SupportCodeProvider qualifies its target and
    // moves it into the namespace of the support code, where a type around the namespace cannot hide it, and one in the
    // namespace conflicts with it (CS0576).
    private static string? GetPrefix(SemanticModel model, SimpleNameSyntax name)
    {
        if (name.Identifier.ValueText is "nint" or "nuint" or "dynamic" or "unmanaged" or "notnull"
            || name is IdentifierNameSyntax identifier && (identifier.IsVar || model.GetAliasInfo(identifier) is not null))
        {
            return null;
        }

        return GetSymbol(model, name) switch
        {
            INamedTypeSymbol { ContainingType: null, TypeKind: not TypeKind.Error, IsNativeIntegerType: false, DeclaredAccessibility: Accessibility.Public } type
                when type.ContainingAssembly is not ISourceAssemblySymbol =>
                type.ContainingNamespace.IsGlobalNamespace ? "global::" : $"global::{type.ContainingNamespace.ToDisplayString()}.",
            INamespaceSymbol { IsGlobalNamespace: false } @namespace when @namespace.Locations.All(static l => l.IsInMetadata) => "global::",
            _ => null,
        };
    }

    // The class through which an invocation calls a classic extension method through a receiver, receiver.Method(…), and
    // the modifier of the receiver as its first argument: the qualified name of a class of .NET, or the name of a class
    // of the support code, which no type of its namespace can hide, and whose namespace is renamed. A call after ?. is no
    // member access, and is left alone like the members of extension blocks.
    private static (string Type, string Modifier)? GetExtensionClass(SemanticModel model, InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } access
            || StartsWithBinding(access.Expression)
            || GetSymbol(model, invocation) is not IMethodSymbol
            {
                MethodKind: MethodKind.ReducedExtension,
                ReducedFrom: { ContainingType: { IsStatic: true, IsExtension: false } type } definition,
            })
        {
            return null;
        }

        string modifier = definition.Parameters[0].RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.RefReadOnlyParameter => "in ",
            _ => string.Empty,
        };
        return (type.ContainingAssembly is ISourceAssemblySymbol ? type.Name : type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), modifier);
    }

    // Whether an expression is the rest of a chain after ?., which starts with the member that it binds.
    private static bool StartsWithBinding(ExpressionSyntax expression) => expression switch
    {
        MemberBindingExpressionSyntax or ElementBindingExpressionSyntax => true,
        MemberAccessExpressionSyntax access => StartsWithBinding(access.Expression),
        InvocationExpressionSyntax invocation => StartsWithBinding(invocation.Expression),
        ElementAccessExpressionSyntax element => StartsWithBinding(element.Expression),
        PostfixUnaryExpressionSyntax postfix => StartsWithBinding(postfix.Operand),
        _ => false,
    };

    // Visits the original nodes, which the model has bound, and builds the qualified ones from the visited children. The
    // using directives are left to SupportCodeProvider, which qualifies them itself; the documentation comments are
    // visited for their crefs.
    private sealed class Rewriter(SemanticModel model) : CSharpSyntaxRewriter(visitIntoStructuredTrivia: true)
    {
        private readonly CSharpParseOptions _options = (CSharpParseOptions)model.SyntaxTree.Options;

        public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node) => node;

        // The qualifier becomes part of the identifier: the tree is only printed, and so a generic name keeps its type
        // arguments as written, also the braces of a cref, which parsing the qualified name would not accept.
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) =>
            IsFirstName(node) && GetPrefix(model, node) is { } prefix
                ? node.WithIdentifier(Qualify(node.Identifier, prefix))
                : base.VisitIdentifierName(node);

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            var visited = (GenericNameSyntax)base.VisitGenericName(node)!;
            return IsFirstName(node) && GetPrefix(model, node) is { } prefix ? visited.WithIdentifier(Qualify(visited.Identifier, prefix)) : visited;
        }

        // An expression of an interpolation that changed is put in parentheses: a colon outside of them ends it, which the
        // :: of global:: would.
        public override SyntaxNode? VisitInterpolation(InterpolationSyntax node)
        {
            var visited = (InterpolationSyntax)base.VisitInterpolation(node)!;
            return node.Expression is ParenthesizedExpressionSyntax || visited.Expression.ToFullString() == node.Expression.ToFullString()
                ? visited
                : visited.WithExpression(ParenthesizedExpression(visited.Expression.WithoutTrivia()).WithTriviaFrom(visited.Expression));
        }

        // receiver.Method(arguments) becomes Class.Method(receiver, arguments), with the visited receiver and arguments,
        // e.g. the inner call of a chain, which is rewritten first. The receiver is evaluated first as before, and the
        // written type arguments are those of the method of the class.
        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
            if (GetExtensionClass(model, node) is not { } extension || visited.Expression is not MemberAccessExpressionSyntax access)
            {
                return visited;
            }

            var arguments = visited.ArgumentList.Arguments;
            string separator = arguments.Count == 0 ? string.Empty : ", ";
            string call = $"{extension.Type}.{access.Name.ToFullString().Trim()}({extension.Modifier}{access.Expression}{separator}{arguments.ToFullString()})";
            return ParseExpression(call, options: _options).WithTriviaFrom(visited);
        }

        private static SyntaxToken Qualify(SyntaxToken identifier, string prefix) =>
            Identifier(identifier.LeadingTrivia, prefix + identifier.Text, identifier.TrailingTrivia);
    }
}

/// <summary>The qualified support code files, and the errors of the files.</summary>
/// <param name="Texts">The texts of the qualified files.</param>
/// <param name="Errors">The errors of the files, apart from those of the missing code of the COM generators.</param>
internal sealed record QualifiedFiles(IReadOnlyList<string> Texts, IReadOnlyList<Diagnostic> Errors);
