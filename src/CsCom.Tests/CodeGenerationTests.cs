using System.Runtime.InteropServices;
using System.Xml.Linq;
using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Tests;

/// <summary>
/// Snapshot tests of the code generated for TestAssets/CsComTest.idl.
/// </summary>
[TestClass]
public sealed class CodeGenerationTests : VerifyBase
{
    [TestMethod]
    public Task GeneratesFilePerType()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion });

        return Verify(ToTargets(documents));
    }

    [TestMethod]
    public Task GeneratesExtensionIndexersForCSharp15()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", LanguageVersion = 15, ToolVersion = TestEnvironment.ToolVersion });

        return Verify(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "cs");
    }

    [TestMethod]
    public Task GeneratesInternalTypesWithInlineSupportCode()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SupportCode = SupportCodeMode.Internal,
            ToolVersion = TestEnvironment.ToolVersion,
        });

        // The support code is a copy of CsCom.Runtime; only its location and accessibility are checked.
        var support = documents.Where(static d => d.RelativePath.StartsWith("ComSupport/", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(support.Any(static d => d.RelativePath == "ComSupport/IDispatch.cs"));
        foreach (var document in support)
        {
            string text = document.GetText();
            StringAssert.Contains(text, "namespace Interop.CsComTest.ComSupport;");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, @"^public ", System.Text.RegularExpressions.RegexOptions.Multiline), document.RelativePath);
        }

        return Verify(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "cs");
    }

    [TestMethod]
    public Task GeneratesSingleFile()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SingleFile = true,
            SingleFileName = "Interop.CsComTest.cs",
            ToolVersion = TestEnvironment.ToolVersion,
        });

        Assert.AreEqual("Interop.CsComTest.cs", documents.Single().RelativePath);
        return Verify(documents.Single().GetText(), "cs");
    }

    [TestMethod]
    [DataRow(SupportCodeMode.Package)]
    [DataRow(SupportCodeMode.Public)]
    [DataRow(SupportCodeMode.Internal)]
    public void GeneratesTheLateBindingWithDynamicOnlyWithTheOption(SupportCodeMode mode)
    {
        var withoutDynamic = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = mode, ToolVersion = TestEnvironment.ToolVersion });
        var withDynamic = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = mode, Dynamic = true, ToolVersion = TestEnvironment.ToolVersion });

        // The activation through the extension of ComActivator in ComDynamic is called in its qualified form.
        string support = mode == SupportCodeMode.Package ? "global::CsCom.Runtime" : "global::Interop.CsComTest.ComSupport";
        Assert.IsFalse(withoutDynamic.Single(static d => d.RelativePath == "Shape.cs").GetText().Contains("CreateDynamic", StringComparison.Ordinal));
        StringAssert.Contains(
            withDynamic.Single(static d => d.RelativePath == "Shape.cs").GetText(),
            $"public static dynamic CreateDynamic({support}.ClassContext context = {support}.ComActivator.DefaultContext) => {support}.ComDynamic.CreateDynamicInstance(Clsid, context);");

        // The support code of CsCom.Runtime.Dynamic is only emitted with the option.
        Assert.IsFalse(withoutDynamic.Any(static d => d.RelativePath.EndsWith("DynamicDispatchObject.cs", StringComparison.Ordinal)));
        Assert.AreEqual(
            mode != SupportCodeMode.Package,
            withDynamic.Any(static d => d.RelativePath == "ComSupport/DynamicDispatchObject.cs"));
        Assert.AreEqual(
            mode != SupportCodeMode.Package,
            withDynamic.Any(static d => d.RelativePath == "ComSupport/DynamicEventSink.cs"));
    }

    [TestMethod]
    public void GeneratesTheSupportCodeIntoTheSingleFile()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SingleFile = true,
            SupportCode = SupportCodeMode.Public,
            ToolVersion = TestEnvironment.ToolVersion,
        });

        string text = documents.Single().GetText();
        StringAssert.Contains(text, "namespace Interop.CsComTest.ComSupport");
        StringAssert.Contains(text, "public unsafe partial interface IDispatch");
        StringAssert.Contains(text, ": global::Interop.CsComTest.ComSupport.IDispatch");
        StringAssert.Contains(text, "using global::System.Runtime.InteropServices;");
    }

    [TestMethod]
    public void TheSupportCodeTakesTheTypesOfDotNetFromTheGlobalNamespace()
    {
        // The types of the library or of the application around the namespace of the support code can have the names of the
        // types of .NET that it uses, e.g. Action, Exception or Task, and a namespace around it can be named System. So the
        // using directives are in the namespace, where they take precedence over the enclosing namespaces, and global, and
        // the qualified names start with global::System. The names of .NET in the code are qualified too, so that types in
        // the namespace cannot hide them either (SupportCodeQualificationTests). LibrarySetBuildsWithoutWarnings compiles
        // it among such types.
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SupportCode = SupportCodeMode.Public,
            Dynamic = true,
            ToolVersion = TestEnvironment.ToolVersion,
        });

        var support = documents.Where(static d => d.RelativePath.StartsWith("ComSupport/", StringComparison.Ordinal)).ToList();
        Assert.IsNotEmpty(support);
        foreach (var document in support)
        {
            var root = (Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax)Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(document.GetText()).GetRoot();
            Assert.IsEmpty(root.Usings, document.RelativePath);
            foreach (var directive in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>())
            {
                StringAssert.StartsWith(directive.Name!.ToString(), "global::", document.RelativePath);
            }

            // Also in the documentation comments, whose cref attributes are resolved like the code.
            var unqualified = root.DescendantNodes(descendIntoTrivia: true)
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>()
                .Where(static name => name.Identifier.Text == "System" && name.Parent switch
                {
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedNameSyntax qualified => qualified.Left == name,
                    Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax access => access.Expression == name,
                    Microsoft.CodeAnalysis.CSharp.Syntax.QualifiedCrefSyntax cref => cref.Container == name,
                    _ => false,
                })
                .Select(static name => name.Parent!.ToString())
                .ToList();
            Assert.IsEmpty(unqualified, $"{document.RelativePath}: {string.Join(", ", unqualified)}");
        }
    }

    [TestMethod]
    public void TheSupportCodeKeepsTheTriviaBeforeItsNamespace()
    {
        // The using directives move into the namespace; the comments between them and the namespace stay between them and
        // the members, in their order.
        const string Source = """
            // Header.
            using System;

            // Before the namespace.
            // Right before it.
            namespace CsCom.Runtime;

            public static class Sample
            {
            }
            """;
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public, Dynamic = true };
        string text = SupportCodeProvider.Adapt("Sample.cs", Source, options).ToFullString();
        Assert.IsEmpty(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetDiagnostics(), text);
        string[] parts =
        [
            "// Header.",
            "namespace Interop.CsComTest.ComSupport;",
            "using global::System;",
            "// Before the namespace.",
            "// Right before it.",
            "public static class Sample",
        ];
        int[] positions = [.. parts.Select(part => text.IndexOf(part, StringComparison.Ordinal))];
        CollectionAssert.DoesNotContain(positions, -1, text);
        CollectionAssert.AreEqual(positions.Order().ToArray(), positions, text);
    }

    [TestMethod]
    public void TheSupportCodeQualifiesTheNamesOfItsUsingDirectives()
    {
        // At the top of the file, the names of the using directives are looked up in the global namespace, which they name
        // in the namespace, also in the target of an alias; a name that is qualified with an alias already keeps it.
        const string Source = """
            using System;
            using global::System.Text;
            using static System.Math;
            using Guids = System.Collections.Generic.List<System.Guid>;
            using Pair = (System.Guid Id, int Count);
            using unsafe Pointer = int*;
            using Handle = nint;

            namespace CsCom.Runtime;

            public static class Sample
            {
            }
            """;
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public };
        var root = SupportCodeProvider.Adapt("Sample.cs", Source, options);
        string text = root.ToFullString();
        Assert.IsEmpty(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(text).GetDiagnostics(), text);
        Assert.IsEmpty(root.Usings, text);
        string[] usings = [.. root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.UsingDirectiveSyntax>().Select(static u => u.ToString())];
        CollectionAssert.AreEqual(
            new[]
            {
                "using global::System;",
                "using global::System.Text;",
                "using static global::System.Math;",
                "using Guids = global::System.Collections.Generic.List<global::System.Guid>;",
                "using Pair = (global::System.Guid Id, int Count);",
                "using unsafe Pointer = int*;",
                "using Handle = nint;",
            },
            usings,
            text);
    }

    [TestMethod]
    [DataRow("namespace CsCom.Runtime { public static class Sample { } }", DisplayName = "Block-scoped namespace")]
    [DataRow("using System; public static class Sample { }", DisplayName = "No namespace")]
    [DataRow("public static class Before { } namespace CsCom.Runtime; public static class Sample { }", DisplayName = "Type before the namespace")]
    [DataRow("global using System; namespace CsCom.Runtime; public static class Sample { }", DisplayName = "Global using directive")]
    [DataRow("[assembly: System.CLSCompliant(false)] namespace CsCom.Runtime; public static class Sample { }", DisplayName = "Assembly attribute")]
    [DataRow("extern alias Other; namespace CsCom.Runtime; public static class Sample { }", DisplayName = "Extern alias")]
    [DataRow("namespace CsCom.Runtime.Other; public static class Sample { }", DisplayName = "Other namespace")]
    [DataRow("namespace CsCom.Runtime; using System; public static class Sample { }", DisplayName = "Using directive in the namespace")]
    [DataRow("namespace CsCom.Runtime; extern alias Other; public static class Sample { }", DisplayName = "Extern alias in the namespace")]
    public void TheSupportCodeRejectsAFileThatTheSingleFileModeCannotMerge(string source)
    {
        // The single-file mode keeps only the using directives, without their trivia, and the members of the namespace of
        // each file, and a using directive in the namespace would be looked up elsewhere once the namespace is renamed;
        // GetFiles checks every file of the support code.
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => SupportCodeProvider.Adapt("Sample.cs", source, options));
        StringAssert.Contains(exception.Message, "Sample.cs");
    }

    [TestMethod]
    [DataRow("#if true\nusing System;\n#endif\nnamespace CsCom.Runtime; public static class Sample { }", "#if true", 1, DisplayName = "Before the using directives")]
    [DataRow("using System;\n#if true\nusing System.Text;\n#endif\nnamespace CsCom.Runtime; public static class Sample { }", "#if true", 2, DisplayName = "Among the using directives")]
    [DataRow("using System;\n#pragma warning disable CA1822 // A reason.\nnamespace CsCom.Runtime; public static class Sample { }", "#pragma warning disable CA1822 // A reason.", 2, DisplayName = "After the using directives")]
    [DataRow("#pragma warning disable CA1822\nnamespace CsCom.Runtime; public static class Sample { }", "#pragma warning disable CA1822", 1, DisplayName = "Before a namespace without using directives")]
    [DataRow("namespace\n#if true\nCsCom.Runtime;\n#endif\npublic static class Sample { }", "#if true", 2, DisplayName = "In the namespace declaration")]
    [DataRow("namespace CsCom.Runtime;\npublic static class Sample\n{\n#nullable disable\n}", "#nullable disable", 4, DisplayName = "In a member")]
    [DataRow("namespace CsCom.Runtime;\npublic static class Sample { }\n#pragma warning restore CA1822", "#pragma warning restore CA1822", 3, DisplayName = "At the end of the file")]
    [DataRow("namespace CsCom.Runtime;\n#if NET11_0_OR_GREATER\npublic static class Sample { }\n#endif", "#if NET11_0_OR_GREATER", 2, DisplayName = "Excluded code")]
    public void TheSupportCodeRejectsAPreprocessorDirective(string source, string directive, int line)
    {
        // The single-file mode merges the members of all files into one namespace, where a directive in front of a member
        // would apply to the files that follow too, and one at the end of a file would be lost; the code that a directive
        // excludes is not parsed, so it would not be adapted. Comments are kept, see
        // TheSupportCodeKeepsTheTriviaBeforeItsNamespace.
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => SupportCodeProvider.Adapt("Sample.cs", source, options));
        StringAssert.Contains(exception.Message, "Sample.cs");
        StringAssert.Contains(exception.Message, $"'{directive}' at line {line}:");
    }

    [TestMethod]
    [DataRow("using static CsCom.Runtime.OleNativeMethods; namespace CsCom.Runtime; public static class Sample { }", "CsCom.Runtime", 1, DisplayName = "Static using directive")]
    [DataRow("using Alias = CsCom.Runtime.Other; namespace CsCom.Runtime; public static class Sample { }", "CsCom.Runtime", 1, DisplayName = "Alias")]
    [DataRow("using Alias = System.Collections.Generic.List<CsCom.Runtime.Other>; namespace CsCom.Runtime; public static class Sample { }", "CsCom.Runtime", 1, DisplayName = "Type argument of an alias")]
    [DataRow("using CsCom.Runtime; namespace CsCom.Runtime; public static class Sample { }", "CsCom.Runtime", 1, DisplayName = "Using directive of the namespace")]
    [DataRow("using static global::CsCom.Runtime.Other; namespace CsCom.Runtime; public static class Sample { }", "global::CsCom", 1, DisplayName = "Global alias")]
    [DataRow("namespace CsCom.Runtime; public static class Sample { private static readonly CsCom.Runtime.Other? Field; }", "CsCom.Runtime", 1, DisplayName = "Type")]
    [DataRow("namespace CsCom.Runtime; public static class Sample { public static int Value => CsCom.Runtime.Other.Value; }", "CsCom.Runtime", 1, DisplayName = "Member access")]
    [DataRow("namespace CsCom.Runtime; public static class Sample { public static int Value => Runtime.Other.Value; }", "Runtime.Other", 1, DisplayName = "Member access in the enclosing namespace")]
    [DataRow("namespace CsCom.Runtime; public static class Sample { public static object Type => typeof(global::CsCom.Runtime.Other); }", "global::CsCom", 1, DisplayName = "Global alias in the code")]
    [DataRow("namespace CsCom.Runtime;\n/// <summary><see cref=\"CsCom.Runtime.Other\"/></summary>\npublic static class Sample { }", "CsCom.Runtime", 2, DisplayName = "Cref")]
    [DataRow("namespace CsCom.Runtime;\n/// <summary><see cref=\"Runtime.Other\"/></summary>\npublic static class Sample { }", "Runtime.Other", 2, DisplayName = "Cref in the enclosing namespace")]
    public void TheSupportCodeRejectsANameOfItsNamespace(string source, string name, int line)
    {
        // The namespace is renamed in the generated projects, where such a name fails to compile, or names a type of the
        // CsCom.Runtime package that the project references through another library; the rewriter qualifies the names of
        // the using directives with global::. Runtime is found in the enclosing namespace CsCom.
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public };
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => SupportCodeProvider.Adapt("Sample.cs", source, options));
        StringAssert.Contains(exception.Message, "Sample.cs");
        StringAssert.Contains(exception.Message, $"'{name}' at line {line}:");
    }

    [TestMethod]
    public void TheSupportCodeAcceptsRuntimeWhereItDoesNotNameItsNamespace()
    {
        // After a dot, Runtime is a member of what precedes it, and alone it names the namespace only in nameof, whose text
        // is the same: a member called Runtime is a member, also in an initializer and as the name of an argument.
        const string Source = """
            using System.Runtime.InteropServices;

            namespace CsCom.Runtime;

            /// <summary>Uses <see cref="System.Runtime.InteropServices.Marshal"/>.</summary>
            public sealed class Sample
            {
                public int Runtime { get; init; }

                /// <summary>Returns <paramref name="Runtime"/>.</summary>
                public static int Get(int Runtime) => Runtime;

                public Sample Copy() => new() { Runtime = this.Runtime };

                public static string Name() => nameof(Runtime);

                public static object Anonymous() => new { Runtime = Get(Runtime: 1) };
            }
            """;
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = SupportCodeMode.Public };
        string text = SupportCodeProvider.Adapt("Sample.cs", Source, options).ToFullString();
        StringAssert.Contains(text, "new() { Runtime = this.Runtime }");
    }

    [TestMethod]
    [DataRow(ValueMarshalling.None)]
    [DataRow(ValueMarshalling.Wrapper)]
    public Task GeneratesValueMarshalling(ValueMarshalling mode)
    {
        // The default Bcl marshalling is covered by GeneratesFilePerType.
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions { Dates = mode, Currencies = mode, Booleans = mode },
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IMarshalling.cs" or "IShape.cs" or "Record.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    [DataRow(BstrMarshalling.Wrapper)]
    [DataRow(BstrMarshalling.None)]
    public Task GeneratesBstrMarshalling(BstrMarshalling mode)
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions { Bstrs = mode },
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IStrings.cs" or "ICalculator.cs" or "IShape.cs" or "IShapeEventsDual.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    public void GeneratesPerParameterInterfaceMarshalling()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest" }, """
            <CsCom>
              <ModelTransformers>
                <SetMarshalling Type="^IShape$" Member="^Parent$" Interfaces="unique" />
                <SetMarshalling Type="^IShape$" Member="^Dispatch$" Parameter="^Dispatch$" Interfaces="unique" />
              </ModelTransformers>
            </CsCom>
            """);

        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        const string Dispatch = "global::CsCom.Runtime.IDispatch";
        static string Using(string marshaller, string type) =>
            $"global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::CsCom.Runtime.{marshaller}<{type}>))";

        // Without a parameter selector, the return value and the parameters of both accessors change; with it, only the
        // parameter does, and the return value keeps the marshaller of shared objects, like the other members.
        StringAssert.Contains(shape, $"[return: {Using("UniqueComObjectMarshaller", "IShape")}]\r\n    IShape? get_Parent();");
        StringAssert.Contains(shape, $"void putref_Parent([{Using("UniqueComObjectMarshaller", "IShape")}] IShape? Parent);");
        StringAssert.Contains(
            shape,
            $"[return: {Using("ComObjectMarshaller", Dispatch)}]\r\n    {Dispatch}? Dispatch([{Using("UniqueComObjectMarshaller", Dispatch)}] {Dispatch}? Dispatch);");
        StringAssert.Contains(shape, $"[return: {Using("ComObjectMarshaller", "IShape")}]\r\n    IShape? Clone();");
    }

    [TestMethod]
    public void AUniqueParameterOfAnEventIsUniqueThroughIDispatchToo()
    {
        // The marshaller of the parameter gives the handler an object of its own when the event is raised through the
        // vtable, and so does the DispatchHandler when it is raised through IDispatch::Invoke, which passes the object in a
        // VARIANT. A closed sink releases nothing: a managed source in the same process can call it directly, with its own
        // objects, which the sink cannot tell from those that the marshaller created.
        string events = Generate(new ObjectEventTransformer { Unique = true });
        const string Dispatch = "global::CsCom.Runtime.IDispatch";
        StringAssert.Contains(
            events,
            $"void Renamed([global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::CsCom.Runtime.UniqueComObjectMarshaller<{Dispatch}>))] {Dispatch}? Name);");
        StringAssert.Contains(events, "case 3:");
        StringAssert.Contains(events, $"__handler.Renamed(arguments.GetUnique<{Dispatch}>(0));");
        StringAssert.Contains(
            Flatten(events),
            $"public void Renamed({Dispatch}? Name) {{ if (!__gate.TryEnter()) {{ return; }} try {{ __handler.Renamed(Name); }} finally {{ __gate.Exit(); }} }}");
        Assert.IsFalse(events.Contains("ComMarshalling.Release(", StringComparison.Ordinal));
        Assert.IsFalse(events.Contains("FinalRelease", StringComparison.Ordinal));

        // A late-bound wrapper.
        events = Generate(new ObjectEventTransformer { Unique = true, Wrapper = true });
        StringAssert.Contains(
            Flatten(events),
            "public void Renamed(DShapeEvents? Name) { if (!__gate.TryEnter()) { return; } try { __handler.Renamed(Name); } finally { __gate.Exit(); } }");
        StringAssert.Contains(events, "__handler.Renamed(arguments.GetUniqueWrapper<DShapeEvents>(0));");
        Assert.IsFalse(events.Contains("FinalRelease", StringComparison.Ordinal));

        // An object that is passed in and out is passed back to the caller, so it is always a shared object.
        events = Generate(new ObjectEventTransformer { Unique = true, ByReference = true });
        StringAssert.Contains(
            events,
            $"void Renamed([global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::CsCom.Runtime.ComObjectMarshaller<{Dispatch}>))] ref {Dispatch}? Name);");
        StringAssert.Contains(events, $"var __a0 = arguments.Get<{Dispatch}?>(0);");
        Assert.IsFalse(events.Contains("GetUnique", StringComparison.Ordinal));
        Assert.IsFalse(events.Contains("FinalRelease", StringComparison.Ordinal));

        static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        // The generated code compiles; the test compiler does not run the LibraryImport generator, which the functions of
        // modules need.
        static string Generate(ObjectEventTransformer transformer)
        {
            var pipeline = new InteropPipeline();
            pipeline.ModelTransformers.Add(transformer);
            var library = pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary);
            library.Types.RemoveAll(static t => t is ModuleDefinition);
            string text = pipeline
                .GenerateCode(library, new CodeGenerationOptions { Namespace = "Interop.CsComTest", SingleFile = true, SingleFileName = "Test.cs" })
                .Single()
                .GetText();
            using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
            TestCompiler.Compile("UniqueEvents", text, reference: runtime);
            return text;
        }
    }

    [TestMethod]
    public void TheFieldsOfTheSinkOfAConnectionPointGiveWayToTheEvents()
    {
        // The sink implements the event interface with its names: a field named like one of its methods would clash with
        // it, and one named like a parameter would be hidden by it, so the fields take other names. The generated code
        // compiles; the test compiler does not run the LibraryImport generator, which the functions of modules need.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new SinkFieldNamesTransformer());
        var library = pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary);
        library.Types.RemoveAll(static t => t is ModuleDefinition);
        string text = pipeline
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Interop.CsComTest", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("SinkFieldNames", text, reference: runtime);

        StringAssert.Contains(text, "private readonly global::CsCom.Runtime.EventGate __gate_2;");
        StringAssert.Contains(text, "if (!__gate_2.TryEnter())");
        StringAssert.Contains(text, "__handler_2.Moved(__handler, __dispatch);");
        StringAssert.Contains(text, "global::CsCom.Runtime.DispatchEventSink.InvokeHandler(__gate_2, __dispatch_2,");
    }

    [TestMethod]
    public void TheEnumeratorIsOwnedOnlyWhenItIsUniqueAndReturnedThroughCom()
    {
        // A unique enumerator belongs to foreach when the call went through COM, so that the marshaller created it, and not
        // when a managed implementation returned an object of its own. A shared enumerator is never owned.
        const string Call = "global::CsCom.Runtime.ComEnumerator.FromObject(@this.get__NewEnum(), ownsEnumerator: (object)@this is global::System.Runtime.InteropServices.Marshalling.ComObject);";
        string shared = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest" })
            .Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(shared, "global::CsCom.Runtime.ComEnumerator.FromObject(@this.get__NewEnum());");
        Assert.IsFalse(shared.Contains("ownsEnumerator", StringComparison.Ordinal));

        string unique = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest" }, """
            <CsCom>
              <ModelTransformers>
                <SetMarshalling TypeName="IShape" MemberName="_NewEnum" Interfaces="unique" />
              </ModelTransformers>
            </CsCom>
            """).Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(unique, Call);
    }

    [TestMethod]
    [DataRow(EventStyle.Events)]
    [DataRow(EventStyle.Observable)]
    public void OnlyTheMembersOfEventsWithPointersAreUnsafe(EventStyle style)
    {
        // The sink class is not unsafe, only the handler method of an event with a pointer is: the compilation shows that
        // nothing else needs an unsafe context. A part of the user's own is not affected either way, since the modifier of
        // a partial declaration applies to its part only.
        var pipeline = new InteropPipeline();
        var library = pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary);
        var moved = library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "IShapeEventsVt").Methods.Single(static m => m.Name == "Moved");
        moved.Parameters[0].Type = new PointerTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID));
        moved.Parameters[0].Flags = ParameterFlags.In;

        // The test compiler does not run the LibraryImport generator, which the functions of modules need.
        library.Types.RemoveAll(static t => t is ModuleDefinition);
        string text = pipeline
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Interop.CsComTest", SingleFile = true, SingleFileName = "Test.cs", EventStyle = style })
            .Single()
            .GetText();

        StringAssert.Contains(text, "public sealed partial class IShapeEventsVtSink : IShapeEventsVtHandler, global::System.IDisposable, global::System.IAsyncDisposable");
        StringAssert.Contains(text, "unsafe void IShapeEventsVtHandler.Moved(void* X, int Y)");
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("PointerEvents", text, reference: runtime);
    }

    [TestMethod]
    public void TheFieldsOfObservablesDoNotClashWithTheMembersOfTheSink()
    {
        // The sink has a method __Complete, which the field of an event named Complete must not clash with.
        var pipeline = new InteropPipeline();
        var library = pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary);
        var methods = library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "IShapeEventsVt").Methods;
        methods.Single(static m => m.Name == "Renamed").Name = "Complete";

        // The test compiler does not run the LibraryImport generator, which the functions of modules need.
        library.Types.RemoveAll(static t => t is ModuleDefinition);
        string text = pipeline
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Interop.CsComTest", SingleFile = true, SingleFileName = "Test.cs", EventStyle = EventStyle.Observable })
            .Single()
            .GetText();

        StringAssert.Matches(text, new System.Text.RegularExpressions.Regex(@"private void __Complete\(\) => global::CsCom\.Runtime\.EventSubject\.Complete\([^)]*\b__Complete_2\b[^)]*\);"));
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("ObservableNames", text, reference: runtime);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TheEnumeratorOfADispinterfaceIsItsObject(bool unique)
    {
        // A late-bound wrapper is no IEnumVARIANT: its COM object is, which ComEnumerator.FromObject enumerates, and foreach
        // releases when the marshaller created a unique object for the wrapper, and never when it is shared.
        var pipeline = new InteropPipeline();
        var library = pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary);
        var dispinterface = library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "DShapeEvents");
        var newEnum = library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "IShape").Methods
            .Single(static m => m.MemberId == -4 && m.InvokeKind == InvokeKind.PropertyGet);
        var retval = newEnum.RetvalParameter!;
        // The retval is the address of an interface pointer, which type libraries describe as a pointer to the interface.
        retval.Type = new PointerTypeReference(new PointerTypeReference(new UserDefinedTypeReference(dispinterface)));
        if (unique)
        {
            retval.Marshalling = new MarshallingOverrides { Interfaces = InterfaceMarshalling.Unique };
        }

        // The test compiler does not run the LibraryImport generator, which the functions of modules need.
        library.Types.RemoveAll(static t => t is ModuleDefinition);
        string text = pipeline
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Interop.CsComTest", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();

        const string Call = "global::CsCom.Runtime.ComEnumerator.FromObject(@this.get__NewEnum()";
        StringAssert.Contains(
            text,
            unique ? $"{Call}, ownsEnumerator: (object)@this is global::System.Runtime.InteropServices.Marshalling.ComObject);" : $"{Call});");
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("DispatchEnumerator", text, reference: runtime);
    }

    [TestMethod]
    public void InterfacePointersAreTheTypesThatAreMappedToUniqueObjects()
    {
        // SetMarshalling asks TypeMapper.IsInterfacePointer whether the marshaller of interface pointers applies to a
        // parameter, which has to agree with the mapping of the types.
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest" };
        var library = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);
        new CsCom.Transformations.AliasResolver().Transform(library, new CsCom.Transformations.TransformationContext { Options = options, Logger = NullLogger.Instance });
        var mapper = new TypeMapper(new EmitContext(library, options, new ModelAnnotations(), NullLogger.Instance), options.Namespace)
            .WithMarshalling(new MarshallingOverrides { Interfaces = InterfaceMarshalling.Unique });

        var types = library.Types.OfType<InterfaceDefinition>()
            .Where(static i => i.InterfaceKind != InterfaceKind.Dispatch)
            .SelectMany(static i => i.Methods)
            .SelectMany(static m => m.Parameters.Select(static p => TypeMapper.TryGetByRefElement(p.Type, out var element) ? element : p.Type).Append(m.ReturnType))
            .Where(static t => t is not PrimitiveTypeReference { VarType: VarEnum.VT_VOID or VarEnum.VT_HRESULT })
            .Distinct()
            .ToList();

        var pointers = types.Where(TypeMapper.IsInterfacePointer).ToList();
        CollectionAssert.AreEqual(types.Where(t => mapper.MapValue(t).IsUnique).ToList(), pointers);

        // The library has the kinds of interface pointers: IUnknown, IDispatch, an interface, a dispinterface and a coclass.
        Assert.IsTrue(pointers.Count >= 5, string.Join(", ", pointers));
    }

    [TestMethod]
    public void TheMappingsAndPredicatesOfInterfacePointersAgreeOnAliases()
    {
        // A coclass whose default interface is an alias of a dispinterface, which the pipeline resolves before the code is
        // emitted, so the model is built by hand: the predicates of the transformers and the emitters and the mappings
        // classify the interface pointers the same way.
        var dispatch = new InterfaceDefinition { Name = "DShape", OriginalName = "DShape", Guid = Guid.NewGuid(), InterfaceKind = InterfaceKind.Dispatch };
        var alias = new AliasDefinition { Name = "ShapeAlias", OriginalName = "ShapeAlias", Target = new UserDefinedTypeReference(dispatch) };
        var coClass = new CoClassDefinition { Name = "Shape", OriginalName = "Shape", Guid = Guid.NewGuid() };
        coClass.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(alias), IsDefault: true, IsSource: false, IsRestricted: false));
        var library = new TypeLibrary { Name = "Aliases" };
        library.Types.AddRange([dispatch, alias, coClass]);
        var options = new CodeGenerationOptions { Namespace = "Interop.Aliases" };
        var mapper = new TypeMapper(new EmitContext(library, options, new ModelAnnotations(), NullLogger.Instance), options.Namespace);

        foreach (TypeReference type in new TypeReference[] { new UserDefinedTypeReference(coClass), new PointerTypeReference(new UserDefinedTypeReference(coClass)) })
        {
            Assert.IsTrue(TypeMapper.IsInterfacePointer(type), type.ToString());
            Assert.IsTrue(TypeMapper.IsDispatchWrapper(type), type.ToString());
            Assert.AreEqual("DShape?", mapper.MapValue(type).Name, type.ToString());
            Assert.AreEqual("DShape?", mapper.MapLateBound(type).Name, type.ToString());
        }
    }

    [TestMethod]
    public void SetMarshallingAppliesToTheOptionsThatTheMemberTypesRead()
    {
        // SetMarshalling does not match the types that none of its changes applies to. Whether a change applies is declared
        // by the TypeMapper next to the code that reads the options, and checked here against the code that it generates, in
        // both directions: an option applies exactly when one of its values changes the code of the test library, which has
        // a structure with fields of every type that an option changes.
        MarshallingOverrides[][] options =
        [
            [.. Enum.GetValues<ValueMarshalling>().Select(static v => new MarshallingOverrides { Options = new() { Dates = v } })],
            [.. Enum.GetValues<ValueMarshalling>().Select(static v => new MarshallingOverrides { Options = new() { Currencies = v } })],
            [.. Enum.GetValues<ValueMarshalling>().Select(static v => new MarshallingOverrides { Options = new() { Booleans = v } })],
            [.. Enum.GetValues<BstrMarshalling>().Select(static v => new MarshallingOverrides { Options = new() { Bstrs = v } })],
            [.. Enum.GetValues<SafeArrayMarshalling>().Select(static v => new MarshallingOverrides { Options = new() { SafeArrays = v } })],
            [.. Enum.GetValues<InterfaceMarshalling>().Select(static v => new MarshallingOverrides { Interfaces = v })],
        ];

        string baseline = Generate(null, lateBound: false);
        foreach (var changes in options)
        {
            bool changesFields = changes.Any(change => Generate(change, lateBound: false) != baseline);
            bool changesLateBound = changes.Any(change => Generate(change, lateBound: true) != baseline);
            foreach (var change in changes)
            {
                Assert.AreEqual(changesFields, TypeMapper.AppliesToFields(change), $"{change} changes the fields of structures: {changesFields}.");
                Assert.AreEqual(changesLateBound, TypeMapper.AppliesToLateBound(change), $"{change} changes the late-bound wrappers: {changesLateBound}.");
            }
        }

        static string Generate(MarshallingOverrides? change, bool lateBound)
        {
            var pipeline = new InteropPipeline();
            if (change is not null)
            {
                pipeline.ModelTransformers.Add(new MemberMarshallingTransformer(change, lateBound));
            }

            var documents = pipeline.GenerateCode(
                pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
                new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion },
                null);
            return string.Concat(documents.OrderBy(static d => d.RelativePath, StringComparer.Ordinal).Select(static d => d.GetText()));
        }
    }

    [TestMethod]
    public void BstrFieldsArePointersWithoutMarshalling()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            Marshalling = new MarshallingOptions { Bstrs = BstrMarshalling.None },
        });

        string record = documents.Single(static d => d.RelativePath == "Record.cs").GetText();
        StringAssert.Contains(record, "public nint Name;");
        Assert.IsFalse(record.Contains("ComBstrValue", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(StructProperties.Never)]
    public Task GeneratesStructProperties(StructProperties mode)
    {
        // The default Auto mode is covered by GeneratesFilePerType.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion, StructProperties = mode });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "Record.cs" or "Number.cs" or "Point.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    [DataRow(SafeArrayMarshalling.SysArray)]
    [DataRow(SafeArrayMarshalling.Wrapper)]
    [DataRow(SafeArrayMarshalling.None)]
    public Task GeneratesSafeArrayMarshalling(SafeArrayMarshalling mode)
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions { SafeArrays = mode },
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IArrays.cs" or "IMarshalling.cs" or "DShapeEvents.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    [DataRow(EventStyle.Observable)]
    [DataRow(EventStyle.Interface)]
    public Task GeneratesEventCode(EventStyle style)
    {
        // The events style is covered by GeneratesFilePerType.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion, EventStyle = style });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "_ShapeEvents.cs" or "IShapeEventsVt.cs" or "IShapeEventsDual.cs")))
            .UseParameters(style);
    }

    [TestMethod]
    public void GeneratesNoEventCodeForTheNoneStyle()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion, EventStyle = EventStyle.None });

        Assert.IsFalse(documents.Any(static d => d.GetText().Contains("Sink", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UsesTheNamespaceForImportedTypes()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "My.Interop", ToolVersion = TestEnvironment.ToolVersion });

        var font = documents.Single(static d => d.RelativePath == "stdole/Font.cs");
        StringAssert.Contains(font.GetText(), "namespace My.Interop.stdole;");
        StringAssert.Contains(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "global::My.Interop.stdole.Font? Font();");
    }

    [TestMethod]
    public Task GeneratesPerMemberMarshalling()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <SetMarshalling Type="^IArrays$" Member="^Matrix$" SafeArrays="wrapper" />
                <SetMarshalling Type="^IStrings$" Member="^Echo$" Parameter="^Value$" Bstrs="none" />
                <SetMarshalling Type="^Record$" Member="^Date$" Dates="wrapper" />
                <SetMarshalling Type="^IShapeEventsDual$" Member="^Renamed$" Bstrs="wrapper" />
                <SetMarshalling Type="^DShapeEvents$" Member="^Names$" SafeArrays="sysarray" />
              </ModelTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IArrays.cs" or "IStrings.cs" or "Record.cs" or "IShapeEventsDual.cs" or "DShapeEvents.cs")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GeneratesWindowsLineEndings(bool singleFile)
    {
        // The snapshots do not catch mixed line endings, because Verify normalizes them.
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SupportCode = SupportCodeMode.Internal,
            SingleFile = singleFile,
            SingleFileName = "Interop.CsComTest.cs",
        });

        foreach (var document in documents)
        {
            Assert.IsFalse(document.GetText().Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal), document.RelativePath);
        }
    }

    [TestMethod]
    public Task GeneratesFlagsAttribute()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <SyntaxTransformers>
                <AddFlagsAttribute Type="^Color$" />
              </SyntaxTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "Color.cs")));
    }

    [TestMethod]
    public Task GeneratesSafeArrayRanks()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <SetSafeArrayRank TypeName="IArrays" MemberName="Matrix" Rank="2" />
                <SetSafeArrayRank TypeName="IMarshalling" MemberName="Colors" Rank="3" />
                <SetSafeArrayRank TypeName="IMarshalling" MemberName="Strings" ParameterName="Values" Rank="2" />
                <SetSafeArrayRank TypeName="DShapeEvents" MemberName="Names" Rank="2" />
              </ModelTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IArrays.cs" or "IMarshalling.cs" or "DShapeEvents.cs" or "CsComSafeArrayMarshallers.cs")));
    }

    [TestMethod]
    public Task GeneratesDefaultOverloads()
    {
        // With the wrappers, the default values of the label (BSTR) and flag (VARIANT_BOOL) parameters of IShape.Move have no C#
        // constants, so the overloads pass them.
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions
            {
                Dates = ValueMarshalling.Wrapper,
                Currencies = ValueMarshalling.Wrapper,
                Booleans = ValueMarshalling.Wrapper,
                Bstrs = BstrMarshalling.Wrapper,
            },
            OverloadsForDefaultValues = true,
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IShape.cs")));
    }

    [TestMethod]
    public void NamesIndexersOfDispinterfacesWithItemMembers()
    {
        // Like Excel's collections, whose _Default indexer conflicts with the Item method, and Excel's Range, whose _Default
        // getter has optional indexes, so that only its setter is an indexer, next to an Item property.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var library = new TypeLibrary { Name = "Synthetic" };
        var collection = Dispinterface("Collection");
        collection.Methods.Add(DispatchMethod("_Default", 0, InvokeKind.PropertyGet, variant, Parameter("Index", variant)));
        collection.Methods.Add(DispatchMethod("Item", 170, InvokeKind.Method, variant, Parameter("Index", variant)));
        collection.Methods.Add(DispatchMethod(
            "Render",
            6,
            InvokeKind.Method,
            new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID),
            Parameter("hdc", new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4)),
            Parameter("bounds", new PointerTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID)))));
        var range = Dispinterface("Range");
        range.Methods.Add(DispatchMethod("_Default", 0, InvokeKind.PropertyGet, variant, Parameter("Row", variant, optional: true), Parameter("Column", variant, optional: true)));
        range.Methods.Add(DispatchMethod("_Default", 0, InvokeKind.PropertyPut, new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID), Parameter("Row", variant, optional: true), Parameter("Column", variant, optional: true), Parameter("value", variant)));
        range.Methods.Add(DispatchMethod("Item", 170, InvokeKind.PropertyGet, variant, Parameter("Row", variant)));
        library.Types.Add(collection);
        library.Types.Add(range);

        var documents = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" });
        string collectionText = documents.Single(static d => d.RelativePath == "Collection.cs").GetText();
        string rangeText = documents.Single(static d => d.RelativePath == "Range.cs").GetText();

        StringAssert.Contains(collectionText, "[global::System.Runtime.CompilerServices.IndexerName(\"_Default\")]\r\n    public object? this[object? Index]");
        StringAssert.Contains(collectionText, "public object? Item(object? Index)");
        StringAssert.Contains(collectionText, "public void Render(int hdc, nint bounds)");
        StringAssert.Contains(rangeText, "[global::System.Runtime.CompilerServices.IndexerName(\"_DefaultIndexer\")]\r\n    public object? this[object? Row, object? Column] { set");
        StringAssert.Contains(rangeText, "public object? get__Default(object? Row = null, object? Column = null)");

        static InterfaceDefinition Dispinterface(string name) => new()
        {
            Name = name,
            OriginalName = name,
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };

        static MethodDefinition DispatchMethod(string name, int dispId, InvokeKind kind, TypeReference returnType, params ParameterDefinition[] parameters)
        {
            var method = new MethodDefinition { Name = name, OriginalName = name, MemberId = dispId, InvokeKind = kind, ReturnType = returnType };
            method.Parameters.AddRange(parameters);
            return method;
        }

        static ParameterDefinition Parameter(string name, TypeReference type, bool optional = false) =>
            new() { Name = name, Type = type, Flags = ParameterFlags.In | (optional ? ParameterFlags.Optional : ParameterFlags.None) };
    }

    [TestMethod]
    public void LimitsTheDefaultOverloads()
    {
        var library = new TypeLibrary { Name = "Synthetic" };
        var @interface = new InterfaceDefinition
        {
            Name = "IOptions",
            OriginalName = "IOptions",
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Vtable,
            BaseVtableSlots = 3,
        };
        var method = new MethodDefinition
        {
            Name = "Configure",
            OriginalName = "Configure",
            InvokeKind = InvokeKind.Method,
            VtableSlot = 3,
            ReturnType = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT),
        };
        method.Parameters.Add(new ParameterDefinition { Name = "id", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4), Flags = ParameterFlags.In });
        for (int i = 1; i <= 6; i++)
        {
            method.Parameters.Add(new ParameterDefinition
            {
                Name = $"flag{i}",
                Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_BOOL),
                Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault,
                DefaultValue = (short)-1,
            });
        }

        @interface.Methods.Add(method);
        library.Types.Add(@interface);

        var documents = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions
        {
            Namespace = "Test",
            Marshalling = new MarshallingOptions { Booleans = ValueMarshalling.Wrapper },
            OverloadsForDefaultValues = true,
        });

        string text = documents.Single(static d => d.RelativePath == "IOptions.cs").GetText();
        Assert.AreEqual(SignatureBuilder.MaxDefaultOverloads, text.Split("public void Configure(").Length - 1);
        StringAssert.Contains(text, "public void Configure(int id) => @this.Configure(id, global::CsCom.Runtime.ComBool.True,");
    }

    [TestMethod]
    [DataRow(EventStyle.Observable)]
    [DataRow(EventStyle.Events)]
    [DataRow(EventStyle.Interface)]
    public void NamesTheFieldsOfEventSinksWithoutClashes(EventStyle style)
    {
        // The observables are stored in fields named after the events, next to the __connection field: the event named
        // connection, then connection_2, get the next free names. The event named __connection is renamed so that it does not
        // clash with the field itself. The generated code is compiled to catch duplicate members.
        var events = new InterfaceDefinition
        {
            Name = "DPortEvents",
            OriginalName = "DPortEvents",
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };
        string[] names = ["connection", "connection_2", "Closed", "__connection"];
        for (int i = 0; i < names.Length; i++)
        {
            events.Methods.Add(new MethodDefinition
            {
                Name = names[i],
                OriginalName = names[i],
                MemberId = i + 1,
                InvokeKind = InvokeKind.Method,
                ReturnType = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID),
            });
        }

        var port = new CoClassDefinition { Name = "Port", OriginalName = "Port", Guid = Guid.NewGuid() };
        port.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(events), IsDefault: true, IsSource: true, IsRestricted: false));
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(events);
        library.Types.Add(port);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", EventStyle = style, SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("Port", text, reference: runtime);

        if (style == EventStyle.Observable)
        {
            StringAssert.Contains(text, "private readonly global::CsCom.Runtime.EventConnection __connection;");
            StringAssert.Contains(text, "global::CsCom.Runtime.EventSubject<DPortEvents_connectionEventArgs> __connection_2 = new();");
            StringAssert.Contains(text, "global::CsCom.Runtime.EventSubject<DPortEvents_connection_2EventArgs> __connection_2_2 = new();");
            StringAssert.Contains(text, "global::CsCom.Runtime.EventSubject<DPortEvents_ClosedEventArgs> __Closed = new();");
        }

        if (style != EventStyle.Interface)
        {
            StringAssert.Contains(text, " __connectionEvent");
        }
    }

    [TestMethod]
    public void NamesIndexParametersWithoutClashes()
    {
        // Index parameters named value, or like @this or _target that the accessors use, get a trailing _, and more while the
        // name is taken by another index parameter. The generated code is compiled to catch duplicate parameters.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var library = new TypeLibrary { Name = "Synthetic" };

        // The default member is the extension indexer with C# 15 and a proxy like Cell with C# 14.
        var vtable = new InterfaceDefinition { Name = "IGrid", OriginalName = "IGrid", Guid = Guid.NewGuid(), InterfaceKind = InterfaceKind.Vtable, BaseVtableSlots = 3 };
        var retval = new ParameterDefinition { Name = "result", Type = new PointerTypeReference(variant), Flags = ParameterFlags.Out | ParameterFlags.Retval };
        AddAccessors(vtable, "Item", 0, new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT), retval, "value", "value_", "this", "this_");
        AddAccessors(vtable, "Cell", 1, new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT), retval, "value", "value_", "_target", "_target_");
        library.Types.Add(vtable);

        // The default member of a dispinterface is the indexer of its wrapper.
        var dispatch = new InterfaceDefinition
        {
            Name = "DGrid",
            OriginalName = "DGrid",
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };
        AddAccessors(dispatch, "_Default", 0, variant, null, "value", "value_");
        library.Types.Add(dispatch);

        string text = Generate(14);
        StringAssert.Contains(text, "public object? this[object? value__, object? value_, object? @this, object? this_]");
        StringAssert.Contains(text, "public object? this[object? value__, object? value_, object? _target__, object? _target_]");
        StringAssert.Contains(text, "get => _target.get_Cell(value__, value_, _target__, _target_);");
        StringAssert.Contains(text, "set => _target.set_Cell(value__, value_, _target__, _target_, value);");
        StringAssert.Contains(text, "public object? this[object? value__, object? value_]");
        StringAssert.Contains(text, "InvokePut(0, value, value__, value_)");

        text = Generate(15);
        StringAssert.Contains(text, "public object? this[object? value__, object? value_, object? this__, object? this_] { get => @this.get_Item(value__, value_, this__, this_); set => @this.set_Item(value__, value_, this__, this_, value); }");

        string Generate(int languageVersion)
        {
            var options = new CodeGenerationOptions
            {
                Namespace = "Test",
                LanguageVersion = languageVersion,
                NamedIndexers = NamedIndexers.ClassProperty,
                SingleFile = true,
                SingleFileName = "Test.cs",
            };
            string text = new InteropPipeline().GenerateCode(library, options).Single().GetText();
            using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
            TestCompiler.Compile("Grid", text, reference: runtime, languageVersion: languageVersion >= 15 ? Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview : Microsoft.CodeAnalysis.CSharp.LanguageVersion.Default);
            return text;
        }

        // A getter that returns its value through retval (vtable) or as the result (dispinterface), and a setter.
        static void AddAccessors(InterfaceDefinition definition, string name, int dispId, TypeReference returnType, ParameterDefinition? retval, params string[] index)
        {
            var parameters = index.Select(n => new ParameterDefinition { Name = n, Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT), Flags = ParameterFlags.In }).ToList();
            bool isVtable = definition.InterfaceKind == InterfaceKind.Vtable;
            var getter = new MethodDefinition
            {
                Name = name,
                OriginalName = name,
                MemberId = dispId,
                InvokeKind = InvokeKind.PropertyGet,
                VtableSlot = isVtable ? definition.BaseVtableSlots + definition.Methods.Count : -1,
                ReturnType = returnType,
            };
            getter.Parameters.AddRange(retval is null ? parameters : [.. parameters, retval]);
            definition.Methods.Add(getter);

            var setter = new MethodDefinition
            {
                Name = name,
                OriginalName = name,
                MemberId = dispId,
                InvokeKind = InvokeKind.PropertyPut,
                VtableSlot = isVtable ? definition.BaseVtableSlots + definition.Methods.Count : -1,
                ReturnType = isVtable ? returnType : new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID),
            };
            setter.Parameters.AddRange([.. parameters, new ParameterDefinition { Name = "item", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT), Flags = ParameterFlags.In }]);
            definition.Methods.Add(setter);
        }
    }

    [TestMethod]
    public void TypesUnsignedVariantConstantsLikeIntegerLiterals()
    {
        // The VT_UI4 and VT_UI8 values of VARIANT constants get the first of int, uint, long and ulong that can hold them, like
        // C# integer literals; the other integers stay int. The generated code is compiled to catch constants that do not fit.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var module = new ModuleDefinition { Name = "Limits", OriginalName = "Limits" };
        (string Name, object Value)[] constants = [("Small", 5u), ("Mask", 0xFFFFFFFFu), ("Large", 5_000_000_000ul), ("All", ulong.MaxValue), ("Byte", (byte)255), ("Signed", -1)];
        foreach (var (name, value) in constants)
        {
            module.Constants.Add(new ConstantDefinition { Name = name, Type = variant, Value = value });
        }

        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(module);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();
        TestCompiler.Compile("Limits", text);

        StringAssert.Contains(text, "public const int Small = 5;");
        StringAssert.Contains(text, "public const uint Mask = 4294967295;");
        StringAssert.Contains(text, "public const long Large = 5000000000;");
        StringAssert.Contains(text, "public const ulong All = 18446744073709551615;");
        StringAssert.Contains(text, "public const int Byte = 255;");
        StringAssert.Contains(text, "public const int Signed = -1;");
    }

    [TestMethod]
    [DataRow(InterfaceKind.Vtable)]
    [DataRow(InterfaceKind.Dispatch)]
    public void DefaultOverloadsPassMissingOnlyForOptionalVariants(InterfaceKind kind)
    {
        // The DATE default value of date has no C# constant, so an overload omits it. The optional VARIANTs before it are
        // passed as Missing.Value when null, but the required VARIANT keeps its null value (VT_EMPTY) and the VARIANT passed
        // by reference is passed as is: `ref state ?? Missing.Value` does not compile.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var library = new TypeLibrary { Name = "Synthetic" };
        var @interface = new InterfaceDefinition
        {
            Name = "IRecorder",
            OriginalName = "IRecorder",
            Guid = Guid.NewGuid(),
            InterfaceKind = kind,
            BaseInterface = kind == InterfaceKind.Dispatch ? new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH) : null,
            BaseVtableSlots = kind == InterfaceKind.Dispatch ? 7 : 3,
        };
        var method = new MethodDefinition
        {
            Name = "Record",
            OriginalName = "Record",
            InvokeKind = InvokeKind.Method,
            MemberId = 1,
            VtableSlot = 3,
            ReturnType = new PrimitiveTypeReference(kind == InterfaceKind.Dispatch ? System.Runtime.InteropServices.VarEnum.VT_VOID : System.Runtime.InteropServices.VarEnum.VT_HRESULT),
        };
        method.Parameters.Add(new ParameterDefinition { Name = "state", Type = new PointerTypeReference(variant), Flags = ParameterFlags.In | ParameterFlags.Out });
        method.Parameters.Add(new ParameterDefinition { Name = "key", Type = variant, Flags = ParameterFlags.In });
        method.Parameters.Add(new ParameterDefinition { Name = "hint", Type = new PointerTypeReference(variant), Flags = ParameterFlags.In | ParameterFlags.Optional });
        method.Parameters.Add(new ParameterDefinition { Name = "extra", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional });
        method.Parameters.Add(new ParameterDefinition
        {
            Name = "date",
            Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DATE),
            Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault,
            DefaultValue = new DateTime(2000, 1, 1),
        });
        @interface.Methods.Add(method);
        library.Types.Add(@interface);

        var documents = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", OverloadsForDefaultValues = true });

        // The early-bound [in] VARIANT* is an `in` parameter; the call omits the modifier, which an expression cannot have.
        string text = documents.Single(static d => d.RelativePath == "IRecorder.cs").GetText();
        string hint = kind == InterfaceKind.Dispatch ? "object? hint" : "in object? hint";
        string callee = kind == InterfaceKind.Dispatch ? "Record" : "@this.Record";
        StringAssert.Contains(
            text,
            $"public void Record(ref object? state, object? key, {hint} = null, object? extra = null) => {callee}(ref state, key, " +
            "hint ?? global::System.Reflection.Missing.Value, extra ?? global::System.Reflection.Missing.Value, global::System.DateTime.FromOADate(36526D));");
    }

    [TestMethod]
    [DataRow(InterfaceKind.Vtable)]
    [DataRow(InterfaceKind.Dispatch)]
    public void SettersOmitTheOptionalIndexesLikeGetters(InterfaceKind kind)
    {
        // The value of a setter follows its index parameters, so they have no default values, but a null optional VARIANT
        // index is omitted like that of the getter and not passed as an empty VARIANT. A required index keeps its null.
        bool isDispatch = kind == InterfaceKind.Dispatch;
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var @interface = new InterfaceDefinition
        {
            Name = "ISheet",
            OriginalName = "ISheet",
            Guid = Guid.NewGuid(),
            InterfaceKind = kind,
            BaseInterface = isDispatch ? new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH) : null,
            BaseVtableSlots = isDispatch ? 7 : 3,
        };

        MethodDefinition Accessor(string name, int id, int slot, InvokeKind invokeKind)
        {
            var accessor = Method(name, slot, invokeKind, id);
            accessor.Parameters.Add(new ParameterDefinition { Name = "row", Type = variant, Flags = ParameterFlags.In });
            accessor.Parameters.Add(new ParameterDefinition { Name = "column", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional });
            if (invokeKind != InvokeKind.PropertyGet)
            {
                accessor.Parameters.Add(new ParameterDefinition { Name = "newValue", Type = variant, Flags = ParameterFlags.In });
            }
            else if (isDispatch)
            {
                accessor.ReturnType = variant;
            }
            else
            {
                accessor.Parameters.Add(new ParameterDefinition { Name = "result", Type = new PointerTypeReference(variant), Flags = ParameterFlags.Out | ParameterFlags.Retval });
            }

            if (isDispatch && invokeKind != InvokeKind.PropertyGet)
            {
                accessor.ReturnType = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID);
            }

            return accessor;
        }

        // Cell has both accessors; the default member only has a setter, which becomes the indexer of the late-bound wrapper,
        // and so has Note, which gets a proxy.
        @interface.Methods.AddRange(
        [
            Accessor("Cell", 5, 3, InvokeKind.PropertyGet),
            Accessor("Cell", 5, 4, InvokeKind.PropertyPut),
            Accessor("Item", 0, 5, InvokeKind.PropertyPutRef),
            Accessor("Note", 7, 6, InvokeKind.PropertyPut),
        ]);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(@interface);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", NamedIndexers = NamedIndexers.StructMethod, SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();

        const string Missing = "global::System.Reflection.Missing.Value";
        if (isDispatch)
        {
            StringAssert.Contains(text, $"public object? get_Cell(object? row, object? column = null) => InvokeGet(5, row, column ?? {Missing});");
            StringAssert.Contains(text, $"public void set_Cell(object? row, object? column, object? newValue) => InvokePut(5, newValue, row, column ?? {Missing});");
            StringAssert.Contains(text, $"set => InvokePutRef(0, value, row, column ?? {Missing});");

            // The indexer of the proxy of a property that only has a setter declares the default value, which the method cannot.
            StringAssert.Contains(text, $"public void set_Note(object? row, object? column, object? newValue) => InvokePut(7, newValue, row, column ?? {Missing});");
            StringAssert.Contains(text, "public object? this[object? row, object? column = null ] { set => _target.set_Note(row, column, value); }");
        }
        else
        {
            // The marshaller of the omitted VARIANTs is that of the index of the getter.
            const string Optional = "[global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::CsCom.Runtime.OptionalVariantMarshaller))] object? column";
            const string Row = "[global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::CsCom.Runtime.VariantMarshaller))] object? row";
            StringAssert.Contains(text, $"object? get_Cell({Row}, {Optional} = null);");
            StringAssert.Contains(text, $"void set_Cell({Row}, {Optional}, [");
            StringAssert.Contains(text, $"void putref_Item({Row}, {Optional}, [");
        }

        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("Sheet", text, reference: runtime);
    }

    [TestMethod]
    public void VariantsWithDefaultValuesAreNotOmittedInVtableCalls()
    {
        // Nothing applies the default value of an omitted VARIANT in a call through the vtable, and C# cannot express it as
        // the default value of an object parameter, so the parameter is required and an overload passes the value.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var find = Method("Find", 3);
        find.Parameters.Add(new ParameterDefinition { Name = "text", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_BSTR), Flags = ParameterFlags.In });
        find.Parameters.Add(new ParameterDefinition { Name = "start", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault, DefaultValue = 1 });
        find.Parameters.Add(new ParameterDefinition { Name = "options", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional });

        string text = GenerateInterface("IFinder", null, 3, find);

        StringAssert.Contains(text, "VariantMarshaller))] object? start, ");
        StringAssert.Contains(text, "OptionalVariantMarshaller))] object? options = null);");
        StringAssert.Contains(text, "public void Find(string? text) => @this.Find(text, 1, null);");
    }

    [TestMethod]
    [DataRow(15, NamedIndexers.None, "@this")]
    [DataRow(14, NamedIndexers.StructMethod, "_target")]
    public void LcidParametersHaveADefaultValueAndAreNotIndexParameters(int languageVersion, NamedIndexers namedIndexers, string target)
    {
        var @long = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4);
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var lcid = new ParameterDefinition { Name = "lcid", Type = @long, Flags = ParameterFlags.In | ParameterFlags.Lcid };
        ParameterDefinition Retval(TypeReference type) => new() { Name = "result", Type = new PointerTypeReference(type), Flags = ParameterFlags.Out | ParameterFlags.Retval };

        // The optional parameter before the [lcid] parameter keeps its default value.
        var open = Method("Open", 3);
        open.Parameters.AddRange(
        [
            new ParameterDefinition { Name = "name", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_BSTR), Flags = ParameterFlags.In },
            new ParameterDefinition { Name = "mode", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional },
            lcid,
            Retval(@long),
        ]);

        // A property and a parameterized property, whose setter has the [lcid] parameter before the value.
        var getVisible = Method("Visible", 4, InvokeKind.PropertyGet);
        getVisible.Parameters.AddRange([lcid, Retval(@long)]);
        var getItem = Method("Item", 5, InvokeKind.PropertyGet, memberId: 0);
        getItem.Parameters.AddRange([new ParameterDefinition { Name = "index", Type = @long, Flags = ParameterFlags.In }, lcid, Retval(@long)]);
        var setItem = Method("Item", 6, InvokeKind.PropertyPut, memberId: 0);
        setItem.Parameters.AddRange(
        [
            new ParameterDefinition { Name = "index", Type = @long, Flags = ParameterFlags.In },
            lcid,
            new ParameterDefinition { Name = "value", Type = @long, Flags = ParameterFlags.In },
        ]);

        // The indexer of C# 15, or the proxy of the parameterized property.
        string text = GenerateInterface("IWorkbooks", null, 3, languageVersion, namedIndexers, open, getVisible, getItem, setItem);

        StringAssert.Contains(text, "OptionalVariantMarshaller))] object? mode = null, int lcid = 0x0400);");
        StringAssert.Contains(text, "public int Visible => @this.get_Visible(0x0400);");
        StringAssert.Contains(text, $"public int this[int index]");
        StringAssert.Contains(text, $"get => {target}.get_Item(index, 0x0400);");
        StringAssert.Contains(text, $"set => {target}.set_Item(index, 0x0400, value);");
    }

    [TestMethod]
    [DataRow(InterfaceKind.Vtable, "] ref object? result, [|] in object? source);")]
    [DataRow(InterfaceKind.Dispatch, "public void Read(ref object? result, object? source)")]
    public void PointersWithoutADirectionArePassedByReference(InterfaceKind kind, string parameters)
    {
        // Old type libraries have pointers without [in] and [out], which can return values, so they are ref parameters like
        // those of TlbImp; an [in] pointer to a VARIANT is not.
        var pointer = new PointerTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT));
        bool isDispatch = kind == InterfaceKind.Dispatch;
        var read = Method("Read", 3);
        read.ReturnType = new PrimitiveTypeReference(isDispatch ? System.Runtime.InteropServices.VarEnum.VT_VOID : System.Runtime.InteropServices.VarEnum.VT_HRESULT);
        read.Parameters.Add(new ParameterDefinition { Name = "result", Type = pointer, Flags = ParameterFlags.None });
        read.Parameters.Add(new ParameterDefinition { Name = "source", Type = pointer, Flags = ParameterFlags.In });
        var @interface = new InterfaceDefinition
        {
            Name = "IReader",
            OriginalName = "IReader",
            Guid = Guid.NewGuid(),
            InterfaceKind = kind,
            BaseInterface = isDispatch ? new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH) : null,
            BaseVtableSlots = isDispatch ? 7 : 3,
        };
        @interface.Methods.Add(read);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(@interface);

        string text = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" }).Single(static d => d.RelativePath == "IReader.cs").GetText();

        foreach (string expected in parameters.Split('|'))
        {
            StringAssert.Contains(text, expected);
        }
    }

    [TestMethod]
    public void HelpStringsWithOtherLineSeparatorsStayComments()
    {
        // C# also ends a line at the Unicode line and paragraph separators and at the next-line character, which would
        // end the documentation comment and leave the rest of the help string as code.
        var method = Method("Run", 3);
        method.Documentation = "First" + (char)0x2028 + "Second" + (char)0x2029 + "Third" + (char)0x0085 + "Last";

        string text = GenerateInterface("IRunner", null, 3, method);

        StringAssert.Contains(text, "    /// First\r\n    /// Second\r\n    /// Third\r\n    /// Last\r\n    /// </summary>\r\n    void Run();");
    }

    [TestMethod]
    public void VoidPointerRetvalsStayParameters()
    {
        // HRESULT Read([out, retval] void* buffer): the method fills the buffer of the caller, so it is not a return value,
        // for which the generated code would pass the address of a local variable.
        var @void = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID);
        var read = Method("Read", 3);
        read.Parameters.Add(new ParameterDefinition { Name = "buffer", Type = new PointerTypeReference(@void), Flags = ParameterFlags.Out | ParameterFlags.Retval });

        // A void** is the address of a pointer, which is returned.
        var get = Method("Get", 4);
        get.Parameters.Add(new ParameterDefinition { Name = "result", Type = new PointerTypeReference(new PointerTypeReference(@void)), Flags = ParameterFlags.Out | ParameterFlags.Retval });

        string text = GenerateInterface("IBuffers", null, 3, read, get);

        StringAssert.Contains(text, "void Read(void* buffer);");
        StringAssert.Contains(text, "void* Get();");
    }

    [TestMethod]
    public void DefaultValuesThatDoNotFitTheirTypeAreNotGenerated()
    {
        // A type library can store the default value of a long or of an enum as an unsigned number with the same bits; a
        // value that does not fit the type has no C# constant, so the parameter is required.
        var @long = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4);
        var @short = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I2);
        var color = new EnumDefinition { Name = "Color", OriginalName = "Color" };
        ParameterDefinition Optional(string name, TypeReference type, object value) =>
            new() { Name = name, Type = type, Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault, DefaultValue = value };
        MethodDefinition With(string name, int slot, ParameterDefinition parameter)
        {
            var method = Method(name, slot);
            method.Parameters.Add(parameter);
            return method;
        }

        var @interface = new InterfaceDefinition { Name = "IDefaults", OriginalName = "IDefaults", Guid = Guid.NewGuid(), BaseVtableSlots = 3 };
        @interface.Methods.AddRange(
        [
            With("Bits", 3, Optional("value", @long, 0xFFFFFFFFu)),
            With("Large", 4, Optional("value", @long, 5_000_000_000L)),
            With("Small", 5, Optional("value", @short, 70000)),
            With("Flag", 6, Optional("value", new UserDefinedTypeReference(color), 0x80000000u)),
            With("Huge", 7, Optional("value", new UserDefinedTypeReference(color), 5_000_000_000L)),
        ]);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([color, @interface]);

        string text = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" }).Single(static d => d.RelativePath == "IDefaults.cs").GetText();

        StringAssert.Contains(text, "void Bits(int value = -1);");
        StringAssert.Contains(text, "void Large(int value);");
        StringAssert.Contains(text, "void Small(short value);");
        StringAssert.Contains(text, "void Flag(Color value = (Color)(-2147483648));");
        StringAssert.Contains(text, "void Huge(Color value);");
    }

    [TestMethod]
    public void InterfacesWithoutAGuidAreReplacedByIUnknown()
    {
        // An interface of a library block without the uuid attribute cannot be generated: the pointers to it are IUnknown
        // pointers, and an interface that derives from it has placeholders for its vtable slots.
        var hidden = new InterfaceDefinition { Name = "IHidden", OriginalName = "IHidden", BaseVtableSlots = 3 };
        hidden.Methods.AddRange([Method("First", 3), Method("Second", 4)]);
        var hiddenPointer = new PointerTypeReference(new UserDefinedTypeReference(hidden));

        var visible = new InterfaceDefinition
        {
            Name = "IVisible",
            OriginalName = "IVisible",
            Guid = Guid.NewGuid(),
            BaseInterface = new UserDefinedTypeReference(hidden),
            BaseVtableSlots = 5,
        };
        var use = Method("Use", 5);
        use.Parameters.Add(new ParameterDefinition { Name = "item", Type = hiddenPointer, Flags = ParameterFlags.In });
        var get = Method("Get", 6);
        get.Parameters.Add(new ParameterDefinition { Name = "result", Type = new PointerTypeReference(hiddenPointer), Flags = ParameterFlags.Out | ParameterFlags.Retval });
        visible.Methods.AddRange([use, get]);

        var thing = new CoClassDefinition { Name = "Thing", OriginalName = "Thing", Guid = Guid.NewGuid() };
        thing.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(hidden), IsDefault: true, IsSource: false, IsRestricted: false));

        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([hidden, visible, thing]);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();

        Assert.IsFalse(text.Contains("IHidden", StringComparison.Ordinal), text);
        StringAssert.Contains(text, "public partial interface IVisible\r\n");
        StringAssert.Contains(text, "int _VtblGap3();");
        StringAssert.Contains(text, "int _VtblGap4();");
        StringAssert.Contains(text, "object? item);");
        Assert.IsFalse(text.Contains("_VtblGap5", StringComparison.Ordinal));

        // The code compiles: nothing refers to the interface that is not generated.
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("Unidentified", text, reference: runtime);
    }

    [TestMethod]
    public void PlaceholdersFollowAnEmptyInterfaceWhoseBaseHasNoGuid()
    {
        // IMiddle has no methods, so it gets no placeholders for the slots of IHidden, which is not generated: the generated
        // IMiddle only has the slots of IUnknown, and the interface that derives from it gets the placeholders.
        var hidden = new InterfaceDefinition { Name = "IHidden", OriginalName = "IHidden", BaseVtableSlots = 3 };
        hidden.Methods.AddRange([Method("First", 3), Method("Second", 4)]);
        var middle = new InterfaceDefinition
        {
            Name = "IMiddle", OriginalName = "IMiddle", Guid = Guid.NewGuid(), BaseInterface = new UserDefinedTypeReference(hidden), BaseVtableSlots = 5,
        };
        var derived = new InterfaceDefinition
        {
            Name = "IDerived", OriginalName = "IDerived", Guid = Guid.NewGuid(), BaseInterface = new UserDefinedTypeReference(middle), BaseVtableSlots = 5,
        };
        derived.Methods.Add(Method("Use", 5));
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([hidden, middle, derived]);

        var documents = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" });

        string middleText = documents.Single(static d => d.RelativePath == "IMiddle.cs").GetText();
        string derivedText = documents.Single(static d => d.RelativePath == "IDerived.cs").GetText();
        Assert.IsFalse(middleText.Contains("_VtblGap", StringComparison.Ordinal), middleText);
        StringAssert.Contains(derivedText, "public unsafe partial interface IDerived : IMiddle");
        Assert.IsTrue(
            derivedText.IndexOf("int _VtblGap3();", StringComparison.Ordinal) is var first and >= 0
            && derivedText.IndexOf("int _VtblGap4();", StringComparison.Ordinal) is var second && second > first
            && derivedText.IndexOf("void Use();", StringComparison.Ordinal) > second,
            derivedText);
    }

    [TestMethod]
    public void InterfacesDerivedFromIEnumVariantHaveItAsTheBase()
    {
        // The methods follow those of IEnumVARIANT in the vtable, so the interface derives from it.
        string text = GenerateInterface("IEnumShapes", new WellKnownTypeReference(WellKnownType.IEnumVariant), 7, Method("Extra", 7));

        StringAssert.Contains(text, "public unsafe partial interface IEnumShapes : global::CsCom.Runtime.IEnumVARIANT");
        Assert.IsFalse(text.Contains("_VtblGap", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(EventStyle.Events)]
    [DataRow(EventStyle.Observable)]
    [DataRow(EventStyle.Interface)]
    public void EventSinksImplementTheMethodsOfIEnumVariant(EventStyle style)
    {
        // The interface of a source that derives from IEnumVARIANT derives from it, so the sink has to implement its
        // methods. The generated code is compiled to prove that it does.
        var events = new InterfaceDefinition
        {
            Name = "IEnumEvents",
            OriginalName = "IEnumEvents",
            Guid = Guid.NewGuid(),
            BaseInterface = new WellKnownTypeReference(WellKnownType.IEnumVariant),
            BaseVtableSlots = 7,
        };
        events.Methods.Add(Method("Changed", 7));

        var list = new CoClassDefinition { Name = "List", OriginalName = "List", Guid = Guid.NewGuid() };
        list.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(events), IsDefault: true, IsSource: true, IsRestricted: false));
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([events, list]);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", EventStyle = style, SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();

        StringAssert.Contains(text, "int global::CsCom.Runtime.IEnumVARIANT.Reset()");
        Assert.IsFalse(text.Contains("_VtblGap", StringComparison.Ordinal));

        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("EnumeratorEvents", text, reference: runtime);
    }

    [TestMethod]
    [DataRow(EventStyle.Events)]
    [DataRow(EventStyle.Observable)]
    [DataRow(EventStyle.Interface)]
    public void GeneratedNamesDoNotClashWithTheTypesOfTheLibrary(EventStyle style)
    {
        // A method named like its interface is renamed, and the types generated next to an interface (the extensions class,
        // the event handler, the sink and the delegates or the event arguments) get a trailing _ when types of the library
        // have their names. The generated code is compiled to prove that nothing clashes.
        var thing = new InterfaceDefinition { Name = "IThing", OriginalName = "IThing", Guid = Guid.NewGuid(), BaseVtableSlots = 3 };
        var getter = Method("Size", 4, InvokeKind.PropertyGet);
        getter.Parameters.Add(new ParameterDefinition
        {
            Name = "result",
            Type = new PointerTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4)),
            Flags = ParameterFlags.Out | ParameterFlags.Retval,
        });
        thing.Methods.AddRange([Method("IThing", 3), getter]);

        var events = new InterfaceDefinition { Name = "IThingEvents", OriginalName = "IThingEvents", Guid = Guid.NewGuid(), BaseVtableSlots = 3 };
        events.Methods.Add(Method("Changed", 3));
        var coClass = new CoClassDefinition { Name = "Thing", OriginalName = "Thing", Guid = Guid.NewGuid() };
        coClass.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(thing), IsDefault: true, IsSource: false, IsRestricted: false));
        coClass.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(events), IsDefault: true, IsSource: true, IsRestricted: false));

        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([thing, events, coClass]);
        string[] taken = ["IThingExtensions", "IThingEventsHandler", "IThingEventsSink", "IThingEvents_ChangedEventHandler", "IThingEvents_ChangedEventArgs"];
        foreach (string name in taken)
        {
            var @enum = new EnumDefinition { Name = name, OriginalName = name };
            @enum.Members.Add(new EnumMember { Name = "None", Value = 0 });
            library.Types.Add(@enum);
        }

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", EventStyle = style, SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();

        StringAssert.Contains(text, "void IThing_();");
        StringAssert.Contains(text, "public static partial class IThingExtensions_\r\n");
        StringAssert.Contains(text, "partial interface IThingEventsHandler_\r\n");
        StringAssert.Contains(text, " class IThingEventsSink_");
        if (style == EventStyle.Events)
        {
            StringAssert.Contains(text, "public delegate void IThingEvents_ChangedEventHandler_();");
        }
        else if (style == EventStyle.Observable)
        {
            StringAssert.Contains(text, "public sealed class IThingEvents_ChangedEventArgs_\r\n");
        }

        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("Clashes", text, reference: runtime);
    }

    [TestMethod]
    public void TheClassOfTheSafeArrayMarshallersDoesNotClashWithATypeOfTheLibrary()
    {
        // The marshallers of the arrays with a known rank are generated into CsComSafeArrayMarshallers, which a type of the
        // library can be named too.
        var matrix = new SafeArrayTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_R8), 2);
        var engine = new InterfaceDefinition { Name = "IEngine", OriginalName = "IEngine", Guid = Guid.NewGuid(), BaseVtableSlots = 3 };
        var put = Method("Put", 3);
        put.Parameters.Add(new ParameterDefinition { Name = "values", Type = matrix, Flags = ParameterFlags.In });
        engine.Methods.Add(put);
        var taken = new EnumDefinition { Name = "CsComSafeArrayMarshallers", OriginalName = "CsComSafeArrayMarshallers" };
        taken.Members.Add(new EnumMember { Name = "None", Value = 0 });
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([engine, taken]);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();

        StringAssert.Contains(text, "internal static class CsComSafeArrayMarshallers_\r\n");
        StringAssert.Contains(text, "typeof(global::Test.CsComSafeArrayMarshallers_.double_2D)");
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("Marshallers", text, reference: runtime);
    }

    private static MethodDefinition Method(string name, int slot, InvokeKind kind = InvokeKind.Method, int? memberId = null) => new()
    {
        Name = name,
        OriginalName = name,
        InvokeKind = kind,
        MemberId = memberId ?? slot,
        VtableSlot = slot,
        ReturnType = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT),
    };

    private static string GenerateInterface(string name, TypeReference? baseInterface, int baseSlots, params MethodDefinition[] methods) =>
        GenerateInterface(name, baseInterface, baseSlots, CodeGenerationOptions.MinimumLanguageVersion, NamedIndexers.None, methods);

    // Generates a vtable interface of a synthetic library with the overloads for default values.
    private static string GenerateInterface(
        string name, TypeReference? baseInterface, int baseSlots, int languageVersion, NamedIndexers namedIndexers, params MethodDefinition[] methods)
    {
        var @interface = new InterfaceDefinition { Name = name, OriginalName = name, Guid = Guid.NewGuid(), BaseInterface = baseInterface, BaseVtableSlots = baseSlots };
        @interface.Methods.AddRange(methods);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(@interface);

        var documents = new InteropPipeline().GenerateCode(
            library,
            new CodeGenerationOptions { Namespace = "Test", OverloadsForDefaultValues = true, LanguageVersion = languageVersion, NamedIndexers = namedIndexers });
        return documents.Single(d => d.RelativePath == name + ".cs").GetText();
    }

    [TestMethod]
    public Task GeneratesChangedTypes()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <SetType TypeName="ICalculator" MemberName="Add" To="Color" />
                <SetType TypeName="ICalculator" MemberName="Swap" To="Color" Parameter="." />
                <SetType TypeName="IShape" MemberName="Dispatch" To="IShape" />
                <SetType TypeName="IShape" MemberName="Coordinate" To="Color" />
              </ModelTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "ICalculator.cs" or "IShape.cs")));
    }

    [TestMethod]
    public Task GeneratesExcludedTypes()
    {
        // The references to DShapeEvents become IDispatch pointers and those to Color become int; EventShape has no sink for
        // IShapeEventsVt.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <ExcludeType Type="^(DShapeEvents|Color|IShapeEventsVt)$" />
              </ModelTransformers>
            </CsCom>
            """);

        Assert.IsFalse(documents.Any(static d => d.RelativePath is "DShapeEvents.cs" or "Color.cs" or "IShapeEventsVt.cs"));
        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IShape.cs")));
    }

    [TestMethod]
    public Task GeneratesAttributes()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <SyntaxTransformers>
                <AddAttribute TypeName="ICircle" Attribute="System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)" />
                <AddAttribute TypeName="ICircle" Role="Extensions" Attribute='System.Obsolete("Use IShape")' />
                <AddAttribute TypeName="ICircle" MemberName="Radius" Attribute="System.CLSCompliant(false)" />
              </SyntaxTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "ICircle.cs")));
    }

    [TestMethod]
    [DataRow(14)]
    [DataRow(15)]
    public void ExtensionsOfDerivedInterfacesTakeTheInheritedAccessors(int languageVersion)
    {
        // ICircle adds setters to the read-only Name and Item of IShape. Its extension block wins for ICircle receivers, so it
        // has the getters of IShape too; the extension block of IShape keeps the getters only.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new AccessorsInDerivedInterfaceTransformer());
        var documents = pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            new CodeGenerationOptions { Namespace = "Interop.CsComTest", LanguageVersion = languageVersion, ToolVersion = TestEnvironment.ToolVersion });

        string circle = documents.Single(static d => d.RelativePath == "ICircle.cs").GetText();
        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(circle, "public string? Name { get => @this.get_Name(); set => @this.set_Name(value); }");
        StringAssert.Contains(shape, "public string? Name => @this.get_Name();");
        if (languageVersion >= 15)
        {
            StringAssert.Contains(circle, "public object? this[object? index] { get => @this.get_Item(index); set => @this.set_Item(index, value); }");
            StringAssert.Contains(shape, "public object? this[object? index] => @this.get_Item(index);");
        }
        else
        {
            Assert.IsFalse(circle.Contains("this[", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow(NamedIndexers.ClassProperty)]
    [DataRow(NamedIndexers.StructMethod)]
    public Task GeneratesNamedIndexers(NamedIndexers mode)
    {
        // With C# 14, the default member of IShape has no extension indexer, so Item gets a proxy like Coordinate. The default
        // member of DShapeEvents is the indexer of its wrapper, and Cell gets a proxy.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", NamedIndexers = mode, ToolVersion = TestEnvironment.ToolVersion });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IShape.cs" or "DShapeEvents.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    public void NamedIndexersLeaveTheDefaultMemberToTheExtensionIndexer()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            LanguageVersion = 15,
            NamedIndexers = NamedIndexers.ClassProperty,
            ToolVersion = TestEnvironment.ToolVersion,
        });

        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(shape, "public object? this[object? index] { get => @this.get_Item(index); set => @this.set_Item(index, value); }");
        StringAssert.Contains(shape, "public CoordinateIndexer Coordinate");
        Assert.IsFalse(shape.Contains("ItemIndexer", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(NamedIndexers.ClassProperty)]
    [DataRow(NamedIndexers.StructMethod)]
    public void NamedIndexersHaveTheDefaultValuesOfOptionalIndexParameters(NamedIndexers mode)
    {
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new OptionalCoordinateIndexTransformer());
        var documents = pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            new CodeGenerationOptions { Namespace = "Interop.CsComTest", NamedIndexers = mode, ToolVersion = TestEnvironment.ToolVersion });

        StringAssert.Contains(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "public int this[int index = 0]");
    }

    [TestMethod]
    public void NamedIndexersOfDerivedInterfacesTakeTheInheritedAccessors()
    {
        // ICircle adds the setter of the default member Item of IShape, which has no extension indexer with C# 14.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new AccessorsInDerivedInterfaceTransformer());
        var documents = pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            new CodeGenerationOptions { Namespace = "Interop.CsComTest", NamedIndexers = NamedIndexers.ClassProperty, ToolVersion = TestEnvironment.ToolVersion });

        string circle = documents.Single(static d => d.RelativePath == "ICircle.cs").GetText();
        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(circle, "get => _target.get_Item(index);");
        StringAssert.Contains(circle, "set => _target.set_Item(index, value);");
        StringAssert.Contains(shape, "get => _target.get_Item(index);");
        Assert.IsFalse(shape.Contains("set_Item(index, value)", StringComparison.Ordinal));
    }

    private static IReadOnlyList<GeneratedDocument> Generate(CodeGenerationOptions options, string? configuration = null)
    {
        var pipeline = new InteropPipeline();
        return pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            options,
            configuration is null ? null : PipelineConfiguration.Parse(XDocument.Parse(configuration), pipeline.Registry));
    }

    private static IEnumerable<Target> ToTargets(IEnumerable<GeneratedDocument> documents) =>
        documents.Select(static d => new Target("cs", d.GetText(), Path.ChangeExtension(d.RelativePath, null).Replace('/', '_')));
}

