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
}
