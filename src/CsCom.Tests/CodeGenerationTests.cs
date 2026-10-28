using System.Xml.Linq;
using CsCom.CodeGeneration;
using CsCom.Configuration;

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
    [DataRow(StructProperties.Always)]
    [DataRow(StructProperties.Never)]
    public Task GeneratesStructProperties(StructProperties mode)
    {
        // The default NonBlittable mode is covered by GeneratesFilePerType.
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
