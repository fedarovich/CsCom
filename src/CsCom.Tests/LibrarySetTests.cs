using CsCom.CodeGeneration;
using CsCom.LibrarySets;
using Microsoft.CodeAnalysis.CSharp;

namespace CsCom.Tests;

/// <summary>
/// Tests of the generation of several libraries into one project, each into its own namespace.
/// </summary>
[TestClass]
public sealed class LibrarySetTests : VerifyBase
{
    [TestMethod]
    public Task SharesTheTypesUsedFromOtherLibraries()
    {
        var result = Generate(Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.DrawingTypeLibrary));

        // CsComTest and CsComDrawing both use stdole, whose used types are generated once.
        StringAssert.Contains(GetText(result, "Interop.CsComTest/IShape.cs"), "global::Interop.stdole.Font? Font()");
        StringAssert.Contains(GetText(result, "Interop.CsComDrawing/ICanvas.cs"), "global::Interop.stdole.Font? get_Font()");
        return Verify(new { result.Libraries, Files = result.Documents.Select(static d => d.RelativePath) }, ToTargets(result.Documents.Where(static d =>
            d.RelativePath.StartsWith("Interop.stdole/", StringComparison.Ordinal) || d.RelativePath is "Interop.CsComDrawing/ICanvas.cs" or "Interop.CsComDrawing/Canvas.cs")));
    }

    [TestMethod]
    public void GeneratesAllTheTypesOfReferencedLibraries()
    {
        var stdole = new InteropPipeline().ReadTypeLibrary(TestEnvironment.StdOle);

        var result = Generate(Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.StdOle), Reference(TestEnvironment.DrawingTypeLibrary));

        CollectionAssert.AreEqual(
            new[] { ("CsComTest", true), ("stdole", true), ("CsComDrawing", true) },
            result.Libraries.Select(static l => (l.Name, l.IsReferenced)).ToArray());
        Assert.AreEqual(stdole.Types.Count, result.Libraries[1].TypeCount + stdole.Types.Count(static t => t is Model.AliasDefinition));
        StringAssert.Contains(GetText(result, "Interop.CsComDrawing/ICanvas.cs"), "global::Interop.stdole.Font? get_Font()");
        Assert.AreEqual(1, result.Documents.Count(static d => d.RelativePath.EndsWith("/Font.cs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void ResolvesTheTypesOfReferencedInteropAssemblies()
    {
        string directory = TestEnvironment.CreateDirectory("set-interop");
        string interop = Path.Combine(directory, Path.GetFileName(TestEnvironment.TestInteropAssembly));
        File.Copy(TestEnvironment.TestInteropAssembly, interop);
        string renderer = Path.Combine(directory, "ShapeRenderer.dll");
        using (var reference = new MemoryStream(File.ReadAllBytes(interop)))
        {
            TestCompiler.Compile("ShapeRenderer", RendererSource, renderer, reference);
        }

        var result = Generate(Reference(interop), Reference(renderer));

        CollectionAssert.AreEqual(
            new[] { ("CsComTest", "Interop.CsComTest", true), ("ShapeRenderer", "Interop.ShapeRenderer", true), ("stdole", "Interop.stdole", false) },
            result.Libraries.Select(static l => (l.Name, l.Namespace, l.IsReferenced)).ToArray());
        StringAssert.Contains(GetText(result, "Interop.ShapeRenderer/IShapeRenderer.cs"), "void Render([global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::CsCom.Runtime.ComObjectMarshaller<global::Interop.CsComTest.IShape>))] global::Interop.CsComTest.IShape? shape, global::Interop.CsComTest.Color color);");
        Assert.AreEqual(1, result.Documents.Count(static d => d.RelativePath.EndsWith("/IShape.cs", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void LeavesNoImportsInTheMergedModels()
    {
        // The types that CsComTest and CsComDrawing import from stdole move to the model of stdole, so the models of the
        // references import nothing, nor list the file of stdole, which is an input of the set instead.
        var options = new LibrarySetOptions { ToolVersion = TestEnvironment.ToolVersion };
        var pipeline = new InteropPipeline();
        var models = new LibrarySetGenerator(pipeline).Transform([Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.DrawingTypeLibrary)], options);

        CollectionAssert.AreEqual(new[] { "CsComTest", "CsComDrawing", "stdole" }, models.Select(static m => m.Library.Name).ToArray());
        AssertNoImports(models);

        // The same for an interop assembly, whose imported file is the assembly that it references. The files that were read
        // are those of the set.
        string directory = TestEnvironment.CreateDirectory("set-imports");
        string interop = Path.Combine(directory, Path.GetFileName(TestEnvironment.TestInteropAssembly));
        File.Copy(TestEnvironment.TestInteropAssembly, interop);
        string renderer = Path.Combine(directory, "ShapeRenderer.dll");
        using (var reference = new MemoryStream(File.ReadAllBytes(interop)))
        {
            TestCompiler.Compile("ShapeRenderer", RendererSource, renderer, reference);
        }

        Assert.IsTrue(pipeline.ReadTypeLibrary(renderer).ImportedFiles.Contains(interop, StringComparer.OrdinalIgnoreCase));

        models = new LibrarySetGenerator(pipeline).Transform([Reference(renderer)], options, sanitizeNames: true, out var inputFiles);

        CollectionAssert.IsSubsetOf(new[] { "ShapeRenderer", "CsComTest" }, models.Select(static m => m.Library.Name).ToArray());
        AssertNoImports(models);
        Assert.IsTrue(inputFiles.Contains(renderer, StringComparer.OrdinalIgnoreCase), string.Join(Environment.NewLine, inputFiles));
        Assert.IsTrue(inputFiles.Contains(interop, StringComparer.OrdinalIgnoreCase), string.Join(Environment.NewLine, inputFiles));

        static void AssertNoImports(IReadOnlyList<LibrarySetModel> models)
        {
            foreach (var model in models)
            {
                Assert.AreEqual((0, 0), (model.Model.ImportedLibraries.Count, model.Model.ImportedFiles.Count), model.Library.Name);
                Assert.IsFalse(model.Model.Types.Any(static t => t.ImportedFrom is not null), model.Library.Name);
            }
        }
    }

    [TestMethod]
    public void UsesTheNamespacesOfTheReferences()
    {
        var result = Generate(Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.DrawingTypeLibrary, "Drawing.Interop"));

        CollectionAssert.AreEqual(
            new[] { "Interop.CsComTest", "Drawing.Interop", "Interop.stdole" },
            result.Libraries.Select(static l => l.Namespace).ToArray());
        string canvas = GetText(result, "Drawing.Interop/ICanvas.cs");
        StringAssert.Contains(canvas, "namespace Drawing.Interop;");
        StringAssert.Contains(canvas, "global::Interop.stdole.Font? get_Font()");
    }

    [TestMethod]
    public void RejectsLibrariesWithTheSameNamespace()
    {
        var generator = new LibrarySetGenerator(new InteropPipeline());
        var options = new LibrarySetOptions();

        var sameNamespace = Assert.ThrowsExactly<CsComException>(() => generator.Generate(
            [Reference(TestEnvironment.TestTypeLibrary, "Interop.Shared"), Reference(TestEnvironment.DrawingTypeLibrary, "Interop.Shared")], options));
        var usedNamespace = Assert.ThrowsExactly<CsComException>(() => generator.Generate(
            [Reference(TestEnvironment.TestTypeLibrary, "Interop.stdole")], options));
        var twice = Assert.ThrowsExactly<CsComException>(() => generator.Generate(
            [Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.TestTypeLibrary, "Other")], options));

        StringAssert.StartsWith(sameNamespace.Message, "The libraries 'CsComTest' and 'CsComDrawing' would be generated into the same namespace 'Interop.Shared'.");
        StringAssert.StartsWith(usedNamespace.Message, "The libraries 'CsComTest' and 'stdole' would be generated into the same namespace 'Interop.stdole'.");
        StringAssert.StartsWith(twice.Message, "The library 'CsComTest' is referenced twice:");
    }

    [TestMethod]
    public void RejectsExcludingTypesThatOtherLibrariesUse()
    {
        string configuration = Path.Combine(TestEnvironment.CreateDirectory("set-exclude"), "cscom.xml");
        File.WriteAllText(configuration, """<CsCom><ModelTransformers><ExcludeType TypeName="Font" /></ModelTransformers></CsCom>""");

        var exception = Assert.ThrowsExactly<CsComException>(() => Generate(
            Reference(TestEnvironment.TestTypeLibrary),
            new LibrarySetReference { Source = TestEnvironment.StdOle, ConfigurationPath = configuration }));

        Assert.AreEqual("The type 'IShape' of 'CsComTest' uses 'Font', which a configuration file removed from its library.", exception.Message);
    }

    [TestMethod]
    public void SetTypeFindsTheTypesOfTheOtherLibraries()
    {
        // Font is a type of stdole, whose types are in their own model when the libraries are generated together.
        string configuration = Path.Combine(TestEnvironment.CreateDirectory("set-type"), "cscom.xml");
        File.WriteAllText(configuration, """<CsCom><ModelTransformers><SetType TypeName="IShape" MemberName="Dispatch" To="Font" /></ModelTransformers></CsCom>""");

        var result = Generate(new LibrarySetReference { Source = TestEnvironment.TestTypeLibrary, ConfigurationPath = configuration });

        StringAssert.Contains(GetText(result, "Interop.CsComTest/IShape.cs"), "global::Interop.stdole.Font? Dispatch(");
    }

    [TestMethod]
    [DataRow(SupportCodeMode.Public, "public")]
    [DataRow(SupportCodeMode.Internal, "internal")]
    public void GeneratesTheSupportCodeOnce(SupportCodeMode mode, string accessibility)
    {
        var result = Generate(
            new LibrarySetOptions { SupportCode = mode, SupportNamespace = "Test.ComSupport", ToolVersion = TestEnvironment.ToolVersion },
            Reference(TestEnvironment.TestTypeLibrary),
            Reference(TestEnvironment.DrawingTypeLibrary));

        var support = result.Documents.Where(static d => d.RelativePath.StartsWith("Test.ComSupport/", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(support.Any(static d => d.RelativePath == "Test.ComSupport/IDispatch.cs"), string.Join(", ", support.Select(static d => d.RelativePath)));
        Assert.IsFalse(result.Documents.Any(static d => d.RelativePath.Contains("/ComSupport/", StringComparison.Ordinal)));
        StringAssert.Contains(GetText(result, "Test.ComSupport/IDispatch.cs"), "namespace Test.ComSupport;");
        StringAssert.Contains(GetText(result, "Interop.CsComDrawing/ICanvas.cs"), $"{accessibility} unsafe partial interface ICanvas : global::Test.ComSupport.IDispatch");
        StringAssert.Contains(GetText(result, "Interop.stdole/Font.cs"), $"{accessibility} sealed partial class Font : global::Test.ComSupport.DispatchObject");
    }

    [TestMethod]
    public void GeneratesTheEventCodeOfEventInterfacesOfOtherLibraries()
    {
        var result = Generate(Reference(TestEnvironment.DrawingTypeLibrary));

        // FontEvents of stdole is an event interface of CsComDrawing's Canvas.
        string events = GetText(result, "Interop.stdole/FontEvents.cs");
        StringAssert.Contains(events, "namespace Interop.stdole;");
        StringAssert.Contains(events, "class FontEventsSink");
    }

    [TestMethod]
    public void GeneratesTheAssemblyAttributesAndSuppressesWarnings()
    {
        var result = Generate(
            new LibrarySetOptions { DisableRuntimeMarshalling = true, SuppressedWarnings = ["CS1591", "SYSLIB1230"], ToolVersion = TestEnvironment.ToolVersion },
            Reference(TestEnvironment.DrawingTypeLibrary));

        StringAssert.Contains(GetText(result, LibrarySetGenerator.AssemblyAttributesFileName), "[assembly: global::System.Runtime.CompilerServices.DisableRuntimeMarshalling]");
        StringAssert.Contains(GetText(result, "Interop.CsComDrawing/ICanvas.cs"), "#nullable enable\r\n#pragma warning disable CS1591, SYSLIB1230\r\n");

        // CsComDrawing uses types of stdole, which are read from its registered type library.
        string[] inputs = [.. result.InputFiles];
        Assert.AreEqual(2, inputs.Length, string.Join(Environment.NewLine, inputs));
        Assert.AreEqual(TestEnvironment.DrawingTypeLibrary, inputs[0]);
        Assert.AreEqual(TestEnvironment.StdOle, inputs[1], ignoreCase: true);
    }

    [TestMethod]
    public void UsesTheNamespacesAndConfigurationsOfLibrariesByName()
    {
        string configuration = Path.Combine(TestEnvironment.CreateDirectory("set-by-name"), "stdole.xml");
        File.WriteAllText(configuration, """<CsCom><ModelTransformers><RenameType TypeName="Font" NewName="StdFont" /></ModelTransformers></CsCom>""");
        var logger = new WarningCollector<LibrarySetGenerator>();

        var result = new LibrarySetGenerator(new InteropPipeline(), new CollectorFactory(logger)).Generate(
            [Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.DrawingTypeLibrary, "Canvas.Interop")],
            new LibrarySetOptions
            {
                // The namespace of the reference wins over the namespace of its library.
                Namespaces = new Dictionary<string, string> { ["csComDrawing"] = "Drawing.Interop", ["STDOLE"] = "Ole.Interop", ["Nope"] = "Nope.Interop" },
                ConfigurationPaths = new Dictionary<string, string> { ["stdole"] = configuration },
                ToolVersion = TestEnvironment.ToolVersion,
            });

        CollectionAssert.AreEqual(
            new[] { "Interop.CsComTest", "Canvas.Interop", "Ole.Interop" },
            result.Libraries.Select(static l => l.Namespace).ToArray());
        StringAssert.Contains(GetText(result, "Canvas.Interop/ICanvas.cs"), "global::Ole.Interop.StdFont? get_Font()");
        StringAssert.Contains(GetText(result, "Interop.CsComTest/IShape.cs"), "global::Ole.Interop.StdFont? Font()");
        CollectionAssert.Contains(result.InputFiles.ToArray(), configuration);
        CollectionAssert.AreEqual(
            new[] { "No generated library is named 'Nope'; the libraries are CsComTest, CsComDrawing, stdole" },
            logger.Messages);
    }

    [TestMethod]
    public void MergesTheSingleFilesOfTheLibraries()
    {
        var result = Generate(
            new LibrarySetOptions { SingleFile = true, SingleFileName = "Set.cs", SupportCode = SupportCodeMode.Internal, ToolVersion = TestEnvironment.ToolVersion },
            Reference(TestEnvironment.TestTypeLibrary),
            Reference(TestEnvironment.DrawingTypeLibrary));

        string text = result.Documents.Single().GetText().ReplaceLineEndings("\n");
        Assert.AreEqual("Set.cs", result.Documents[0].RelativePath);
        StringAssert.StartsWith(text, "// <auto-generated>\n//     This code was generated by CsCom 1.0.0-test from the type libraries CsComTest 1.2 ({6b1f2d2a-4c1e-4b71-9d0b-7b7c2e0a1000}), CsComDrawing 1.0");
        foreach (string @namespace in new[] { "Interop.CsComTest", "Interop.CsComDrawing", "Interop.stdole", "Interop.CsComTest.ComSupport" })
        {
            StringAssert.Contains(text, $"\nnamespace {@namespace}\n{{\n");
        }

        Assert.AreEqual(1, text.Split("// <auto-generated>").Length - 1, "The headers of the files of the libraries are removed.");

        // A single file has the metadata of the type libraries itself, before its namespaces.
        int metadata = text.IndexOf("""[assembly: global::System.Reflection.AssemblyMetadata("CsCom.TypeLibrary", "Interop.CsComTest|""", StringComparison.Ordinal);
        Assert.IsTrue(metadata > 0 && metadata < text.IndexOf("\nnamespace ", StringComparison.Ordinal));
        StringAssert.Contains(text, """AssemblyMetadata("CsCom.SupportNamespace", "Interop.CsComTest.ComSupport")""");
    }

    [TestMethod]
    public void GeneratesTheMetadataOfTheTypeLibraries()
    {
        var result = Generate(Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.DrawingTypeLibrary));

        string text = GetText(result, LibrarySetGenerator.AssemblyAttributesFileName);
        StringAssert.Contains(text, """[assembly: global::System.Reflection.AssemblyMetadata("CsCom.TypeLibrary", "Interop.CsComTest|CsComTest|{6b1f2d2a-4c1e-4b71-9d0b-7b7c2e0a1000}|1.2")]""");
        StringAssert.Contains(text, """[assembly: global::System.Reflection.AssemblyMetadata("CsCom.TypeLibrary", "Interop.stdole|stdole|{00020430-0000-0000-c000-000000000046}|2.0")]""");
        Assert.IsFalse(text.Contains("DisableRuntimeMarshalling", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(TypeLibraryMarker.SupportNamespaceKey, StringComparison.Ordinal), "The support code of the package is not generated.");

        // The metadata is read back as it was written.
        string value = text.Split('"').First(static part => part.StartsWith("Interop.CsComDrawing|", StringComparison.Ordinal));
        Assert.IsTrue(TypeLibraryMarker.TryParseLibrary(value, out string @namespace, out var library));
        Assert.AreEqual("Interop.CsComDrawing", @namespace);
        Assert.AreEqual(new Model.TypeLibraryReference("CsComDrawing", result.Libraries.Single(static l => l.Name == "CsComDrawing").Guid, 1, 0), library);
        Assert.IsFalse(TypeLibraryMarker.TryParseLibrary("Interop.CsComDrawing|CsComDrawing|1.0", out _, out _));
    }

    [TestMethod]
    public void GeneratesTheMetadataOfRenamedTypesAndOfTheSupportCode()
    {
        string directory = TestEnvironment.CreateDirectory("set-markers");
        string configuration = Path.Combine(directory, "cscom.xml");
        File.WriteAllText(configuration, """
            <CsCom>
              <ModelTransformers>
                <RenameType Type="^Color$" NewName="ShapeColor" />
              </ModelTransformers>
            </CsCom>
            """);

        var result = Generate(
            new LibrarySetOptions { SupportCode = SupportCodeMode.Public, ToolVersion = TestEnvironment.ToolVersion },
            new LibrarySetReference { Source = TestEnvironment.TestTypeLibrary, ConfigurationPath = configuration });

        string text = GetText(result, LibrarySetGenerator.AssemblyAttributesFileName);
        StringAssert.Contains(text, """AssemblyMetadata("CsCom.TypeLibrary.Type", "Interop.CsComTest.ShapeColor|Color")""");
        StringAssert.Contains(text, """AssemblyMetadata("CsCom.SupportNamespace", "Interop.CsComTest.ComSupport")""");
        Assert.IsTrue(TypeLibraryMarker.TryParseType("Interop.CsComTest.ShapeColor|Color", out string fullName, out string originalName));
        Assert.AreEqual(("Interop.CsComTest.ShapeColor", "Color"), (fullName, originalName));
    }

    [TestMethod]
    public void RejectsANamespaceThatIsAGeneratedType()
    {
        LibrarySetReference[] references = [Reference(TestEnvironment.TestTypeLibrary), Reference(TestEnvironment.DrawingTypeLibrary)];

        // The support code would declare the namespace Interop.CsComTest.IShape, an interface of CsComTest, or a class that
        // only the emitter adds.
        var support = Assert.ThrowsExactly<CsComException>(() => Generate(
            new LibrarySetOptions { SupportCode = SupportCodeMode.Internal, SupportNamespace = "Interop.CsComTest.IShape.ComSupport", ToolVersion = TestEnvironment.ToolVersion },
            references));
        var extensions = Assert.ThrowsExactly<CsComException>(() => Generate(
            new LibrarySetOptions { SupportCode = SupportCodeMode.Public, SupportNamespace = "Interop.CsComTest.IShapeExtensions.ComSupport", ToolVersion = TestEnvironment.ToolVersion },
            references));

        // So would the namespace of a library, also with the package.
        var library = Assert.ThrowsExactly<CsComException>(() => Generate(
            new LibrarySetOptions { Namespaces = new Dictionary<string, string> { ["CsComDrawing"] = "Interop.CsComTest.Shape" }, ToolVersion = TestEnvironment.ToolVersion },
            references));

        Assert.AreEqual("The namespace 'Interop.CsComTest.IShape.ComSupport' cannot be generated: 'Interop.CsComTest.IShape' is a type of 'CsComTest'.", support.Message);
        Assert.AreEqual("The namespace 'Interop.CsComTest.IShapeExtensions.ComSupport' cannot be generated: 'Interop.CsComTest.IShapeExtensions' is a type of 'CsComTest'.", extensions.Message);
        Assert.AreEqual("The namespace 'Interop.CsComTest.Shape' cannot be generated: 'Interop.CsComTest.Shape' is a type of 'CsComTest'.", library.Message);

        // The same in a single file, whose document of the first library has namespace blocks for it and the support code.
        var singleSupport = Assert.ThrowsExactly<CsComException>(() => Generate(
            new LibrarySetOptions { SingleFile = true, SingleFileName = "Set.cs", SupportCode = SupportCodeMode.Internal, SupportNamespace = "Interop.CsComTest.IShape.ComSupport", ToolVersion = TestEnvironment.ToolVersion },
            references));
        var singleLibrary = Assert.ThrowsExactly<CsComException>(() => Generate(
            new LibrarySetOptions { SingleFile = true, SingleFileName = "Set.cs", Namespaces = new Dictionary<string, string> { ["CsComDrawing"] = "Interop.CsComTest.Shape" }, ToolVersion = TestEnvironment.ToolVersion },
            references));
        Assert.AreEqual(support.Message, singleSupport.Message);
        Assert.AreEqual(library.Message, singleLibrary.Message);

        // The package declares no support namespace.
        Generate(new LibrarySetOptions { SupportNamespace = "Interop.CsComTest.IShape.ComSupport", ToolVersion = TestEnvironment.ToolVersion }, references);
    }

    [TestMethod]
    public void IgnoresGenericTypesNamedLikeNamespaces()
    {
        // As for the compiler, a namespace conflicts with a type of the same name only when the type is not generic.
        LibrarySetGenerator.ValidateDeclarations(
            [
                (Document("namespace A.B { public class C<T> { } public delegate void D<T>(); }"), "Lib"),
                (Document("namespace A.B.C { } namespace A.B.D { }"), "Other"),
            ],
            null);

        var exception = Assert.ThrowsExactly<CsComException>(() => LibrarySetGenerator.ValidateDeclarations(
            [
                (Document("namespace A.B { public class C { } }"), "Lib"),
                (Document("namespace A.B.C.ComSupport { }"), "Other"),
            ],
            null));
        Assert.AreEqual("The namespace 'A.B.C.ComSupport' cannot be generated: 'A.B.C' is a type of 'Lib'.", exception.Message);

        static GeneratedDocument Document(string text) => new("Document.cs", SyntaxFactory.ParseCompilationUnit(text));
    }

    [TestMethod]
    public void NamesTheNamespacesOfLibrariesNamedLikeKeywords()
    {
        // An interop assembly without ImportedFromTypeLib is the library of its name, here a C# keyword.
        string directory = TestEnvironment.CreateDirectory("set-keyword");
        string handlers = Path.Combine(directory, "event.dll");
        string user = Path.Combine(directory, "User.dll");
        using (var reference = TestCompiler.Compile("event", HandlerSource, handlers))
        {
            TestCompiler.Compile("User", UserSource, user, reference).Dispose();
        }

        var result = Generate(Reference(user));

        CollectionAssert.AreEqual(
            new[] { ("User", "Interop.User"), ("event", "Interop.event_") },
            result.Libraries.Select(static l => (l.Name, l.Namespace)).ToArray());
        StringAssert.Contains(GetText(result, "Interop.event_/IHandler.cs"), "namespace Interop.event_;");
        StringAssert.Contains(GetText(result, "Interop.User/IUser.cs"), "global::Interop.event_.IHandler");

        // Generated on its own, the library has the types that it uses in a nested namespace, in a folder of the same name.
        var pipeline = new InteropPipeline();
        var documents = pipeline.GenerateCode(pipeline.ReadTypeLibrary(user), new CodeGenerationOptions { Namespace = "Interop.User", ToolVersion = TestEnvironment.ToolVersion });
        var handler = documents.Single(static d => d.RelativePath.StartsWith("event_/", StringComparison.Ordinal));
        StringAssert.Contains(handler.GetText(), "namespace Interop.User.event_;");
    }

    private const string HandlerSource = """
        using System.Runtime.InteropServices;

        namespace Events
        {
            [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000201"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IHandler
            {
                void Handle();
            }
        }
        """;

    private const string UserSource = """
        using System.Runtime.InteropServices;

        namespace User
        {
            [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000202"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IUser
            {
                void Use(Events.IHandler handler);
            }
        }
        """;

    private const string RendererSource = """
        using System.Runtime.InteropServices;

        namespace Drawing
        {
            [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000101"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IShapeRenderer
            {
                void Render(CsComTest.IShape shape, CsComTest.Color color);
            }
        }
        """;

    private static LibrarySetReference Reference(string source, string? @namespace = null) => new() { Source = source, Namespace = @namespace };

    private static LibrarySetResult Generate(params LibrarySetReference[] references) =>
        Generate(new LibrarySetOptions { ToolVersion = TestEnvironment.ToolVersion }, references);

    private static LibrarySetResult Generate(LibrarySetOptions options, params LibrarySetReference[] references) =>
        new LibrarySetGenerator(new InteropPipeline()).Generate(references, options);

    private static string GetText(LibrarySetResult result, string path) =>
        result.Documents.SingleOrDefault(d => d.RelativePath == path)?.GetText()
        ?? throw new AssertFailedException($"'{path}' was not generated: {string.Join(", ", result.Documents.Select(static d => d.RelativePath))}");

    private static IEnumerable<Target> ToTargets(IEnumerable<GeneratedDocument> documents) =>
        documents.Select(static d => new Target("cs", d.GetText(), Path.ChangeExtension(d.RelativePath, null).Replace('/', '_')));
}
