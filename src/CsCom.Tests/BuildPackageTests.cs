using System.Diagnostics;
using System.Text.Json;
using CsCom.Output;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Tests;

/// <summary>
/// Tests of the CsCom.Build package: projects that reference it and list type libraries as CsComReference items are built with
/// the .NET SDK, and with the MSBuild of Visual Studio if it is installed.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class BuildPackageTests
{
    private const string Program = """
        using Interop.Scripting;

        var dictionary = Dictionary.Create();
        dictionary.Add("a", "one");
        dictionary.Add("b", "two");
        Console.WriteLine($"Count={dictionary.Count}");
        Console.WriteLine(typeof(Drawing.Interop.ICanvas).FullName);
        Console.WriteLine(typeof(Interop.stdole.Font).FullName);
        Console.WriteLine(typeof(Interop.CsComTest.IShape).FullName);
        """;

    [TestMethod]
    public void GeneratesTheReferencesDuringTheBuild()
    {
        // The namespace of stdole, whose types are only used, is set by a CsComLibrary item.
        string project = CreateConsumer(
            "build-package",
            $"""
            <CsComReference Include="{TestEnvironment.TestTypeLibrary}" />
            <CsComReference Include="{TestEnvironment.DrawingTypeLibrary}" Namespace="Drawing.Interop" />
            <CsComReference Include="Scripting.Dictionary" />
            <CsComLibrary Include="stdole" Namespace="Ole.Interop" />
            """,
            program: Program.Replace("Interop.stdole.Font", "Ole.Interop.Font", StringComparison.Ordinal));

        var output = Run(project);

        CollectionAssert.AreEqual(
            new[] { "Count=2", "Drawing.Interop.ICanvas", "Ole.Interop.Font", "Interop.CsComTest.IShape" },
            output.TakeLast(4).ToArray());
        string generated = Path.Combine(Path.GetDirectoryName(project)!, "obj", "Debug", TestEnvironment.TargetFramework, "CsCom");
        CollectionAssert.AreEquivalent(
            new[] { "Drawing.Interop", "Interop.CsComTest", "Interop.Scripting", "Ole.Interop" },
            Directory.GetDirectories(generated).Select(Path.GetFileName).ToArray());

        // The second build does not run the generator. Its evaluation includes the generated files, which are compiled once.
        string dictionary = Path.Combine(generated, "Interop.Scripting", "Dictionary.cs");
        var written = File.GetLastWriteTimeUtc(dictionary);
        StringAssert.Contains(Build(project), "CsCom: the interop code in");
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(dictionary));

        // The files that the generator read are inputs of the fast up-to-date check of Visual Studio, also the type library
        // of the ProgID.
        var inputs = GetItems(project, "UpToDateCheckInput", "-t:_CsComCollectUpToDateCheckInputs").Select(static item => item.Path).ToList();
        CollectionAssert.Contains(inputs, TestEnvironment.TestTypeLibrary);
        Assert.IsTrue(inputs.Any(static path => path.EndsWith("scrrun.dll", StringComparison.OrdinalIgnoreCase)), string.Join(Environment.NewLine, inputs));

        // Solution Explorer shows the files of the intermediate output under the CsCom folder.
        CollectionAssert.Contains(
            GetItems(project, "Compile"),
            ($@"obj\Debug\{TestEnvironment.TargetFramework}\CsCom\Interop.Scripting\Dictionary.cs", @"CsCom\Interop.Scripting\Dictionary.cs"));
    }

    [TestMethod]
    public void GeneratesTheLateBindingWithDynamicWithItsPackage()
    {
        string project = CreateConsumer(
            "build-package-dynamic",
            """<CsComReference Include="Scripting.Dictionary" />""",
            program: """
                using Interop.Scripting;

                dynamic dictionary = Dictionary.CreateDynamic();
                dictionary.Add("a", "one");
                Console.WriteLine($"Count={dictionary.Count}");
                """,
            properties: "<CsComDynamic>true</CsComDynamic>");
        string directory = Path.GetDirectoryName(project)!;

        // The late binding of the package support code is in CsCom.Runtime.Dynamic, which the project must reference.
        var (exitCode, output) = DotNetCli.Run(["build", project], directory, NullLogger.Instance);
        Assert.AreNotEqual(0, exitCode);
        Assert.IsTrue(
            output.Any(static line => line.Contains("error CSCOM1000: CsComDynamic requires a reference to the CsCom.Runtime.Dynamic package", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, output));

        Assert.AreEqual(0, RunCommand("reference", "add", "Scripting.Dictionary", "--project", project, "--dynamic"));
        Assert.AreEqual("Count=1", Run(project).Last());
    }

    [TestMethod]
    public void GeneratesIntoAFolderOfTheProject()
    {
        // E.g. to commit the generated files, which Solution Explorer then shows in place.
        string project = CreateConsumer(
            "build-package-folder",
            """<CsComReference Include="Scripting.Dictionary" />""",
            program: """
                using Interop.Scripting;

                var dictionary = Dictionary.Create();
                dictionary.Add("a", "one");
                Console.WriteLine($"Count={dictionary.Count}");
                """,
            properties: @"<CsComOutputPath>Generated\CsCom\</CsComOutputPath>");
        string directory = Path.GetDirectoryName(project)!;

        Assert.AreEqual("Count=1", Run(project).Last());
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "CsCom", "Interop.Scripting", "Dictionary.cs")));

        // The folder only gets the generated files: the state, with the paths of this machine, is in the intermediate output.
        // Clean deletes it, so a rebuild generates the code again.
        Assert.AreEqual(0, Directory.GetFiles(Path.Combine(directory, "Generated", "CsCom"), "cscom.*").Length);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "obj", "Debug", TestEnvironment.TargetFramework, "CsCom", "cscom.state")));
        StringAssert.Contains(Build(project), "CsCom: the interop code in");
        Assert.IsFalse(Build(project, "-t:Rebuild").Contains("CsCom: the interop code in", StringComparison.Ordinal));

        // The default globs skip the folder, so the files are compiled once and the state files are not shown.
        Build(project);
        CollectionAssert.Contains(GetItems(project, "Compile"), (@"Generated\CsCom\Interop.Scripting\Dictionary.cs", string.Empty));
        Assert.IsFalse(GetItems(project, "None").Any(static item => item.Path.Contains("cscom.", StringComparison.Ordinal)));

        // Without them in Solution Explorer, the files are only added by the build.
        Assert.IsFalse(GetItems(project, "Compile", "-p:CsComShowGeneratedFiles=false").Any(static item => item.Path.StartsWith("Generated", StringComparison.Ordinal)));
        Build(project, "-p:CsComShowGeneratedFiles=false");

        // The files of removed references are not compiled: they would require unsafe code, which only the build enables.
        File.WriteAllText(project, File.ReadAllText(project).Replace("""<CsComReference Include="Scripting.Dictionary" />""", string.Empty, StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(directory, "Program.cs"), """Console.WriteLine("No references");""");
        Assert.AreEqual("No references", Run(project).Last());
        Assert.IsFalse(GetItems(project, "Compile").Any(static item => item.Path.StartsWith("Generated", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AppliesTheConfigurationAndReportsItsErrors()
    {
        string project = CreateConsumer("build-package-config", """
            <CsComReference Include="Scripting.Dictionary" Config="cscom.xml" />
            """, program: """
            using Interop.Scripting;

            var dictionary = ScriptingDictionary.Create();
            Console.WriteLine(dictionary.Count);
            """);
        string configuration = Path.Combine(Path.GetDirectoryName(project)!, "cscom.xml");
        File.WriteAllText(configuration, "<CsCom>\n  <ModelTransformers>\n    <Rename />\n  </ModelTransformers>\n</CsCom>");

        var (exitCode, output) = DotNetCli.Run(["build", project], Path.GetDirectoryName(project)!, NullLogger.Instance);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsTrue(output.Any(line => line.Contains($"{configuration}(3,6): error CSCOM1000: Unknown model transformer <Rename>.", StringComparison.Ordinal)), string.Join(Environment.NewLine, output));

        File.WriteAllText(configuration, """<CsCom><ModelTransformers><RenameType TypeName="Dictionary" NewName="ScriptingDictionary" /></ModelTransformers></CsCom>""");
        Assert.AreEqual("0", Run(project).Last());
    }

    [TestMethod]
    public void CompilesAgainWhenTheGeneratorOnlyDeletesFiles()
    {
        // The configuration file is not an input of the compiler, and removing a class leaves no file that is newer than the
        // assembly: the state file that the generator writes makes the compiler run.
        string project = CreateConsumer("build-package-deleted", """
            <CsComReference Include="Scripting.Dictionary" Config="cscom.xml" />
            """, program: """
            Console.WriteLine(typeof(Interop.Scripting.Dictionary).Assembly.GetType("Interop.Scripting.Encoder") is not null);
            """);
        string configuration = Path.Combine(Path.GetDirectoryName(project)!, "cscom.xml");
        File.WriteAllText(configuration, "<CsCom />");
        Assert.AreEqual("True", Run(project).Last());

        // The build after the first generation compiles anyway, since its evaluation is the first to include the generated files.
        Build(project);

        File.WriteAllText(configuration, """<CsCom><ModelTransformers><ExcludeType TypeName="Encoder" /></ModelTransformers></CsCom>""");
        Assert.AreEqual("False", Run(project).Last());
    }

    [TestMethod]
    public void ReportsTheWarningsOfTheGeneratorInEveryBuild()
    {
        // The second build does not run the generator, but it reports the warnings again, like the compiler does: with
        // warnings as errors, it would succeed otherwise.
        string project = CreateConsumer(
            "build-package-warnings",
            """
            <CsComReference Include="Scripting.Dictionary" />
            <CsComLibrary Include="Unknown" Namespace="Unknown.Interop" />
            """,
            program: """Console.WriteLine(typeof(Interop.Scripting.Dictionary).Name);""");
        string directory = Path.GetDirectoryName(project)!;

        for (int build = 0; build < 2; build++)
        {
            var (exitCode, output) = DotNetCli.Run(["build", project, "-v", "n", "-warnaserror"], directory, NullLogger.Instance);
            string text = string.Join(Environment.NewLine, output);
            Assert.AreNotEqual(0, exitCode, text);
            StringAssert.Contains(text, "CSCOM2000: No generated library is named 'Unknown'");
            Assert.AreEqual(build == 1, text.Contains("CsCom: the interop code in", StringComparison.Ordinal), text);
        }
    }

    [TestMethod]
    public void GeneratesInternalSupportCode()
    {
        string project = CreateConsumer(
            "build-package-internal",
            $"""
            <CsComReference Include="{TestEnvironment.TestTypeLibrary}" />
            <CsComReference Include="{TestEnvironment.DrawingTypeLibrary}" Namespace="Drawing.Interop" />
            <CsComReference Include="Scripting.Dictionary" />
            """,
            properties: "<CsComSupportCode>internal</CsComSupportCode>");

        var output = Run(project);

        Assert.AreEqual("Count=2", output[^4]);
        string support = Path.Combine(Path.GetDirectoryName(project)!, "obj", "Debug", TestEnvironment.TargetFramework, "CsCom", "Consumer.ComSupport", "IDispatch.cs");
        StringAssert.Contains(File.ReadAllText(support), "internal unsafe partial interface IDispatch");
    }

    [TestMethod]
    public void BuildsTheReferencesAddedByTheCommands()
    {
        // 'cscom reference add' also adds the reference to CsCom.Build.
        string project = CreateConsumer("build-package-commands", references: string.Empty, program: """
            using Interop.Scripting;

            var dictionary = Dictionary.Create();
            dictionary.Add("a", "one");
            dictionary.Add("b", "two");
            Console.WriteLine($"Count={dictionary.Count}");
            Console.WriteLine(typeof(Dictionary).Assembly.GetType("Interop.CsComTest.IShape") is not null);
            """, withPackage: false);
        string directory = Path.GetDirectoryName(project)!;
        File.Copy(TestEnvironment.TestTypeLibrary, Path.Combine(directory, "CsComTest.tlb"));

        Assert.AreEqual(0, RunCommand("reference", "add", Path.Combine(directory, "CsComTest.tlb"), "Scripting.Dictionary", "--project", project, "--event-style", "none"));

        StringAssert.Contains(File.ReadAllText(project), """<CsComReference Include="CsComTest.tlb" EventStyle="none" />""");
        CollectionAssert.AreEqual(new[] { "Count=2", "True" }, Run(project).TakeLast(2).ToArray());

        // The generated files of a removed reference are removed too.
        Assert.AreEqual(0, RunCommand("reference", "remove", "CsComTest.tlb", "--project", project));
        CollectionAssert.AreEqual(new[] { "Count=2", "False" }, Run(project).TakeLast(2).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BuildsWithTheMSBuildOfVisualStudio(bool is64Bit)
    {
        // For the 32-bit MSBuild, System32 is redirected to SysWOW64, e.g. for scrrun.dll.
        string? msbuild = FindVisualStudioMSBuild(is64Bit);
        if (msbuild is null)
        {
            Assert.Inconclusive("The MSBuild of Visual Studio was not found.");
        }

        string project = CreateConsumer($"build-package-vs{(is64Bit ? "64" : "32")}", $"""
            <CsComReference Include="{TestEnvironment.TestTypeLibrary}" />
            <CsComReference Include="{TestEnvironment.DrawingTypeLibrary}" Namespace="Drawing.Interop" />
            <CsComReference Include="Scripting.Dictionary" />
            """);

        var (exitCode, output) = RunProcess(msbuild, ["-restore", "-nologo", "-nodeReuse:false", "-p:TreatWarningsAsErrors=true", project], Path.GetDirectoryName(project)!);

        string text = string.Join(Environment.NewLine, output);
        Assert.AreEqual(0, exitCode, text);
        Assert.IsFalse(output.Any(static line => line.Contains(": warning ", StringComparison.Ordinal)), text);
        Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "bin", "Debug", TestEnvironment.TargetFramework, "Consumer.dll")), text);
    }

    // A console application in an isolated directory, which restores CsCom.Build and CsCom.Runtime from the local feed. It
    // generates documentation and treats warnings as errors, like strict projects.
    private static string CreateConsumer(string name, string references, string program = Program, string properties = "", bool withPackage = true)
    {
        string package = withPackage ? $"<PackageReference Include=\"CsCom.Build\" Version=\"{ToolInfo.Version}\" />" : string.Empty;
        string directory = TestEnvironment.CreateDirectory(name);
        File.WriteAllText(Path.Combine(directory, "nuget.config"), ProjectFile.CreateNuGetConfig(TestEnvironment.LocalPackages, TestEnvironment.GlobalPackagesFolder));
        File.WriteAllText(Path.Combine(directory, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(directory, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(directory, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        string project = Path.Combine(directory, "Consumer.csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestEnvironment.TargetFramework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                {properties}
              </PropertyGroup>
              <ItemGroup>
                {package}
                {references}
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Program.cs"), program);
        return project;
    }

    // Builds the project with detailed messages, so that the messages of the task are included, and checks that it has no warnings.
    private static string Build(string project, params string[] arguments)
    {
        var (exitCode, output) = DotNetCli.Run(["build", project, "-v", "n", .. arguments], Path.GetDirectoryName(project)!, NullLogger.Instance);
        string text = string.Join(Environment.NewLine, output);
        Assert.AreEqual(0, exitCode, text);
        Assert.IsFalse(output.Any(static line => line.Contains(": warning ", StringComparison.Ordinal)), text);
        return text;
    }

    // The items of a type in the evaluation of the project, which Solution Explorer shows: their paths and links.
    private static List<(string Path, string Link)> GetItems(string project, string itemType, params string[] arguments)
    {
        var (exitCode, output) = DotNetCli.Run(["msbuild", project, $"-getItem:{itemType}", .. arguments], Path.GetDirectoryName(project)!, NullLogger.Instance);
        string text = string.Join(Environment.NewLine, output);
        Assert.AreEqual(0, exitCode, text);
        using var json = JsonDocument.Parse(text[text.IndexOf('{', StringComparison.Ordinal)..]);
        return json.RootElement.GetProperty("Items").TryGetProperty(itemType, out var items)
            ? [.. items.EnumerateArray().Select(static item => (
                item.GetProperty("Identity").GetString()!,
                item.TryGetProperty("Link", out var link) ? link.GetString() ?? string.Empty : string.Empty))]
            : [];
    }

    private static int RunCommand(params string[] args) => CommandLine.Create().Parse([.. args, "-v", "quiet"]).Invoke(new System.CommandLine.InvocationConfiguration
    {
        Output = TextWriter.Null,
        Error = TextWriter.Null,
    });

    private static List<string> Run(string project)
    {
        Build(project);
        var (exitCode, output) = DotNetCli.Run(["run", "--no-build", "--project", project], Path.GetDirectoryName(project)!, NullLogger.Instance);
        Assert.AreEqual(0, exitCode, string.Join(Environment.NewLine, output));
        return output.Select(static line => line.Trim()).Where(static line => line.Length > 0).ToList();
    }

    private static string? FindVisualStudioMSBuild(bool is64Bit)
    {
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere))
        {
            return null;
        }

        var (exitCode, output) = RunProcess(vswhere, ["-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\Current\Bin\**\MSBuild.exe"], Environment.CurrentDirectory);
        return exitCode == 0
            ? output.FirstOrDefault(path => File.Exists(path) && Path.GetFileName(Path.GetDirectoryName(path)) == (is64Bit ? "amd64" : "Bin"))
            : null;
    }

    private static (int ExitCode, List<string> Output) RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The variables of the test host would make MSBuild use the MSBuild of the .NET SDK.
        foreach (string variable in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly", "MSBUILDNOINPROCNODE" })
        {
            startInfo.Environment.Remove(variable);
        }

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            output.Add(line);
        }

        output.AddRange(error.Result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}