/// <summary>
/// Names a method and parameters of the event interface <c>IShapeEventsDual</c> like the fields of the generated sink
/// of the connection point: <c>Renamed</c> becomes <c>__gate</c>, and the parameters of <c>Moved</c> <c>__handler</c>
/// and <c>__dispatch</c>.
/// </summary>
internal sealed class SinkFieldNamesTransformer : CsCom.Transformations.IModelTransformer
{
    public void Transform(TypeLibrary library, CsCom.Transformations.TransformationContext context)
    {
        if (library.Types.OfType<InterfaceDefinition>().SingleOrDefault(static i => i.Name == "IShapeEventsDual") is not { } events)
        {
            return;
        }

        events.Methods.Single(static m => m.Name == "Renamed").Name = "__gate";
        var moved = events.Methods.Single(static m => m.Name == "Moved");
        moved.Parameters[0].Name = "__handler";
        moved.Parameters[1].Name = "__dispatch";
    }
}

/// <summary>
/// Makes the argument of the event <c>IShapeEventsDual.Renamed</c>, whose DISPID is 3, an <c>IDispatch</c> pointer.
/// </summary>
internal sealed class ObjectEventTransformer : CsCom.Transformations.IModelTransformer
{
    /// <summary>Gets a value indicating whether the pointer is passed in and out, as <c>[in, out] IDispatch**</c>.</summary>
    public bool ByReference { get; init; }

