using CsCom.CodeGeneration;
using CsCom.LibrarySets;
using Microsoft.Extensions.Logging;

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
        StringAssert.Contains(GetText(result, "Interop.ShapeRenderer/IShapeRenderer.cs"), "void Render(global::Interop.CsComTest.IShape? shape, global::Interop.CsComTest.Color color);");
        Assert.AreEqual(1, result.Documents.Count(static d => d.RelativePath.EndsWith("/IShape.cs", StringComparison.Ordinal)));
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
        CollectionAssert.AreEquivalent(new[] { TestEnvironment.DrawingTypeLibrary }, result.InputFiles.ToArray());
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
    }

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

    private sealed class CollectorFactory(ILogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}
