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
            using CsCom.Runtime;
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
            IFolder folder = fileSystem.GetFolder(Path.GetTempPath())!;
            Console.WriteLine($"Created={folder.DateCreated.Year > 1990 && folder.DateCreated <= DateTime.Now}");

            dynamic late = Dictionary.CreateDynamic();
            late.Add("x", 42);
            Console.WriteLine($"Dynamic={late.Item("x")}|{late.Count}");
            Console.WriteLine($"AsDynamic={dictionary.AsDynamic().Count}|{fileSystem.AsDynamic().GetFolder(Path.GetTempPath()).Drive.IsReady}");
            """);

        CollectionAssert.IsSubsetOf(
            new[] { "Count=2", "Exists=True", "Item=two", "Keys=a,b", "Key=a", "Key=b", "Line=Hello", "Exists=False", "Created=True", "Dynamic=42|1", "AsDynamic=2|True" },
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
    public void MarshallersRoundTripThroughComWrappers()
    {
        // The consumer implements IMarshalling and calls it through a runtime callable wrapper of its own COM callable wrapper,
        // so that every argument is marshalled to native code and back by the generated stubs.
        string root = TestEnvironment.CreateDirectory("marshallers");
        string interop = CreateProject(TestEnvironment.TestTypeLibrary, Path.Combine(root, "Interop"), SupportCodeMode.Package, singleFile: false);

        var output = RunConsumer(root, interop, """
            using System.Globalization;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using CsCom.Runtime;
            using Interop.CsComTest;

            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            var wrappers = new StrategyBasedComWrappers();
            var implementation = new MarshallingImpl();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(implementation, CreateComInterfaceFlags.None);
            var proxy = (IMarshalling)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Marshal.Release(unknown);
            Console.WriteLine($"Proxy={!ReferenceEquals(proxy, implementation)}");

            var date = new DateTime(2024, 2, 29, 12, 30, 0);
            DateTime result = proxy.Dates(date, out DateTime next);
            Console.WriteLine($"Dates={result:yyyy-MM-dd HH:mm}|{next:yyyy-MM-dd}");

            decimal total = 10.5m;
            decimal money = proxy.Money(1.25m, ref total);
            Console.WriteLine($"Money={money:0.####}|{total:0.####}");

            Console.WriteLine($"Strings={string.Join(",", proxy.Strings(["a", null, "c"])!.Select(static s => s ?? "<null>"))}");
            Console.WriteLine($"Variants={string.Join(",", proxy.Variants([1, "two", true, null])!.Select(static v => v?.ToString() ?? "<null>"))}");

            int[]? numbers = [1, 2, 3];
            proxy.Numbers(ref numbers);
            Console.WriteLine($"Numbers={string.Join(",", numbers!)}");
            Console.WriteLine($"Flags={string.Join(",", proxy.Flags([true, false, true])!)}");
            Console.WriteLine($"Colors={string.Join(",", proxy.Colors([Color.Red, Color.Blue])!)}");
            Console.WriteLine($"DateArray={string.Join(",", proxy.DateArray([date])!.Select(static d => d.ToString("yyyy-MM-dd")))}");

            object?[] unknowns = proxy.Unknowns([implementation, null])!;
            Console.WriteLine($"Unknowns={ReferenceEquals(unknowns[0], implementation)}|{unknowns[1] is null}");

            var calculator = new CalculatorImpl();
            ICalculator?[] calculators = proxy.Calculators([calculator])!;
            Console.WriteLine($"Calculators={ReferenceEquals(calculators[0], calculator)}|{calculators[0]!.Add(2, 3)}");

            var dictionary = ComActivator.CreateInstance<IDispatch>(new Guid("EE09B103-97E0-11CF-978F-00A02463E06F"));
            Console.WriteLine($"Dispatches={proxy.Dispatches([dictionary, null, dictionary])}");

            [GeneratedComClass]
            internal sealed partial class MarshallingImpl : IMarshalling
            {
                public DateTime Dates(DateTime date, out DateTime next)
                {
                    next = date.AddDays(1);
                    return date.AddHours(1);
                }

                public decimal Money(decimal money, ref decimal total)
                {
                    total += money;
                    return money * 2;
                }

                public string?[]? Strings(string?[]? values) => values?.Reverse().ToArray();

                public object?[]? Variants(in object?[]? values) => values;

                public void Numbers(ref int[]? values) => values = values?.Select(static v => v * 10).ToArray();

                public double[]? Flags(bool[]? values) => values?.Select(static v => v ? 1.0 : 0.0).ToArray();

                public Color[]? Colors(Color[]? values) => values?.Reverse().ToArray();

                public DateTime[]? DateArray(DateTime[]? values) => values?.Select(static d => d.AddYears(1)).ToArray();

                public object?[]? Unknowns(object?[]? values) => values;

                public ICalculator?[]? Calculators(ICalculator?[]? values) => values;

                public int Dispatches(IDispatch?[]? values) => values?.Count(static v => v is not null) ?? -1;

                public void Unsupported(nint points, nint Money)
                {
                }
            }

            [GeneratedComClass]
            internal sealed unsafe partial class CalculatorImpl : ICalculator
            {
                public int Add(int a, int b) => a + b;

                public void Divide(double a, double b, out double quotient, out double remainder) => throw new NotSupportedException();

                public void Swap(ref int a, ref int b) => throw new NotSupportedException();

                public Point GetPoint() => throw new NotSupportedException();

                public void SetPoint(Point Point) => throw new NotSupportedException();

                public void MovePoint(in Point Point) => throw new NotSupportedException();

                public int NoHResult(int Value) => throw new NotSupportedException();

                public void NoReturn(int Value) => throw new NotSupportedException();

                public string? Strings(string? bstr, string? wide, string? ansi) => throw new NotSupportedException();

                public int[]? Arrays(string?[]? Values) => throw new NotSupportedException();

                public void Buffer(int size, ref byte Buffer) => throw new NotSupportedException();

                public void VoidPointer(void* pointer) => throw new NotSupportedException();

                public int GetHandle() => throw new NotSupportedException();

                public int Locale(int lcid) => throw new NotSupportedException();

                public int Keywords(int @params, int @lock, int Value) => throw new NotSupportedException();

                public int Numbers(Number Number, in Record Record, in Guid Id) => throw new NotSupportedException();

                public void Integers(sbyte c, byte b, short s, ushort us, int i, uint ui, long h, ulong uh, float f) => throw new NotSupportedException();

                public IEnumVARIANT? enumerator() => throw new NotSupportedException();
            }
            """, allowUnsafe: true);

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "Proxy=True",
                "Dates=2024-02-29 13:30|2024-03-01",
                "Money=2.5|11.75",
                "Strings=c,<null>,a",
                "Variants=1,two,True,<null>",
                "Numbers=10,20,30",
                "Flags=1,0,1",
                "Colors=Blue,Red",
                "DateArray=2025-02-28",
                "Unknowns=True|True",
                "Calculators=True|5",
                "Dispatches=2",
            },
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
    private static List<string> RunConsumer(string root, string interopProject, string program, bool allowUnsafe = false)
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
                <AllowUnsafeBlocks>{(allowUnsafe ? "true" : "false")}</AllowUnsafeBlocks>
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
