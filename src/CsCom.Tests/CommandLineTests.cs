using CsCom.Build;
using CsCom.LibrarySets;
using CsCom.Output;

namespace CsCom.Tests;

[TestClass]
public sealed class CommandLineTests
{
    [TestMethod]
    public void ProjectCreatePassesTheOptions()
    {
        string directory = TestEnvironment.CreateDirectory("cli-new");

        int exitCode = Run(
            "project", "create", TestEnvironment.TestTypeLibrary,
            "-o", directory,
            "-n", "Cli.Interop",
            "--ns", "Cli.Namespace",
            "-f", "net10.0-windows", "-f", "net11.0-windows",
            "-r", "win-x64",
            "-l", "14",
            "--single-file",
            "-s", "internal",
            "-e", "observable",
            "-v", "quiet");

        Assert.AreEqual(0, exitCode);
        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual("Cli.Interop", manifest.Name);
        Assert.AreEqual("Cli.Namespace", manifest.Namespaces!["CsComTest"]);
        CollectionAssert.AreEqual(new[] { "net10.0-windows", "net11.0-windows" }, manifest.TargetFrameworks.ToArray());
        CollectionAssert.AreEqual(new[] { "win-x64" }, manifest.RuntimeIdentifiers.ToArray());
        Assert.AreEqual(14, manifest.LanguageVersion);
        Assert.IsTrue(manifest.SingleFile);
        Assert.AreEqual(CodeGeneration.SupportCodeMode.Internal, manifest.SupportCode);
        Assert.AreEqual(CodeGeneration.EventStyle.Observable, manifest.EventStyle);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Cli.Interop.csproj")));
    }

    [TestMethod]
    public void MarshallingOptionsHaveDefaultsAndCanBeOverridden()
    {
        string directory = TestEnvironment.CreateDirectory("cli-marshalling");

        Assert.AreEqual(0, Run(
            "project", "create", TestEnvironment.TestTypeLibrary, "-o", directory,
            "--date-marshalling", "wrapper",
            "--currency-marshalling", "None",
            "--bstr-marshalling", "wrapper",
            "--safearray-marshalling", "wrapper",
            "-v", "quiet"));
        Assert.AreEqual(
            new CodeGeneration.MarshallingOptions
            {
                Dates = CodeGeneration.ValueMarshalling.Wrapper,
                Currencies = CodeGeneration.ValueMarshalling.None,
                Bstrs = CodeGeneration.BstrMarshalling.Wrapper,
                SafeArrays = CodeGeneration.SafeArrayMarshalling.Wrapper,
            },
            CsComManifest.Read(directory).Marshalling);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"dates\": \"wrapper\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"bstrs\": \"wrapper\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"safeArrays\": \"wrapper\"");

