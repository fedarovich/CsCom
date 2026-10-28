using CsCom.Output;

namespace CsCom.Tests;

[TestClass]
public sealed class CommandLineTests
{
    [TestMethod]
    public void NewProjectPassesTheOptions()
    {
        string directory = TestEnvironment.CreateDirectory("cli-new");

        int exitCode = Run(
            "new", "project", TestEnvironment.TestTypeLibrary,
            "-o", directory,
            "-n", "Cli.Interop",
            "--ns", "Cli.Namespace",
            "-f", "net10.0-windows", "-f", "net11.0-windows",
            "-r", "win-x64",
            "-c", "14",
            "--single-file",
            "--support-code", "internal",
            "-v", "quiet");

        Assert.AreEqual(0, exitCode);
        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual("Cli.Interop", manifest.Name);
        Assert.AreEqual("Cli.Namespace", manifest.Namespace);
        CollectionAssert.AreEqual(new[] { "net10.0-windows", "net11.0-windows" }, manifest.TargetFrameworks.ToArray());
        CollectionAssert.AreEqual(new[] { "win-x64" }, manifest.RuntimeIdentifiers.ToArray());
        Assert.AreEqual(14, manifest.LanguageVersion);
        Assert.IsTrue(manifest.SingleFile);
        Assert.AreEqual(CodeGeneration.SupportCodeMode.Internal, manifest.SupportCode);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Cli.Interop.csproj")));
    }

    [TestMethod]
    public void MarshallingOptionsHaveDefaultsAndCanBeOverridden()
    {
        string directory = TestEnvironment.CreateDirectory("cli-marshalling");

        Assert.AreEqual(0, Run(
            "new", "project", TestEnvironment.TestTypeLibrary, "-o", directory,
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
            "update", "project", TestEnvironment.TestTypeLibrary, "-o", directory,
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
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "IStrings.cs")), "nint Echo(nint Value);");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"safeArrays\": \"sysarray\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "IArrays.cs")), "global::System.Array? Matrix(");
    }

