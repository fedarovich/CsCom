using System.Globalization;
using System.Text;
using CsCom.CodeGeneration;
using CsCom.SupportCode;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsCom.Tests;

/// <summary>
/// Tests of the names of .NET in the support code that the <c>public</c> and <c>internal</c> modes emit.
/// </summary>
[TestClass]
public sealed class SupportCodeQualificationTests
{
    private const string SupportNamespace = "Interop.CsComTest.ComSupport";

    [TestMethod]
    public void TypesInTheSupportNamespaceDoNotHideTheTypesOfDotNet()
    {
        // C# finds the types of a namespace before its using directives: a type of the application or of a library in the
        // namespace of the support code, e.g. Task, Action or Exception, hides the type of .NET that the support code uses
        // unless it names it with global::. Every name of the emitted support code that starts with an upper-case letter
        // and is no type of its own becomes such a type here, whatever the support code qualifies, also the names of
        // members, which member lookup finds first, and the names of attributes with their suffix.
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public, Dynamic = true };
        var trees = SupportCodeProvider.GetFiles(options)
            .Select(static f => CSharpSyntaxTree.ParseText(f.Root.ToFullString(), Syntax.ParseOptions, f.FileName))
            .ToList();
        // The names of the namespace declarations are no references.
        var nodes = trees.SelectMany(static t => t.GetRoot().DescendantNodes(static n => n is not NameSyntax { Parent: BaseNamespaceDeclarationSyntax }, descendIntoTrivia: true))
            .Where(static n => n is not NameSyntax { Parent: BaseNamespaceDeclarationSyntax })
            .ToList();
        var declared = nodes.OfType<BaseTypeDeclarationSyntax>().Select(static d => d.Identifier.ValueText)
            .Concat(nodes.OfType<DelegateDeclarationSyntax>().Select(static d => d.Identifier.ValueText))
            .ToHashSet(StringComparer.Ordinal);
        var hostile = new SortedSet<(string Name, int Arity)>();
        foreach (var name in nodes.OfType<SimpleNameSyntax>())
        {
            string text = name.Identifier.ValueText;
            if (text is "" or "System" || !char.IsUpper(text[0]) || declared.Contains(text))
            {
                continue;
            }

            int arity = name is GenericNameSyntax generic ? generic.Arity : 0;
            hostile.Add((text, arity));
            if (name.Parent is AttributeSyntax || name.Parent is QualifiedNameSyntax { Parent: AttributeSyntax } qualified && qualified.Right == name)
            {
                hostile.Add((text + "Attribute", arity));
            }
        }

        // The test is only as good as the names that it hides.
        CollectionAssert.IsSubsetOf(
            new[] { ("Exception", 0), ("Marshal", 0), ("Guid", 0), ("List", 1), ("GeneratedComInterfaceAttribute", 0) },
            hostile.ToArray());

        var source = new StringBuilder();
        source.AppendLine($"namespace {SupportNamespace}").AppendLine("{");
        foreach (var (name, arity) in hostile)
        {
            string parameters = arity == 0 ? string.Empty : $"<{string.Join(", ", Enumerable.Range(1, arity).Select(static i => $"THostile{i}"))}>";
            source.AppendLine($"    internal sealed class {name}{parameters} {{ }}");
        }

        source.AppendLine("}").AppendLine($"namespace {SupportNamespace}.System").AppendLine("{");
        source.AppendLine("    internal static class Placeholder { }").AppendLine("}");
        var hostileTree = CSharpSyntaxTree.ParseText(source.ToString(), Syntax.ParseOptions, "Hostile.cs");