        Assert.AreEqual(0, Run(
            "project", "update", TestEnvironment.TestTypeLibrary, "-o", directory,
            "--date-marshalling", "bcl",
            "--bool-marshalling", "none",
            "--bstr-marshalling", "none",
            "--safearray-marshalling", "sysarray",
            "-v", "quiet"));
        Assert.AreEqual(
            new CodeGeneration.MarshallingOptions
            {
                Currencies = CodeGeneration.ValueMarshalling.None,
                Booleans = CodeGeneration.ValueMarshalling.None,
                Bstrs = CodeGeneration.BstrMarshalling.None,
                SafeArrays = CodeGeneration.SafeArrayMarshalling.SysArray,
            },
            CsComManifest.Read(directory).Marshalling);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Interop.CsComTest", "IStrings.cs")), "nint Echo(nint Value);");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"safeArrays\": \"sysarray\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Interop.CsComTest", "IArrays.cs")), "global::System.Array? Matrix(");
    }

    [TestMethod]
    public void EventStyleHasADefaultAndCanBeOverridden()
    {
        string directory = TestEnvironment.CreateDirectory("cli-events");

        Assert.AreEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.EventStyle.Events, CsComManifest.Read(directory).EventStyle);

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "-e", "interface", "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.EventStyle.Interface, CsComManifest.Read(directory).EventStyle);

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.EventStyle.Interface, CsComManifest.Read(directory).EventStyle);

        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--event-style", "delegates"));
    }

    [TestMethod]
    public void StructPropertiesHaveADefaultAndCanBeOverridden()
    {
        string directory = TestEnvironment.CreateDirectory("cli-struct-properties");

        string manifest = Path.Combine(directory, CsComManifest.FileName);
        string record = Path.Combine(directory, "Generated", "Interop.CsComTest", "Record.cs");

        Assert.AreEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.StructProperties.Auto, CsComManifest.Read(directory).StructProperties);
        StringAssert.Contains(File.ReadAllText(manifest), "\"structProperties\": \"auto\"");
        StringAssert.Contains(File.ReadAllText(record), "private short _flag;");

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "--struct-properties", "never", "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.StructProperties.Never, CsComManifest.Read(directory).StructProperties);
        StringAssert.Contains(File.ReadAllText(record), "public short Flag;");

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.StructProperties.Never, CsComManifest.Read(directory).StructProperties);

        // The values of earlier versions are read as auto.
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"structProperties\": \"never\"", "\"structProperties\": \"always\"", StringComparison.Ordinal));
        Assert.AreEqual(0, Run("project", "update", "-o", directory, "-v", "quiet"));
        StringAssert.Contains(File.ReadAllText(manifest), "\"structProperties\": \"auto\"");

        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--struct-properties", "always"));
        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--struct-properties", "sometimes"));
    }

    [TestMethod]
    public void ConfigurationFileIsRecordedAndCanBeRemoved()
    {
        string root = TestEnvironment.CreateDirectory("cli-config");
        string directory = Path.Combine(root, "Project");
        string configuration = Path.Combine(root, "cscom.xml");
        File.WriteAllText(configuration, "<CsCom><ModelTransformers /><SyntaxTransformers /></CsCom>");

        Assert.AreEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", directory, "-c", configuration, "-v", "quiet"));
        Assert.AreEqual(@"..\cscom.xml", CsComManifest.Read(directory).Configurations!["CsComTest"]);

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(@"..\cscom.xml", CsComManifest.Read(directory).Configurations!["CsComTest"]);

        // A file with an unknown transformer is rejected before anything is written.
        File.WriteAllText(configuration, "<CsCom><ModelTransformers><Unknown /></ModelTransformers></CsCom>");
        Assert.AreEqual(1, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(1, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", Path.Combine(root, "Failed"), "--config", configuration, "-v", "quiet"));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "Failed")));

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "--config", "", "-v", "quiet"));
        Assert.IsNull(CsComManifest.Read(directory).Configurations);
    }

    [TestMethod]
    public void GeneratesFromInteropAssembliesWithReferencePaths()
    {
        string root = TestEnvironment.CreateDirectory("cli-interop");
        string directory = Path.Combine(root, "Project");
        string references = Directory.CreateDirectory(Path.Combine(root, "References")).FullName;

        Assert.AreEqual(0, Run("project", "create", TestEnvironment.TestInteropAssembly, "-o", directory, "--reference-path", references, "-v", "quiet"));
        var manifest = CsComManifest.Read(directory);
        CollectionAssert.AreEqual(new[] { Path.GetFullPath(TestEnvironment.TestInteropAssembly) }, manifest.Sources.ToArray());
        Assert.AreEqual("Interop.CsComTest", manifest.Name);
        CollectionAssert.AreEqual(new[] { @"..\References" }, manifest.ReferencePaths!.ToList());
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "Interop.CsComTest", "IShape.cs")));

        Assert.AreEqual(0, Run("project", "update", "-o", directory, "-v", "quiet"));
        CollectionAssert.AreEqual(new[] { @"..\References" }, CsComManifest.Read(directory).ReferencePaths!.ToList());

        Assert.AreEqual(0, Run("project", "update", "-o", directory, "--reference-path", "", "-v", "quiet"));
        Assert.IsNull(CsComManifest.Read(directory).ReferencePaths);
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)).Contains("referencePaths", StringComparison.Ordinal));

        var (exitCode, output) = RunWithOutput("list", TestEnvironment.TestInteropAssembly, "--type", "^IShape$");
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(output, $"// CsComTest 1.2: Interop.CsComTest{Environment.NewLine}interface IShape : IDispatch (dual)");
    }

    [TestMethod]
    public void UpdatesWithoutTheTypeLibrary()
    {
        string directory = TestEnvironment.CreateDirectory("cli-update-recorded");
        Assert.AreEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));

        Assert.AreEqual(0, Run("project", "update", "-o", directory, "-v", "quiet"));
        Assert.AreEqual(0, Run("project", "update", "-o", directory, "--single-file", "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).SingleFile);
        Assert.AreEqual(0, Run("project", "update", "-o", directory, "--cs", "15", "-v", "quiet"));
        Assert.AreEqual(15, CsComManifest.Read(directory).LanguageVersion);
    }

    [TestMethod]
    public void GeneratesSeveralSources()
    {
        string root = TestEnvironment.CreateDirectory("cli-several");
        string directory = Path.Combine(root, "Project");
        string configuration = Path.Combine(root, "drawing.xml");
        File.WriteAllText(configuration, """<CsCom><ModelTransformers><RenameType TypeName="ICanvas" NewName="ISurface" /></ModelTransformers></CsCom>""");
        string generated = Path.Combine(directory, "Generated");

        // The name is required, and the namespaces and configuration files are given by library name.
        Assert.AreEqual(1, Run("project", "create", TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(1, Run("project", "create", TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary, "-o", directory, "-n", "Set.Interop", "--ns", "Set.Interop", "-v", "quiet"));
        Assert.AreEqual(1, Run("project", "create", TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary, "-o", directory, "-n", "Set.Interop", "-c", configuration, "-v", "quiet"));
        Assert.IsFalse(Directory.Exists(directory));

        Assert.AreEqual(0, Run(
            "project", "create", TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary,
            "-o", directory,
            "-n", "Set.Interop",
            "--ns", "CsComDrawing=Drawing.Interop",
            "--ns", "stdole=Ole.Interop",
            "-c", $"CsComDrawing={configuration}",
            "-v", "quiet"));

        Assert.IsTrue(File.Exists(Path.Combine(directory, "Set.Interop.csproj")));
        Assert.IsTrue(File.Exists(Path.Combine(generated, "Drawing.Interop", "ISurface.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(generated, "Ole.Interop", "Font.cs")));
        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual("Drawing.Interop", manifest.Namespaces!["CsComDrawing"]);
        Assert.AreEqual(@"..\drawing.xml", manifest.Configurations!["CsComDrawing"]);

        // The recorded sources, namespaces and configuration files are kept; LIBRARY= removes a namespace.
        Assert.AreEqual(0, Run("project", "update", "-o", directory, "--ns", "stdole=", "-v", "quiet"));
        Assert.IsTrue(File.Exists(Path.Combine(generated, "Drawing.Interop", "ISurface.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(generated, "Interop.stdole", "Font.cs")));
        Assert.IsFalse(Directory.Exists(Path.Combine(generated, "Ole.Interop")));
        Assert.AreEqual(2, CsComManifest.Read(directory).Sources.Count);
    }

    [TestMethod]
    public void ListsTheModel()
    {
        var (exitCode, output) = RunWithOutput("list", TestEnvironment.TestTypeLibrary);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "interface IShape : IDispatch (dual)");
        StringAssert.Contains(output, "    propget Name(");
        StringAssert.Contains(output, "enum AnonymousEnum");

        StringAssert.Contains(output, $"{Environment.NewLine}{Environment.NewLine}// stdole 2.0 (used types): Interop.stdole{Environment.NewLine}");

        (exitCode, output) = RunWithOutput("list", TestEnvironment.TestTypeLibrary, "--type", "^Color$");
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(output, $"// CsComTest 1.2: Interop.CsComTest{Environment.NewLine}enum Color");
        Assert.IsFalse(output.Contains("interface", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("stdole", StringComparison.Ordinal), "The libraries without matching types are not listed.");

        string configuration = Path.Combine(TestEnvironment.CreateDirectory("cli-list"), "cscom.xml");
        File.WriteAllText(configuration, """<CsCom><ModelTransformers><RenameType Type="^Color$" NewName="Colour" /></ModelTransformers></CsCom>""");
        (exitCode, output) = RunWithOutput("list", TestEnvironment.TestTypeLibrary, "-t", "^Col", "--config", configuration);
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(output, $"// CsComTest 1.2: Interop.CsComTest{Environment.NewLine}enum Colour");

        // With several sources, the configuration files are given by library name.
        (exitCode, output) = RunWithOutput("list", TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary, "-t", "^(Colour|Font)$", "-c", $"CsComTest={configuration}", "--ns", "stdole=Ole.Interop");
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(output, $"// CsComTest 1.2: Interop.CsComTest{Environment.NewLine}enum Colour");
        StringAssert.Contains(output, $"// stdole 2.0 (used types): Ole.Interop{Environment.NewLine}dispinterface Font");
        Assert.IsFalse(output.Contains("CsComDrawing", StringComparison.Ordinal));
        Assert.AreEqual(1, RunWithOutput("list", TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary, "-c", configuration).ExitCode);

        Assert.AreEqual(1, RunWithOutput("list", TestEnvironment.TestTypeLibrary, "--type", "(").ExitCode);
        Assert.AreEqual(1, RunWithOutput("list", "CsCom.Does.Not.Exist").ExitCode);
    }

    [TestMethod]
    public void OverloadsForDefaultValuesAreRecordedAndCanBeTurnedOff()
    {
        string directory = TestEnvironment.CreateDirectory("cli-overloads");
        string shape = Path.Combine(directory, "Generated", "Interop.CsComTest", "IShape.cs");

        Assert.AreEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", directory, "--bool-marshalling", "wrapper", "--overloads-for-default-values", "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).OverloadsForDefaultValues);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"overloadsForDefaultValues\": true");
        StringAssert.Contains(File.ReadAllText(shape), "global::CsCom.Runtime.ComBool.True);");

        Assert.AreEqual(0, Run("project", "update", "-o", directory, "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).OverloadsForDefaultValues);

        Assert.AreEqual(0, Run("project", "update", "-o", directory, "--overloads-for-default-values", "false", "-v", "quiet"));
        Assert.IsFalse(CsComManifest.Read(directory).OverloadsForDefaultValues);
        Assert.IsFalse(File.ReadAllText(shape).Contains("ComBool.True", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ConfigCreateWritesTheStarterFiles()
    {
        string directory = TestEnvironment.CreateDirectory("cli-new-config");

        Assert.AreEqual(0, Run("config", "create", "-o", directory, "-v", "quiet"));
        string configuration = Path.Combine(directory, "cscom.xml");
        StringAssert.Contains(File.ReadAllText(configuration), "xsi:noNamespaceSchemaLocation=\"cscom.xsd\"");
        Assert.IsTrue(File.Exists(Path.Combine(directory, "cscom.xsd")));

        File.WriteAllText(configuration, "<CsCom />");
        Assert.AreEqual(0, Run("config", "create", "-o", directory, "-v", "quiet"));
        Assert.AreEqual("<CsCom />", File.ReadAllText(configuration));

        // The starter configuration can be used right away.
        File.Delete(configuration);
        Assert.AreEqual(0, Run("config", "create", "-o", directory, "-v", "quiet"));
        Assert.AreEqual(0, Run("list", TestEnvironment.TestTypeLibrary, "-c", configuration, "-t", "^Color$"));
    }

    [TestMethod]
    public void RejectsInvalidMarshallingOptions()
    {
        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--date-field-marshalling", "wrapper"));
        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--bool-marshalling", "managed"));
        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--currency-marshalling", "com"));
        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--bstr-marshalling", "ansi"));
        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--safearray-marshalling", "vector"));
    }

    [TestMethod]
    public void ProjectUpdateSwitchesBetweenSingleAndMultipleFiles()
    {
        string directory = TestEnvironment.CreateDirectory("cli-update");
        Assert.AreEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "--single-file", "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).SingleFile);

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).SingleFile);

        Assert.AreEqual(0, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "--single-file", "false", "-v", "quiet"));
        Assert.IsFalse(CsComManifest.Read(directory).SingleFile);
        Assert.IsTrue(Directory.GetFiles(Path.Combine(directory, "Generated"), "*.cs", SearchOption.AllDirectories).Length > 1);
    }

    [TestMethod]
    public void GeneratesTheCodeOfBuildRequests()
    {
        string project = TestEnvironment.CreateDirectory("cli-build-generate");
        string output = Path.Combine(project, "obj", "CsCom");

        var (exitCode, text) = RunWithOutput(CommandLine.BuildGenerateCommandName, WriteBuildRequest(project, config: string.Empty), "hash1");

        Assert.AreEqual(0, exitCode, text);
        Assert.IsTrue(File.Exists(Path.Combine(output, "Interop.CsComTest", "IShape.cs")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(output, LibrarySetBuild.StateFileName)), "hash=hash1");
        StringAssert.Contains(text, "Generating 'CsComTest' into 'Interop.CsComTest'");

        // The errors are written in the canonical format of MSBuild, with their location in configuration files.
        string configuration = Path.Combine(project, "cscom.xml");
        File.WriteAllText(configuration, "<CsCom>\n  <ModelTransformers>\n    <Rename />\n  </ModelTransformers>\n</CsCom>");
        (exitCode, text) = RunWithOutput(CommandLine.BuildGenerateCommandName, WriteBuildRequest(project, config: "cscom.xml"), "hash2");
        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(text, $"{configuration}(3,6): error CSCOM1000: Unknown model transformer <Rename>.");

        // The command is only used by the MSBuild task.
        (_, text) = RunWithOutput("--help");
        Assert.IsFalse(text.Contains(CommandLine.BuildGenerateCommandName, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReportsErrorsWithExitCode()
    {
        string directory = TestEnvironment.CreateDirectory("cli-error");

        Assert.AreEqual(1, Run("project", "create", Path.Combine(directory, "missing.tlb"), "-o", directory, "-v", "quiet"));
        Assert.AreEqual(1, Run("project", "create", TestEnvironment.TestTypeLibrary, "-o", directory, "-f", "net8.0", "-v", "quiet"));
        Assert.AreEqual(1, Run("project", "update", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
    }

    [TestMethod]
    public void RejectsInvalidSyntax()
    {
        Assert.AreNotEqual(0, Run("project", "create"));
        Assert.AreNotEqual(0, Run("project", "create", TestEnvironment.TestTypeLibrary, "--support-code", "nuget"));
        Assert.AreNotEqual(0, Run("assembly", "update", TestEnvironment.TestTypeLibrary));

        // The packages are RID-neutral, and the package options and properties are validated before anything is built.
        string output = TestEnvironment.CreateDirectory("pack-syntax");
        Assert.AreNotEqual(0, Run("assembly", "pack", TestEnvironment.TestTypeLibrary, "-o", output, "-r", "win-x64"));
        Assert.AreEqual(1, Run("assembly", "pack", TestEnvironment.TestTypeLibrary, "-o", output, "--package-version", "1.x", "-v", "quiet"));
        Assert.AreEqual(1, Run("assembly", "pack", TestEnvironment.TestTypeLibrary, "-o", output, "-p", "Authors", "-v", "quiet"));
        Assert.AreEqual(1, Run("assembly", "pack", TestEnvironment.TestTypeLibrary, "-o", output, "--property", "1x=y", "-v", "quiet"));
        Assert.AreEqual(1, Run("assembly", "build", TestEnvironment.TestTypeLibrary, "-o", output, "-p", "Authors", "-v", "quiet"));
        Assert.AreEqual(1, Run("assembly", "build", TestEnvironment.TestTypeLibrary, "-o", output, "--property", "1x=y", "-v", "quiet"));
        Assert.IsEmpty(Directory.GetFileSystemEntries(output));

        // The verb-noun commands were renamed.
        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary));
        Assert.AreNotEqual(0, Run("update", "project"));
        Assert.AreNotEqual(0, Run("new", "assembly", TestEnvironment.TestTypeLibrary));
        Assert.AreNotEqual(0, Run("new", "config"));
    }

    [TestMethod]
    public void ManagesTheReferencesOfProjects()
    {
        string directory = TestEnvironment.CreateDirectory("cli-reference");
        File.WriteAllText(Path.Combine(directory, "Directory.Packages.props"), "<Project />");
        string project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");

        // Only the specified options are written as metadata, with the values of the CsCom* properties.
        Assert.AreEqual(0, Run(
            "reference", "add", "Scripting.Dictionary", "--project", directory,
            "-e", "observable", "--safearray-marshalling", "sysarray", "--overloads-for-default-values", "-v", "quiet"));
        Assert.AreEqual(0, Run("reference", "add", TestEnvironment.StdOle, "--project", project, "--ns", "Ole.Interop", "-c", Path.Combine(directory, "stdole.xml"), "-v", "quiet"));
        var (exitCode, output) = RunWithOutput("reference", "list", "--project", project);
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(
            $"Scripting.Dictionary EventStyle=observable OverloadsForDefaultValues=true SafeArrayMarshalling=sysarray\n{TestEnvironment.StdOle} Namespace=Ole.Interop Config=stdole.xml\n",
            output.ReplaceLineEndings("\n"));
        StringAssert.Contains(File.ReadAllText(project), $"<PackageReference Include=\"CsCom.Build\" Version=\"{ToolInfo.Version}\" />");

        Assert.AreEqual(1, Run("reference", "add", "Scripting.Dictionary", TestEnvironment.StdOle, "--project", project, "--ns", "Ole.Interop", "-v", "quiet"));
        Assert.AreEqual(1, Run("reference", "add", "Scripting.Dictionary", "--project", project, "--event-style", "delegates", "-v", "quiet"));
        Assert.AreEqual(1, Run("reference", "remove", "Scripting.Dictionary", "Excel.Application", "--project", project, "-v", "quiet"));
        Assert.AreEqual(1, Run("reference", "list", "--project", Path.Combine(directory, "Missing"), "-v", "quiet"));

        Assert.AreEqual(0, Run("reference", "remove", "Scripting.Dictionary", TestEnvironment.StdOle, "--project", directory, "-v", "quiet"));
        (exitCode, output) = RunWithOutput("reference", "list", "--project", directory, "-v", "quiet");
        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(string.Empty, output);
    }

    // Writes a request as the MSBuild task of CsCom.Build does.
    private static string WriteBuildRequest(string project, string config)
    {
        var json = new JsonWriter();
        json.Property("projectDirectory", project);
        json.Property("outputDirectory", Path.Combine(project, "obj", "CsCom"));
        json.Property("targetFramework", "net10.0-windows");
        json.StartArray("references");
        json.StartObject();
        json.Property("source", TestEnvironment.TestTypeLibrary);
        json.Property("config", config);
        json.EndObject();
        json.EndArray();
        string path = Path.Combine(project, "cscom.request.json");
        File.WriteAllText(path, json.ToString());
        return path;
    }

    private static int Run(params string[] args) => CommandLine.Create().Parse(args).Invoke(new System.CommandLine.InvocationConfiguration
    {
        Output = TextWriter.Null,
        Error = TextWriter.Null,
    });

    private static (int ExitCode, string Output) RunWithOutput(params string[] args)
    {
        using var output = new StringWriter();
        int exitCode = CommandLine.Create().Parse(args).Invoke(new System.CommandLine.InvocationConfiguration
        {
            Output = output,
            Error = TextWriter.Null,
        });
        return (exitCode, output.ToString());
    }
}
