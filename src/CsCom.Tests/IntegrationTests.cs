using CsCom.CodeGeneration;
using CsCom.Model;
using CsCom.Output;
using CsCom.Transformations;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Tests;

/// <summary>
/// Tests that build the generated projects with the .NET SDK and run them.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class IntegrationTests
{
    private readonly InteropPipeline _pipeline = new();

    [TestMethod]
    [DataRow(SupportCodeMode.Package, false)]
    [DataRow(SupportCodeMode.Public, false)]
    [DataRow(SupportCodeMode.Internal, true)]
    public void GeneratedTestProjectBuildsWithoutWarnings(SupportCodeMode supportCode, bool singleFile)
    {
        string directory = TestEnvironment.CreateDirectory($"build-{supportCode}");
        string project = CreateProject(TestEnvironment.TestTypeLibrary, directory, supportCode, singleFile);

        Build(project);
    }

    [TestMethod]
    public void GeneratedScriptingRuntimeProjectBuildsWithoutWarnings()
    {
        string directory = TestEnvironment.CreateDirectory("build-scrrun");
        string project = CreateProject(TestEnvironment.ScriptingRuntime, directory, SupportCodeMode.Package, singleFile: false);

        Build(project);
    }

    [TestMethod]
    public void GeneratedProjectCanBeUsedFromCSharp15()
    {
        if (!HasSdk(11))
        {
            Assert.Inconclusive("The .NET 11 SDK is not installed.");
        }

        string directory = TestEnvironment.CreateDirectory("build-csharp15");
        string project = new ProjectGenerator(_pipeline).CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = directory,
            TargetFrameworks = ["net11.0-windows"],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        Build(project);
    }

    [TestMethod]
    public void ConsumerOfScriptingRuntimeRuns()
    {
        string root = TestEnvironment.CreateDirectory("consumer");
        string interop = CreateProject(TestEnvironment.ScriptingRuntime, Path.Combine(root, "Interop"), SupportCodeMode.Package, singleFile: false);

        var output = RunConsumer(root, interop, """
            using Interop.Scripting;

            IDictionary dictionary = Dictionary.Create();
            dictionary.CompareMode = CompareMethod.TextCompare;
            dictionary.Add("a", 1);
            dictionary.Add("b", "two");
            Console.WriteLine($"Count={dictionary.Count}");
            Console.WriteLine($"Exists={dictionary.Exists("A")}");
            Console.WriteLine($"Item={dictionary.get_Item("b")}");
            Console.WriteLine($"Keys={string.Join(",", (object?[])dictionary.Keys()!)}");
            foreach (var key in dictionary)
            {
                Console.WriteLine($"Key={key}");
            }

            IFileSystem3 fileSystem = FileSystemObject.Create();
            string path = Path.Combine(Path.GetTempPath(), fileSystem.GetTempName()!);
            ITextStream writer = fileSystem.CreateTextFile(path)!;
            writer.WriteLine("Hello");
            writer.Close();
            ITextStream reader = fileSystem.OpenTextFile(path)!;
            Console.WriteLine($"Line={reader.ReadLine()}");
            reader.Close();
            fileSystem.DeleteFile(path);
            Console.WriteLine($"Exists={fileSystem.FileExists(path)}");
            """);

        CollectionAssert.IsSubsetOf(
            new[] { "Count=2", "Exists=True", "Item=two", "Keys=a,b", "Key=a", "Key=b", "Line=Hello", "Exists=False" },
            output.ToArray(),
            string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    public void LateBoundWrappersOfScriptingRuntimeRun()
    {
        // Dispinterfaces are invoked through IDispatch::Invoke. The Scripting Runtime has no dispinterfaces,
        // so its dual interfaces are turned into dispinterfaces by a model transformer.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new LateBindingTransformer());
        string root = TestEnvironment.CreateDirectory("late-bound");
        string interop = new ProjectGenerator(pipeline).CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.ScriptingRuntime,
            OutputDirectory = Path.Combine(root, "Interop"),
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        var output = RunConsumer(root, interop, """
            using Interop.Scripting;

            IDictionary dictionary = Dictionary.Create();
            dictionary.Add("a", 1);
            dictionary.Add("b", "two");
            dictionary["c"] = 3.5;
            Console.WriteLine($"Count={dictionary.Count}");
            Console.WriteLine($"Item={dictionary["b"]}");
            Console.WriteLine($"Exists={dictionary.Exists("c")}");
            Console.WriteLine($"Keys={string.Join(",", (object?[])dictionary.Keys()!)}");
            foreach (var key in dictionary)
            {
                Console.WriteLine($"Key={key}");
            }

            try
            {
                dictionary.Add("a", 2);
            }
            catch (System.Runtime.InteropServices.COMException e)
            {
                Console.WriteLine($"Error=0x{e.HResult:X8}");
            }

            IFileSystem3 fileSystem = FileSystemObject.Create();
            IFolder folder = fileSystem.GetFolder(Path.GetTempPath())!;
            Console.WriteLine($"Folder={folder.Path?.TrimEnd('\\') == Path.GetTempPath().TrimEnd('\\')}");
            Console.WriteLine($"Drive={folder.Drive?.DriveLetter?.Length}");
            """);

        CollectionAssert.IsSubsetOf(
            new[] { "Count=3", "Item=two", "Exists=True", "Keys=a,b,c", "Key=a", "Key=c", "Error=0x800A01C9", "Folder=True", "Drive=1" },
            output.ToArray(),
            string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    public void NewAssemblyCopiesTheOutputsOfEachRuntime()
    {
        string output = TestEnvironment.CreateDirectory("assembly");

        var files = new AssemblyGenerator(_pipeline).CreateAssembly(new NewAssemblyRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = output,
            RuntimeIdentifiers = ["win-x64", "win-arm64"],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        foreach (string runtime in new[] { "win-x64", "win-arm64" })
        {
            string directory = Path.Combine(output, "net10.0-windows", runtime);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "Interop.CsComTest.dll")), directory);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "Interop.CsComTest.xml")), directory);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "CsCom.Runtime.dll")), directory);
        }

        Assert.IsTrue(files.All(File.Exists));
    }

    [TestMethod]
    public void NewAssemblyUsesFlatLayoutForSingleTarget()
    {
        string output = TestEnvironment.CreateDirectory("assembly-flat");

        new AssemblyGenerator(_pipeline).CreateAssembly(new NewAssemblyRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = output,
            SupportCode = SupportCodeMode.Internal,
            Name = "Flat.Interop",
        });

        CollectionAssert.AreEquivalent(
            new[] { "Flat.Interop.dll", "Flat.Interop.pdb", "Flat.Interop.xml" },
            Directory.GetFiles(output).Select(Path.GetFileName).ToArray());
    }

    /// <summary>
    /// Builds and runs a console application that references the interop project.
    /// </summary>
    /// <returns>The trimmed output lines.</returns>
    private static List<string> RunConsumer(string root, string interopProject, string program)
    {
        string consumer = Path.Combine(root, "Consumer");
        Directory.CreateDirectory(consumer);
        File.WriteAllText(Path.Combine(consumer, "nuget.config"), ProjectFile.CreateNuGetConfig(TestEnvironment.LocalPackages, TestEnvironment.GlobalPackagesFolder));
        File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-windows</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{interopProject}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(consumer, "Program.cs"), program);

        var (exitCode, output) = DotNetCli.Run(["run", "--project", Path.Combine(consumer, "Consumer.csproj")], consumer, NullLogger.Instance);

        Assert.AreEqual(0, exitCode, string.Join(Environment.NewLine, output));
        return output.Select(static line => line.Trim()).ToList();
    }

    private string CreateProject(string typeLibrary, string directory, SupportCodeMode supportCode, bool singleFile) =>
        new ProjectGenerator(_pipeline).CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = typeLibrary,
            OutputDirectory = directory,
            SupportCode = supportCode,
            SingleFile = singleFile,
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

    private static void Build(string project)
    {
        var (exitCode, output) = DotNetCli.Run(["build", project, "-p:TreatWarningsAsErrors=true"], Path.GetDirectoryName(project)!, NullLogger.Instance);

        string text = string.Join(Environment.NewLine, output);
        Assert.AreEqual(0, exitCode, text);
        var warnings = output.Where(static line => line.Contains(": warning ", StringComparison.Ordinal)).ToList();
        Assert.IsEmpty(warnings, text);
    }

    private static bool HasSdk(int major)
    {
        var (exitCode, output) = DotNetCli.Run(["--list-sdks"], Environment.CurrentDirectory, NullLogger.Instance);
        return exitCode == 0 && output.Any(line => line.StartsWith($"{major}.", StringComparison.Ordinal));
    }
}

/// <summary>
/// Turns dual interfaces into dispinterfaces, so that they are invoked through late-bound wrappers.
/// Like the members of real dispinterfaces, the members of the base interfaces are included.
/// </summary>
internal sealed class LateBindingTransformer : IModelTransformer
{
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        var duals = library.Types.OfType<InterfaceDefinition>().Where(static i => i.InterfaceKind == InterfaceKind.Dual).ToList();
        var methods = duals.ToDictionary(static i => i, GetAllMethods);
        foreach (var @interface in duals)
        {
            @interface.InterfaceKind = InterfaceKind.Dispatch;
            @interface.BaseInterface = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH);
            @interface.Methods.Clear();
            @interface.Methods.AddRange(methods[@interface]);
        }
    }

    private static List<MethodDefinition> GetAllMethods(InterfaceDefinition @interface) =>
        @interface.BaseInterface is UserDefinedTypeReference { Definition: InterfaceDefinition baseInterface }
            ? [.. GetAllMethods(baseInterface), .. @interface.Methods]
            : [.. @interface.Methods];
}