        // The support code compiles without the code of the COM generators, whose missing parts are errors that do not
        // change what the names bind to.
        var compilation = CSharpCompilation.Create(
            "HostileSupportCode",
            [.. trees, hostileTree],
            GetFrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        // A type or namespace that only the hostile file declares; the global namespace and that of the support code are
        // declared by every file.
        bool IsHostile(ISymbol? symbol) =>
            symbol is INamedTypeSymbol or INamespaceSymbol { IsGlobalNamespace: false }
            && symbol.DeclaringSyntaxReferences is { Length: > 0 } declarations
            && declarations.All(r => r.SyntaxTree == hostileTree);

        var hidden = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in nodes.OfType<SimpleNameSyntax>().Where(n => n.SyntaxTree == tree))
            {
                var info = model.GetSymbolInfo(name);
                foreach (var symbol in info.CandidateSymbols.Prepend(info.Symbol))
                {
                    var target = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType : symbol;
                    if (IsHostile(target))
                    {
                        int line = name.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        hidden.Add($"{tree.FilePath}({line}): {name.Parent}");
                        break;
                    }
                }
            }
        }

        Assert.IsEmpty(hidden, $"{hidden.Count} names find a type of the namespace:{Environment.NewLine}{string.Join(Environment.NewLine, hidden.Take(40))}");
    }

    [TestMethod]
    public void TheQualifiedSupportCodeCompiles()
    {
        // A qualifier must not break the code around it, e.g. in the expression of an interpolation, which a colon ends
        // unless it is in parentheses, or in a cref that is qualified already. The documentation comments are checked too.
        // Only the code of the generators is missing, which the integration tests build with: the [LibraryImport] methods
        // have no implementations (CS8795), so the out arguments of their calls count as unassigned (CS0165, CS0177). It
        // compiles as C# 14, as in a project for .NET 10 or for several target frameworks.
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public, Dynamic = true };
        var parseOptions = Syntax.ParseOptions.WithLanguageVersion(LanguageVersion.CSharp14).WithDocumentationMode(DocumentationMode.Diagnose);
        var trees = SupportCodeProvider.GetFiles(options)
            .Select(f => CSharpSyntaxTree.ParseText(f.Root.ToFullString(), parseOptions, f.FileName))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "QualifiedSupportCode",
            trees,
            GetFrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var errors = compilation.GetDiagnostics()
            .Where(static d => d.Severity == DiagnosticSeverity.Error || d.Id is "CS1574" or "CS1580" or "CS1584")
            .Where(static d => d.Id is not ("CS8795" or "CS0165" or "CS0177"))
            .Select(static d => d.ToString())
            .ToList();
        Assert.IsEmpty(errors, string.Join(Environment.NewLine, errors.Take(40)));
    }

    [TestMethod]
    public void NamesThatBindThroughAnAliasKeepIt()
    {
        // An alias stands for its target under a name of its own, which the namespace of the target does not declare, or
        // declares as another type: Task would become the Task of .NET rather than ValueTask. SupportCodeProvider qualifies
        // the target and moves the alias into the namespace of the support code, where no type around it can hide it.
        const string Source = """
            using System;
            using IO = System.IO;
            using Guids = System.Collections.Generic.List<System.Guid>;
            using Pair = (System.Guid Id, int Count);
            using Task = System.Threading.Tasks.ValueTask;

            namespace CsCom.Runtime;

            /// <summary>Uses <see cref="Guids"/> and <see cref="IO.File"/>.</summary>
            public static class Aliases
            {
                public static Guids Create() => new Guids { Guid.Empty };

                public static Pair First(Guids guids) => (guids[0], guids.Count);

                public static bool Exists(string path) => IO.File.Exists(path);

                public static Task Done() => default;

                public static string Name => nameof(Guids);
            }
            """;
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(Source, Syntax.ParseOptions).GetRoot();
        var qualified = SupportCodeQualifier.Qualify([root], [], GetFrameworkReferences());
        string text = qualified.Texts[0];

        Assert.IsEmpty(qualified.Errors);
        Assert.AreEqual(Source.Replace("{ Guid.Empty }", "{ global::System.Guid.Empty }", StringComparison.Ordinal), text);
        var compilation = CSharpCompilation.Create(
            "Aliases",
            [CSharpSyntaxTree.ParseText(text, Syntax.ParseOptions)],
            GetFrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(static d => d.Severity == DiagnosticSeverity.Error).Select(static d => d.ToString()).ToList();
        Assert.IsEmpty(errors, string.Join(Environment.NewLine, errors));
    }

    [TestMethod]
    [DataRow(NamedIndexers.ClassProperty, EventStyle.Events)]
    [DataRow(NamedIndexers.StructMethod, EventStyle.Observable)]
    public void ExtensionMethodsAreCalledThroughTheirClasses(NamedIndexers namedIndexers, EventStyle eventStyle)
    {
        // An extension method is looked up from the scopes of its call, where a static class of the namespace of the
        // support code or of a library, e.g. one of the application, can have a better one than the one of .NET that the
        // using directives import. The generated code and the support code call them through their classes, which the
        // names of .NET qualify, and use none implicitly, e.g. in foreach, where nothing could name the class.
        var pipeline = new InteropPipeline();
        var options = new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SupportCode = SupportCodeMode.Public,
            Dynamic = true,
            OverloadsForDefaultValues = true,
            NamedIndexers = namedIndexers,
            EventStyle = eventStyle,
        };
        var trees = pipeline.GenerateCode(pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary), options)
            .Select(static d => CSharpSyntaxTree.ParseText(d.GetText(), Syntax.ParseOptions, d.RelativePath))
            .ToList();
        var compilation = CSharpCompilation.Create(
            "ExtensionCalls",
            trees,
            GetFrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var calls = new List<string>();
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                bool throughScopes = node switch
                {
                    SimpleNameSyntax name => IsExtensionThroughScopes(model, name),
                    CommonForEachStatementSyntax loop => IsExtension(model.GetForEachStatementInfo(loop).GetEnumeratorMethod)
                        || loop is ForEachVariableStatementSyntax variables && IsExtension(model.GetDeconstructionInfo(variables).Method),
                    AwaitExpressionSyntax await => IsExtension(model.GetAwaitExpressionInfo(await).GetAwaiterMethod),
                    InitializerExpressionSyntax { RawKind: (int)SyntaxKind.CollectionInitializerExpression } initializer =>
                        initializer.Expressions.Any(e => IsExtension(model.GetCollectionInitializerSymbolInfo(e).Symbol)),
                    AssignmentExpressionSyntax { Left: TupleExpressionSyntax or DeclarationExpressionSyntax } assignment =>
                        IsExtension(model.GetDeconstructionInfo(assignment).Method),
                    QueryClauseSyntax clause => IsExtension(model.GetQueryClauseInfo(clause).OperationInfo.Symbol),
                    _ => false,
                };
                if (throughScopes)
                {
                    int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    calls.Add($"{tree.FilePath}({line}): {node.Parent}");
                }
            }
        }

        Assert.IsEmpty(calls, $"{calls.Count} uses of extension methods through the scopes:{Environment.NewLine}{string.Join(Environment.NewLine, calls.Take(40))}");

        static bool IsExtension(ISymbol? symbol) =>
            symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension } or IMethodSymbol { IsExtensionMethod: true }
            || symbol?.ContainingType is { IsExtension: true };

        // A classic extension method found through a receiver, also a method group or after ?., or a member of an extension
        // block that is not named through the static class that declares the block.
        static bool IsExtensionThroughScopes(SemanticModel model, SimpleNameSyntax name)
        {
            var info = model.GetSymbolInfo(name);
            if ((info.Symbol ?? (info.CandidateSymbols is [var single] ? single : null)) is not { } symbol)
            {
                return false;
            }

            // The parameters and locals of the members of an extension block are no extension members.
            return symbol is IMethodSymbol { MethodKind: MethodKind.ReducedExtension }
                || symbol is IMethodSymbol or IPropertySymbol
                    && symbol.ContainingType is { IsExtension: true, ContainingType: var declaring }
                    && !(name.Parent is MemberAccessExpressionSyntax access && access.Name == name
                        && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, declaring));
        }
    }

    [TestMethod]
    public void TheSupportCodeIsNotBuiltWhenItDoesNotBind()
    {
        // The task of CsCom.SupportCode fails the build of CsCom.Core rather than embed names that it could not qualify,
        // e.g. without the references, or in a file that breaks the layout, whose excluded code it could not bind.
        string directory = TestEnvironment.CreateDirectory("support-code");
        string source = Path.Combine(directory, "Sample.cs");
        File.WriteAllText(source, """
            using System;

            namespace CsCom.Runtime;

            public static class Sample
            {
                public static Guid Empty => Guid.Empty;
            }
            """);
        string output = Path.Combine(directory, "Qualified");
        (string, string)[] sources = [(source, "CsCom.Runtime/Sample.cs")];

        var unbound = SupportCodeBuilder.Build(sources, [], output);
        Assert.IsTrue(unbound.Any(e => e.File == source && e.Line > 0), string.Join(Environment.NewLine, unbound));
        Assert.IsFalse(unbound.Any(static e => e.Code == SupportCodeBuilder.LayoutErrorCode));
        Assert.IsFalse(Directory.Exists(output));

        Assert.IsEmpty(SupportCodeBuilder.Build(sources, GetFrameworkAssemblies(), output));
        StringAssert.Contains(File.ReadAllText(Path.Combine(output, "CsCom.Runtime", "Sample.cs")), "public static global::System.Guid Empty => global::System.Guid.Empty;");

        File.WriteAllText(source, "#nullable disable" + Environment.NewLine + File.ReadAllText(source));
        var error = SupportCodeBuilder.Build(sources, GetFrameworkAssemblies(), output).Single();
        Assert.AreEqual((source, 1, SupportCodeBuilder.LayoutErrorCode), (error.File, error.Line, error.Code));
    }

    [TestMethod]
    public void TheSupportCodeIsBoundAsTheLowestVersionOfCSharp()
    {
        // The support code is C# 14, the lowest version of the generated projects, whose later versions only add
        // conversions: a method that C# 14 calls is still applicable there.
        Assert.IsTrue(LanguageVersionFacts.TryParse(CodeGenerationOptions.MinimumLanguageVersion.ToString(CultureInfo.InvariantCulture), out var minimum));
        Assert.AreEqual(SupportCodeQualifier.BindingLanguageVersion, minimum);
    }

    // The assemblies of the running .NET, without the other assemblies of the tests, e.g. CsCom.Runtime, whose types would
    // be found twice.
    private static List<string> GetFrameworkAssemblies()
    {
        string framework = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(f => string.Equals(Path.GetDirectoryName(f), framework, StringComparison.OrdinalIgnoreCase))];
    }

    private static List<MetadataReference> GetFrameworkReferences() =>
        [.. GetFrameworkAssemblies().Select(static f => MetadataReference.CreateFromFile(f))];
}