    [TestMethod]
    public void EventStyleHasADefaultAndCanBeOverridden()
    {
        string directory = TestEnvironment.CreateDirectory("cli-events");

        Assert.AreEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.EventStyle.Events, CsComManifest.Read(directory).EventStyle);

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "--event-style", "interface", "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.EventStyle.Interface, CsComManifest.Read(directory).EventStyle);

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.EventStyle.Interface, CsComManifest.Read(directory).EventStyle);

        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--event-style", "delegates"));
    }

    [TestMethod]
    public void StructPropertiesHaveADefaultAndCanBeOverridden()
    {
        string directory = TestEnvironment.CreateDirectory("cli-struct-properties");

        Assert.AreEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.StructProperties.NonBlittable, CsComManifest.Read(directory).StructProperties);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"structProperties\": \"nonblittable\"");

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "--struct-properties", "always", "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.StructProperties.Always, CsComManifest.Read(directory).StructProperties);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Record.cs")), "private global::CsCom.Runtime.ComBstrValue _name;");

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(CodeGeneration.StructProperties.Always, CsComManifest.Read(directory).StructProperties);

        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--struct-properties", "sometimes"));
    }

    [TestMethod]
    public void ConfigurationFileIsRecordedAndCanBeRemoved()
    {
        string root = TestEnvironment.CreateDirectory("cli-config");
        string directory = Path.Combine(root, "Project");
        string configuration = Path.Combine(root, "cscom.xml");
        File.WriteAllText(configuration, "<CsCom><ModelTransformers /><SyntaxTransformers /></CsCom>");

        Assert.AreEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "--config", configuration, "-v", "quiet"));
        Assert.AreEqual(@"..\cscom.xml", CsComManifest.Read(directory).Configuration);

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(@"..\cscom.xml", CsComManifest.Read(directory).Configuration);

        // A file with an unknown transformer is rejected before anything is written.
        File.WriteAllText(configuration, "<CsCom><ModelTransformers><Unknown /></ModelTransformers></CsCom>");
        Assert.AreEqual(1, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.AreEqual(1, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", Path.Combine(root, "Failed"), "--config", configuration, "-v", "quiet"));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "Failed")));

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "--config", "", "-v", "quiet"));
        Assert.IsNull(CsComManifest.Read(directory).Configuration);
    }

    [TestMethod]
    public void GeneratesFromInteropAssembliesWithReferencePaths()
    {
        string root = TestEnvironment.CreateDirectory("cli-interop");
        string directory = Path.Combine(root, "Project");
        string references = Directory.CreateDirectory(Path.Combine(root, "References")).FullName;

        Assert.AreEqual(0, Run("new", "project", TestEnvironment.TestInteropAssembly, "-o", directory, "--reference-path", references, "-v", "quiet"));
        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual(Path.GetFullPath(TestEnvironment.TestInteropAssembly), manifest.TypeLibrary);
        Assert.AreEqual("Interop.CsComTest", manifest.Name);
        CollectionAssert.AreEqual(new[] { @"..\References" }, manifest.ReferencePaths!.ToList());
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "IShape.cs")));

        Assert.AreEqual(0, Run("update", "project", "-o", directory, "-v", "quiet"));
        CollectionAssert.AreEqual(new[] { @"..\References" }, CsComManifest.Read(directory).ReferencePaths!.ToList());

        Assert.AreEqual(0, Run("update", "project", "-o", directory, "--reference-path", "", "-v", "quiet"));
        Assert.IsNull(CsComManifest.Read(directory).ReferencePaths);
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)).Contains("referencePaths", StringComparison.Ordinal));

        var (exitCode, output) = RunWithOutput("list", TestEnvironment.TestInteropAssembly, "--type", "^IShape$");
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(output, "interface IShape : IDispatch (dual)");
    }

    [TestMethod]
    public void UpdatesWithoutTheTypeLibrary()
    {
        string directory = TestEnvironment.CreateDirectory("cli-update-recorded");
        Assert.AreEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));

        Assert.AreEqual(0, Run("update", "project", "-o", directory, "-v", "quiet"));
        Assert.AreEqual(0, Run("update", "project", "-o", directory, "--single-file", "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).SingleFile);
    }

    [TestMethod]
    public void ListsTheModel()
    {
        var (exitCode, output) = RunWithOutput("list", TestEnvironment.TestTypeLibrary);
        Assert.AreEqual(0, exitCode);
        StringAssert.Contains(output, "interface IShape : IDispatch (dual)");
        StringAssert.Contains(output, "    propget Name(");
        StringAssert.Contains(output, "enum AnonymousEnum");

        (exitCode, output) = RunWithOutput("list", TestEnvironment.TestTypeLibrary, "--type", "^Color$");
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(output, "enum Color");
        Assert.IsFalse(output.Contains("interface", StringComparison.Ordinal));

        string configuration = Path.Combine(TestEnvironment.CreateDirectory("cli-list"), "cscom.xml");
        File.WriteAllText(configuration, """<CsCom><ModelTransformers><RenameType Type="^Color$" NewName="Colour" /></ModelTransformers></CsCom>""");
        (exitCode, output) = RunWithOutput("list", TestEnvironment.TestTypeLibrary, "-t", "^Col", "--config", configuration);
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(output, "enum Colour");

        Assert.AreEqual(1, RunWithOutput("list", TestEnvironment.TestTypeLibrary, "--type", "(").ExitCode);
        Assert.AreEqual(1, RunWithOutput("list", "CsCom.Does.Not.Exist").ExitCode);
    }

    [TestMethod]
    public void OverloadsForDefaultValuesAreRecordedAndCanBeTurnedOff()
    {
        string directory = TestEnvironment.CreateDirectory("cli-overloads");
        string shape = Path.Combine(directory, "Generated", "IShape.cs");

        Assert.AreEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "--bool-marshalling", "wrapper", "--overloads-for-default-values", "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).OverloadsForDefaultValues);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"overloadsForDefaultValues\": true");
        StringAssert.Contains(File.ReadAllText(shape), "global::CsCom.Runtime.ComBool.True);");

        Assert.AreEqual(0, Run("update", "project", "-o", directory, "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).OverloadsForDefaultValues);

        Assert.AreEqual(0, Run("update", "project", "-o", directory, "--overloads-for-default-values", "false", "-v", "quiet"));
        Assert.IsFalse(CsComManifest.Read(directory).OverloadsForDefaultValues);
        Assert.IsFalse(File.ReadAllText(shape).Contains("ComBool.True", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NewConfigWritesTheStarterFiles()
    {
        string directory = TestEnvironment.CreateDirectory("cli-new-config");

        Assert.AreEqual(0, Run("new", "config", "-o", directory, "-v", "quiet"));
        string configuration = Path.Combine(directory, "cscom.xml");
        StringAssert.Contains(File.ReadAllText(configuration), "xsi:noNamespaceSchemaLocation=\"cscom.xsd\"");
        Assert.IsTrue(File.Exists(Path.Combine(directory, "cscom.xsd")));

        File.WriteAllText(configuration, "<CsCom />");
        Assert.AreEqual(0, Run("new", "config", "-o", directory, "-v", "quiet"));
        Assert.AreEqual("<CsCom />", File.ReadAllText(configuration));

        // The starter configuration can be used right away.
        File.Delete(configuration);
        Assert.AreEqual(0, Run("new", "config", "-o", directory, "-v", "quiet"));
        Assert.AreEqual(0, Run("list", TestEnvironment.TestTypeLibrary, "--config", configuration, "-t", "^Color$"));
    }

    [TestMethod]
    public void RejectsInvalidMarshallingOptions()
    {
        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--date-field-marshalling", "wrapper"));
        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--bool-marshalling", "managed"));
        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--currency-marshalling", "com"));
        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--bstr-marshalling", "ansi"));
        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--safearray-marshalling", "vector"));
    }

    [TestMethod]
    public void UpdateProjectSwitchesBetweenSingleAndMultipleFiles()
    {
        string directory = TestEnvironment.CreateDirectory("cli-update");
        Assert.AreEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "--single-file", "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).SingleFile);

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
        Assert.IsTrue(CsComManifest.Read(directory).SingleFile);

        Assert.AreEqual(0, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "--single-file", "false", "-v", "quiet"));
        Assert.IsFalse(CsComManifest.Read(directory).SingleFile);
        Assert.IsTrue(Directory.GetFiles(Path.Combine(directory, "Generated")).Length > 1);
    }

    [TestMethod]
    public void ReportsErrorsWithExitCode()
    {
        string directory = TestEnvironment.CreateDirectory("cli-error");

        Assert.AreEqual(1, Run("new", "project", Path.Combine(directory, "missing.tlb"), "-o", directory, "-v", "quiet"));
        Assert.AreEqual(1, Run("new", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-f", "net8.0", "-v", "quiet"));
        Assert.AreEqual(1, Run("update", "project", TestEnvironment.TestTypeLibrary, "-o", directory, "-v", "quiet"));
    }

    [TestMethod]
    public void RejectsInvalidSyntax()
    {
        Assert.AreNotEqual(0, Run("new", "project"));
        Assert.AreNotEqual(0, Run("new", "project", TestEnvironment.TestTypeLibrary, "--support-code", "nuget"));
        Assert.AreNotEqual(0, Run("update", "assembly", TestEnvironment.TestTypeLibrary));
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
