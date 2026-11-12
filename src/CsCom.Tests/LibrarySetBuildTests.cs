using CsCom.Build;
using CsCom.LibrarySets;

namespace CsCom.Tests;

/// <summary>
/// Tests of the generation for the MSBuild task of CsCom.Build: the request, the written files, the state file and the messages.
/// </summary>
[TestClass]
public sealed class LibrarySetBuildTests
{
    [TestMethod]
    public void WritesTheFilesAndTheState()
    {
        string project = TestEnvironment.CreateDirectory("build-run");
        File.Copy(TestEnvironment.TestTypeLibrary, Path.Combine(project, "CsComTest.tlb"));
        string output = Path.Combine(project, "obj", "CsCom");

        var files = Run(project, [new BuildReference { Source = "CsComTest.tlb" }, new BuildReference { Source = TestEnvironment.DrawingTypeLibrary, Namespace = "Drawing.Interop" }]);

        string state = Path.Combine(output, LibrarySetBuild.StateFileName);
        CollectionAssert.AreEquivalent(files.ToArray(), BuildState.ReadGeneratedFiles(state, "hash1")!.ToArray());
        Assert.IsNull(BuildState.ReadGeneratedFiles(state, "hash2"), "Another request is not up to date.");
        Assert.IsTrue(File.Exists(Path.Combine(output, "Interop.CsComTest", "IShape.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "Drawing.Interop", "ICanvas.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(output, "Interop.stdole", "Font.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(output, LibrarySetGenerator.AssemblyAttributesFileName)));
        StringAssert.Contains(File.ReadAllText(state), $"|{Path.Combine(project, "CsComTest.tlb")}");

        // The unchanged files are not written again, and the files of the removed references are deleted.
        string shape = Path.Combine(output, "Interop.CsComTest", "IShape.cs");
        var written = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(shape, written);
        Run(project, [new BuildReference { Source = "CsComTest.tlb" }]);
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(shape));
        Assert.IsFalse(Directory.Exists(Path.Combine(output, "Drawing.Interop")));

        // A changed input is not up to date.
        File.SetLastWriteTimeUtc(Path.Combine(project, "CsComTest.tlb"), written);
        Assert.IsNull(BuildState.ReadGeneratedFiles(state, "hash1"));
    }

    [TestMethod]
    public void RecordsTheRegisteredTypeLibrariesOfImportedTypes()
    {
        // CsComTest uses types of stdole, which are read from its registered type library, e.g. System32\stdole2.tlb.
        string project = TestEnvironment.CreateDirectory("build-imported");

        Run(project, [new BuildReference { Source = TestEnvironment.TestTypeLibrary }]);

        var inputs = ReadInputs(Path.Combine(project, "obj", "CsCom", LibrarySetBuild.StateFileName));
        Assert.IsTrue(
            inputs.Any(static i => string.Equals(Path.GetFileName(i), "stdole2.tlb", StringComparison.OrdinalIgnoreCase) && File.Exists(i)),
            string.Join(Environment.NewLine, inputs));
    }

    [TestMethod]
    public void RecordsTheReferencedAssembliesOfImportedTypes()
    {
        // An interop assembly whose structure uses an enum of an assembly of the reference path: the code is out of date when
        // that assembly is rebuilt, like when the interop assembly itself is.
        string project = TestEnvironment.CreateDirectory("build-referenced");
        string references = Path.Combine(project, "references");
        Directory.CreateDirectory(references);
        string enums = Path.Combine(references, "Enums.dll");
        TestCompiler.Compile(
            "Interop.Uses",
            """
            [assembly: System.Runtime.InteropServices.ImportedFromTypeLib("Uses")]
            namespace Uses { public struct Item { public Enums.Kind Kind; } }
            """,
            Path.Combine(project, "Interop.Uses.dll"),
            TestCompiler.Compile("Enums", "namespace Enums { public enum Kind { First } }", enums));

        Run(project, [new BuildReference { Source = "Interop.Uses.dll", ReferencePath = "references" }]);

        string state = Path.Combine(project, "obj", "CsCom", LibrarySetBuild.StateFileName);
        CollectionAssert.Contains(ReadInputs(state).ToArray(), enums);
        Assert.IsNotNull(BuildState.ReadGeneratedFiles(state, "hash1"));

        File.SetLastWriteTimeUtc(enums, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.IsNull(BuildState.ReadGeneratedFiles(state, "hash1"));
    }

    [TestMethod]
    [DataRow("EventStyle", "delegates", "'delegates' is not a valid value of CsComEventStyle: one of events, observable, interface, none is expected.")]
    [DataRow("SupportCode", "nuget", "'nuget' is not a valid value of CsComSupportCode: one of package, public, internal is expected.")]
    [DataRow("SafeArrayMarshalling", "1", "'1' is not a valid value of CsComSafeArrayMarshalling: one of array, sysarray, wrapper, none is expected.")]
    [DataRow("OverloadsForDefaultValues", "yes", "'yes' is not a valid value of CsComOverloadsForDefaultValues: true or false is expected.")]
    [DataRow("NamedIndexers", "methods", "'methods' is not a valid value of CsComNamedIndexers: one of none, classproperty, structmethod is expected.")]
    [DataRow("LanguageVersion", "13", "The generated code requires C# 14 or later, but C# 13 is specified.")]
    [DataRow("TargetFramework", "net8.0-windows", "The target framework 'net8.0-windows' is not supported: .NET 10 or later is required.")]
    public void RejectsInvalidProperties(string property, string value, string message)
    {
        string project = TestEnvironment.CreateDirectory("build-invalid");
        var request = new BuildRequest
        {
            ProjectDirectory = project,
            OutputDirectory = Path.Combine(project, "obj"),
            EventStyle = property == "EventStyle" ? value : string.Empty,
            SupportCode = property == "SupportCode" ? value : string.Empty,
            SafeArrayMarshalling = property == "SafeArrayMarshalling" ? value : string.Empty,
            OverloadsForDefaultValues = property == "OverloadsForDefaultValues" ? value : string.Empty,
            NamedIndexers = property == "NamedIndexers" ? value : string.Empty,
            LanguageVersion = property == "LanguageVersion" ? value : string.Empty,
            TargetFramework = property == "TargetFramework" ? value : "net10.0-windows",
            References = [new BuildReference { Source = TestEnvironment.TestTypeLibrary }],
        };

        var exception = Assert.ThrowsExactly<CsComException>(() => new LibrarySetBuild(new InteropPipeline()).Run(request, "hash"));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void UsesTheNamespacesAndConfigurationsOfLibraries()
    {
        string project = TestEnvironment.CreateDirectory("build-libraries");
        File.WriteAllText(Path.Combine(project, "stdole.xml"), """<CsCom><ModelTransformers><RenameType TypeName="Font" NewName="StdFont" /></ModelTransformers></CsCom>""");

        var files = new LibrarySetBuild(new InteropPipeline()).Run(
            new BuildRequest
            {
                ProjectDirectory = project,
                OutputDirectory = Path.Combine(project, "obj", "CsCom"),
                References = [new BuildReference { Source = TestEnvironment.TestTypeLibrary }],
                Libraries = [new BuildLibrary { Name = "stdole", Namespace = "Ole.Interop", Config = "stdole.xml" }],
            },
            "hash");

        string font = Path.Combine(project, "obj", "CsCom", "Ole.Interop", "StdFont.cs");
        CollectionAssert.Contains(files.ToArray(), font);
        StringAssert.Contains(File.ReadAllText(font), "namespace Ole.Interop;");

        // The configuration file of a library is an input of the state.
        StringAssert.Contains(File.ReadAllText(Path.Combine(project, "obj", "CsCom", LibrarySetBuild.StateFileName)), $"|{Path.Combine(project, "stdole.xml")}");

        var exception = Assert.ThrowsExactly<CsComException>(() => new LibrarySetBuild(new InteropPipeline()).Run(
            new BuildRequest
            {
                ProjectDirectory = project,
                OutputDirectory = Path.Combine(project, "obj", "CsCom"),
                References = [new BuildReference { Source = TestEnvironment.TestTypeLibrary }],
                Libraries = [new BuildLibrary { Name = " ", Namespace = "Ole.Interop" }],
            },
            "hash"));
        Assert.AreEqual("A CsComLibrary item has an empty Include.", exception.Message);
    }

    [TestMethod]
    public void RejectsInvalidMetadata()
    {
        string project = TestEnvironment.CreateDirectory("build-invalid-metadata");

        var exception = Assert.ThrowsExactly<CsComException>(() =>
            Run(project, [new BuildReference { Source = "Scripting.Dictionary", BstrMarshalling = "ansi" }]));

        Assert.AreEqual(
            "'ansi' is not a valid value of the BstrMarshalling metadata of the CsComReference 'Scripting.Dictionary': one of string, wrapper, none is expected.",
            exception.Message);
    }

    [TestMethod]
    public void UsesTheLanguageVersionOfTheProject()
    {
        string project = TestEnvironment.CreateDirectory("build-language");

        Run(project, [new BuildReference { Source = TestEnvironment.TestTypeLibrary }], languageVersion: "15.0");

        // Extension indexers are generated for C# 15.
        StringAssert.Contains(File.ReadAllText(Path.Combine(project, "obj", "CsCom", "Interop.CsComTest", "IShape.cs")), "this[");
    }

    [TestMethod]
    public void ReadsTheRequestWrittenByTheTask()
    {
        string path = Path.Combine(TestEnvironment.CreateDirectory("build-request"), "request.json");
        var json = new JsonWriter();
        json.Property("projectDirectory", @"C:\Project ""A""\");
        json.Property("outputDirectory", "obj\\CsCom\\\u0001");
        json.Property("eventStyle", "none");
        json.StartArray("references");
        json.StartObject();
        json.Property("source", "Excel.Application");
        json.Property("namespace", "Office.Excel");
        json.EndObject();
        json.StartObject();
        json.Property("source", "{00020813-0000-0000-C000-000000000046}:1.9");
        json.EndObject();
        json.EndArray();
        File.WriteAllText(path, json.ToString());

        var request = BuildRequest.Load(path);

        Assert.AreEqual(@"C:\Project ""A""\", request.ProjectDirectory);
        Assert.AreEqual("obj\\CsCom\\\u0001", request.OutputDirectory);
        Assert.AreEqual("none", request.EventStyle);
        CollectionAssert.AreEqual(
            new[] { ("Excel.Application", "Office.Excel"), ("{00020813-0000-0000-C000-000000000046}:1.9", string.Empty) },
            request.References.Select(static r => (r.Source, r.Namespace)).ToArray());
    }

    [TestMethod]
    public void FormatsTheMessagesForMSBuild()
    {
        string configuration = Path.Combine(TestEnvironment.CreateDirectory("build-messages"), "cscom.xml");
        File.WriteAllText(configuration, "<CsCom>\n  <ModelTransformers>\n    <Rename />\n  </ModelTransformers>\n</CsCom>");
        var error = Assert.ThrowsExactly<CsComException>(() => Configuration.PipelineConfiguration.Load(configuration, Configuration.TransformerRegistry.CreateDefault()));

        StringAssert.StartsWith(MSBuildOutput.FormatError(error), $"{configuration}(3,6): error CSCOM1000: Unknown model transformer <Rename>.");
        Assert.AreEqual("CsCom : error CSCOM1000: The library is missing.", MSBuildOutput.FormatError(new CsComException("The library is missing.")));
        Assert.AreEqual("CsCom : warning CSCOM2000: Two lines.", MSBuildOutput.FormatWarning("Two\r\nlines."));
    }

    private static IReadOnlyList<string> Run(string project, IReadOnlyList<BuildReference> references, string languageVersion = "") =>
        new LibrarySetBuild(new InteropPipeline()).Run(
            new BuildRequest
            {
                ProjectDirectory = project,
                OutputDirectory = Path.Combine(project, "obj", "CsCom"),
                TargetFramework = "net10.0-windows",
                LanguageVersion = languageVersion,
                References = references,
            },
            "hash1");

    // The paths of the input= lines of a state file, which follow the last write time and a '|'.
    private static List<string> ReadInputs(string state) =>
        [.. File.ReadAllLines(state).Where(static l => l.StartsWith("input=", StringComparison.Ordinal)).Select(static l => l[(l.IndexOf('|') + 1)..])];
}