    /// <summary>Gets a value indicating whether the parameter is marshalled as a unique object, like with <c>SetMarshalling</c>.</summary>
    public bool Unique { get; init; }

    /// <summary>Gets a value indicating whether the parameter is a <c>DShapeEvents</c> pointer, a late-bound wrapper.</summary>
    public bool Wrapper { get; init; }

    public void Transform(TypeLibrary library, CsCom.Transformations.TransformationContext context)
    {
        // The transformers also run on the libraries whose types are imported, e.g. stdole.
        if (library.Types.OfType<InterfaceDefinition>().SingleOrDefault(static i => i.Name == "IShapeEventsDual") is not { } events)
        {
            return;
        }

        var method = events.Methods.Single(static m => m.Name == "Renamed");
        var parameter = method.Parameters[0];
        TypeReference dispatch = Wrapper
            ? new PointerTypeReference(new UserDefinedTypeReference(library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "DShapeEvents")))
            : new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH);
        if (Unique)
        {
            parameter.Marshalling = new MarshallingOverrides { Interfaces = InterfaceMarshalling.Unique };
        }

        if (ByReference)
        {
            parameter.Type = new PointerTypeReference(dispatch);
            parameter.Flags = ParameterFlags.In | ParameterFlags.Out;
        }
        else
        {
            parameter.Type = dispatch;
        }
    }
}

/// <summary>
/// Changes the marshalling options of all the members of the dispinterfaces, or of all the fields of the structures,
/// like <c>SetMarshalling</c>, but whether or not the changes apply to them.
/// </summary>
internal sealed class MemberMarshallingTransformer(MarshallingOverrides marshalling, bool lateBound) : CsCom.Transformations.IModelTransformer
{
    public void Transform(TypeLibrary library, CsCom.Transformations.TransformationContext context)
    {
        if (!lateBound)
        {
            foreach (var field in library.Types.OfType<RecordDefinition>().SelectMany(static r => r.Fields))
            {
                field.Marshalling = marshalling;
            }

            return;
        }

        foreach (var dispinterface in library.Types.OfType<InterfaceDefinition>().Where(static i => i.InterfaceKind == InterfaceKind.Dispatch))
        {
            foreach (var property in dispinterface.Properties)
            {
                property.Marshalling = marshalling;
            }

            foreach (var method in dispinterface.Methods)
            {
                method.ReturnMarshalling = marshalling;
                foreach (var parameter in method.Parameters)
                {
                    parameter.Marshalling = marshalling;
                }
            }
        }
    }
}

/// <summary>
/// Makes the index of <c>IShape.Coordinate</c> optional with the default value 0.
/// </summary>
internal sealed class OptionalCoordinateIndexTransformer : CsCom.Transformations.IModelTransformer
{
    public void Transform(TypeLibrary library, CsCom.Transformations.TransformationContext context)
    {
        if (library.Types.OfType<InterfaceDefinition>().SingleOrDefault(static i => i.Name == "IShape") is not { } shape)
        {
            return;
        }

        var index = shape.Methods.Single(static m => m.Name == "Coordinate").Parameters[0];
        index.Flags |= ParameterFlags.Optional | ParameterFlags.HasDefault;
        index.DefaultValue = 0;
    }
}
