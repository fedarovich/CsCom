using CsCom.CodeGeneration;
using CsCom.LibrarySets;
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

    // The temporary projects of the assemblies are created in a directory of the test run, which the tests clean up,
    // rather than in the directory of the tool, where the project of a build that fails is kept.
    private static readonly string TemporaryProjects = TestEnvironment.CreateDirectory("assembly-projects");

    private AssemblyGenerator CreateAssemblyGenerator() => new(_pipeline) { TemporaryRoot = TemporaryProjects };

    [TestMethod]
    [DataRow(SupportCodeMode.Package, false, "default", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Public, false, "default", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Internal, true, "default", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Package, false, "none", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Internal, false, "wrapper", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Public, true, "default", EventStyle.Observable, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Internal, false, "wrapper", EventStyle.Interface, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Package, false, "default", EventStyle.None, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Package, false, "safearray-sysarray", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Internal, true, "safearray-wrapper", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Public, false, "safearray-none", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Internal, true, "bstr-wrapper", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Package, false, "bstr-none", EventStyle.Events, StructProperties.Auto)]
    [DataRow(SupportCodeMode.Internal, true, "wrapper", EventStyle.Events, StructProperties.Never)]
    public void GeneratedTestProjectBuildsWithoutWarnings(SupportCodeMode supportCode, bool singleFile, string marshalling, EventStyle eventStyle, StructProperties structProperties)
    {
        string directory = TestEnvironment.CreateDirectory($"build-{supportCode}-{marshalling}-{eventStyle}-{structProperties}");
        var options = marshalling switch
        {
            "none" => new MarshallingOptions { Dates = ValueMarshalling.None, Currencies = ValueMarshalling.None, Booleans = ValueMarshalling.None },
            "wrapper" => AllWrappers,
            "safearray-sysarray" => new MarshallingOptions { SafeArrays = SafeArrayMarshalling.SysArray },
            "safearray-wrapper" => AllWrappers with { SafeArrays = SafeArrayMarshalling.Wrapper },
            "safearray-none" => new MarshallingOptions { SafeArrays = SafeArrayMarshalling.None },
            "bstr-wrapper" => new MarshallingOptions { Bstrs = BstrMarshalling.Wrapper },
            "bstr-none" => new MarshallingOptions { Bstrs = BstrMarshalling.None },
            _ => new MarshallingOptions(),
        };
        string project = CreateProject(TestEnvironment.TestTypeLibrary, directory, supportCode, singleFile, options, eventStyle, structProperties);

        Build(project);
    }

    [TestMethod]
    public void ProjectGeneratedFromInteropAssemblyBuildsWithoutWarnings()
    {
        string directory = TestEnvironment.CreateDirectory("build-interop-assembly");
        string project = CreateProject(TestEnvironment.TestInteropAssembly, directory, SupportCodeMode.Package, singleFile: false);

        Build(project);
    }

    [TestMethod]
    public void ProjectGeneratedFromComImportDeclarationsBuildsWithoutWarnings()
    {
        // The constructs of the fixture that type libraries cannot describe are imported as types that compile.
        string directory = TestEnvironment.CreateDirectory("build-hand-written");
        string assembly = Path.Combine(directory, "HandWritten.dll");
        TestCompiler.Compile("HandWritten", InteropAssemblyTests.HandWrittenSource, assembly);
        string project = CreateProject(assembly, Path.Combine(directory, "Interop"), SupportCodeMode.Package, singleFile: false);

        Build(project);
    }

    [TestMethod]
    public void HandWrittenDeclarationsWorkThroughGeneratedCode()
    {
        // Declarations of the built-in COM interop for the first members of the IDictionary interface of the Scripting
        // Runtime, in the order of its vtable, and of its Dictionary coclass. The generated code calls the real object.
        string root = TestEnvironment.CreateDirectory("consumer-hand-written");
        string assembly = Path.Combine(root, "HandScripting.dll");
        TestCompiler.Compile(
            "HandScripting",
            """
            using System.Runtime.InteropServices;

            namespace HandScripting
            {
                [ComImport, Guid("42C642C1-97E1-11CF-978F-00A02463E06F")]
                public interface IDictionary
                {
                    [DispId(0)] void PutRefItem([In] ref object Key, [In] ref object pRetItem);
                    [DispId(0)] void PutItem([In] ref object Key, [In] ref object pRetItem);
                    [DispId(0)] object GetItem([In] ref object Key);
                    [DispId(1)] void Add([In] ref object Key, [In] ref object Item);
                    int Count { [DispId(2)] get; }
                }

                [ComImport, Guid("EE09B103-97E0-11CF-978F-00A02463E06F")]
                public class DictionaryClass
                {
                }

                [ComImport, Guid("42C642C1-97E1-11CF-978F-00A02463E06F"), CoClass(typeof(DictionaryClass))]
                public interface Dictionary : IDictionary
                {
                }
            }
            """,
            assembly);
        string interop = CreateProject(assembly, Path.Combine(root, "Interop"), SupportCodeMode.Package, singleFile: false);

        var output = RunConsumer(root, interop, """
            using Interop.HandScripting;

            IDictionary dictionary = Dictionary.Create();
            dictionary.Add("a", 1);
            dictionary.Add("b", "two");
            Console.WriteLine($"Count={dictionary.Count}");
            Console.WriteLine($"Item={dictionary.GetItem("b")}");
            """);

        CollectionAssert.IsSubsetOf(new[] { "Count=2", "Item=two" }, output.ToArray(), string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    public void GeneratedScriptingRuntimeProjectBuildsWithoutWarnings()
    {
        string directory = TestEnvironment.CreateDirectory("build-scrrun");
        string project = CreateProject(TestEnvironment.ScriptingRuntime, directory, SupportCodeMode.Package, singleFile: false);

        Build(project);
    }

    [TestMethod]
    public void ConfiguredProjectBuildsWithoutWarnings()
    {
        string directory = TestEnvironment.CreateDirectory("build-configured");
        string configuration = Path.Combine(directory, "cscom.xml");
        File.WriteAllText(configuration, """
            <CsCom>
              <ModelTransformers>
                <RenameType Type="^I(Shape|Circle)$" NewName="I$1Contract" />
                <RenameMember Type="^IShapeContract$" Member="^Move$" NewName="MoveBy" />
                <RenameMember Type="^IShapeContract$" Member="^MoveBy$" Parameter="^d(.)$" NewName="delta$1" />
                <RenameMember Type="^Record$" Member="^checked$" NewName="IsChecked" />
                <SetMarshalling Type="^IArrays$" Member="^Matrix$" SafeArrays="wrapper" />
                <SetMarshalling Type="^IStrings$" Member="^Echo$" Parameter="^Value$" Bstrs="none" />
                <SetMarshalling Type="^IShapeContract$" Member="^Name$" Parameter="(?i)^name$" Bstrs="wrapper" />
                <SetMarshalling Type="^IShapeContract$" Member="^Dates$" Dates="wrapper" Currencies="none" />
                <SetMarshalling Type="^Record$" Member="^(Date|Flag)$" Dates="wrapper" Booleans="none" />
                <SetMarshalling Type="^IShapeEventsDual$" Member="^Renamed$" Bstrs="wrapper" />
                <SetMarshalling Type="^DShapeEvents$" Member="^Names$" SafeArrays="sysarray" />
                <RemoveEnumPrefix />
                <ExcludeType TypeName="IShapeEventsVt" />
                <ExcludeType Kind="module" />
                <SetType TypeName="ICalculator" MemberName="Add" ParameterName="a" To="Color" />
                <SetType TypeName="IShapeContract" MemberName="Dispatch" To="IShapeContract" />
                <SetSafeArrayRank TypeName="IArrays" MemberName="Matrix" Rank="2" />
                <SetSafeArrayRank TypeName="IMarshalling" MemberName="Colors" Rank="3" />
                <SetSafeArrayRank TypeName="DShapeEvents" MemberName="Names" Rank="2" />
              </ModelTransformers>
              <SyntaxTransformers>
                <AddFlagsAttribute Type="^(Color|AnonymousEnum)$" />
                <AddAttribute TypeName="ICircleContract" Attribute="System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)" />
                <AddAttribute TypeName="IShapeContract" Role="Extensions" Attribute='System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0001")' />
                <AddAttribute TypeName="IShapeContract" MemberName="MoveBy" Attribute="System.Diagnostics.DebuggerHidden" />
              </SyntaxTransformers>
            </CsCom>
            """);

        string project = new ProjectGenerator(_pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            ConfigurationPath = configuration,
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        string generated = Path.Combine(directory, "Generated", "Interop.CsComTest");
        StringAssert.Contains(File.ReadAllText(Path.Combine(generated, "IShapeContract.cs")), "void MoveBy(int deltax, int deltay = 0,");
        StringAssert.Contains(File.ReadAllText(Path.Combine(generated, "Color.cs")), "[global::System.Flags]");
        StringAssert.Contains(File.ReadAllText(Path.Combine(generated, "ICircleContract.cs")), "[System.ComponentModel.EditorBrowsable(");
        Assert.IsFalse(File.Exists(Path.Combine(generated, "IShapeEventsVt.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(generated, "Kernel.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(generated, "CsComSafeArrayMarshallers.cs")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(generated, "Record.cs")), "ComBstrValue Name;");
        Build(project);
    }

    [TestMethod]
    [DataRow(SupportCodeMode.Package)]
    [DataRow(SupportCodeMode.Public)]
    public void MultiTargetedProjectBuildsWithoutWarnings(SupportCodeMode supportCode)
    {
        // Each target framework compiles against the builds of CsCom.Runtime and CsCom.Runtime.Dynamic for its .NET version,
        // or the support code, with C# 14, the version that all of them support. The support code is qualified against
        // .NET 10 when CsCom.Core is built, and compiles for .NET 11 too.
        string directory = TestEnvironment.CreateDirectory($"build-multi-target-{supportCode}");
        string project = new ProjectGenerator(_pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.ScriptingRuntime],
            OutputDirectory = directory,
            SupportCode = supportCode,
            Dynamic = true,
            TargetFrameworks = SupportedTargetFrameworks,
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        StringAssert.Contains(File.ReadAllText(project), "<LangVersion>14</LangVersion>");
        Build(project);
    }

    [TestMethod]
    public void ConsumerOfScriptingRuntimeRuns()
    {
        string root = TestEnvironment.CreateDirectory("consumer");
        string interop = CreateProject(TestEnvironment.ScriptingRuntime, Path.Combine(root, "Interop"), SupportCodeMode.Package, singleFile: false, dynamic: true);

        var output = RunConsumer(root, interop, """
            using System.Reflection;
            using System.Runtime.Versioning;
            using CsCom.Runtime;
            using Interop.Scripting;

            // The packages resolve to the builds of the runtime for the target framework of the consumer.
            Console.WriteLine($"Runtime={typeof(ComActivator).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName}");
            Console.WriteLine($"DynamicRuntime={typeof(ComDynamic).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName}");

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
            new[]
            {
                $"Runtime={TestEnvironment.FrameworkName}", $"DynamicRuntime={TestEnvironment.FrameworkName}",
                "Count=2", "Exists=True", "Item=two", "Keys=a,b", "Key=a", "Key=b", "Line=Hello", "Exists=False", "Created=True", "Dynamic=42|1", "AsDynamic=2|True",
            },
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
        string interop = new ProjectGenerator(pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.ScriptingRuntime],
            OutputDirectory = Path.Combine(root, "Interop"),
            TargetFrameworks = [TestEnvironment.TargetFramework],
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
    [DataRow(false)]
    [DataRow(true)]
    public void MarshallersRoundTripThroughComWrappers(bool wrappers)
    {
        // The consumer implements IMarshalling and calls it through a runtime callable wrapper of its own COM callable wrapper,
        // so that every argument is marshalled to native code and back by the generated stubs. The same code compiles with
        // the managed types and with the wrappers, which convert implicitly to and from them.
        string root = TestEnvironment.CreateDirectory(wrappers ? "marshallers-wrappers" : "marshallers");
        string interop = CreateProject(
            TestEnvironment.TestTypeLibrary,
            Path.Combine(root, "Interop"),
            SupportCodeMode.Package,
            singleFile: false,
            wrappers ? AllWrappers : new MarshallingOptions());

        string program = """
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
            DateTime result = proxy.Dates(date, out var next);
            DateTime nextDate = next;
            Console.WriteLine($"Dates={result:yyyy-MM-dd HH:mm}|{nextDate:yyyy-MM-dd}");

            __CURRENCY__ total = 10.5m;
            decimal money = proxy.Money(1.25m, ref total);
            decimal totalValue = total;
            Console.WriteLine($"Money={money:0.####}|{totalValue:0.####}");
            __RECORD__

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
                public __DATE__ Dates(__DATE__ date, out __DATE__ next)
                {
                    next = ((DateTime)date).AddDays(1);
                    return ((DateTime)date).AddHours(1);
                }

                public __CURRENCY__ Money(__CURRENCY__ money, ref __CURRENCY__ total)
                {
                    total = (decimal)total + (decimal)money;
                    return (decimal)money * 2;
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
            """;

        program = program
            .Replace("__DATE__", wrappers ? "ComDate" : "DateTime", StringComparison.Ordinal)
            .Replace("__CURRENCY__", wrappers ? "ComCurrency" : "decimal", StringComparison.Ordinal)
            .Replace("__RECORD__", """
                // The members of the structure are wrapper fields, or properties that convert private fields of the COM types.
                var record = new Record { Date = date, Money = 12.5m, Flag = true };
                DateTime recordDate = record.Date;
                decimal recordMoney = record.Money;
                bool recordFlag = record.Flag;
                Console.WriteLine($"Record={recordDate:yyyy-MM-dd}|{recordMoney:0.##}|{recordFlag}|{__FLAG_VALUE__}");
                """, StringComparison.Ordinal)
            .Replace("__FLAG_VALUE__", wrappers
                ? "record.Flag.Value"
                : "(short)typeof(Record).GetField(\"_flag\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(record)!",
                StringComparison.Ordinal);
        var output = RunConsumer(root, interop, program, allowUnsafe: true);

        string[] expected =
        [
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
            "Record=2024-02-29|12.5|True|-1",
        ];
        CollectionAssert.IsSubsetOf(
            expected,
            output.ToArray(),
            string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    [DataRow(SafeArrayMarshalling.Array)]
    [DataRow(SafeArrayMarshalling.SysArray)]
    [DataRow(SafeArrayMarshalling.Wrapper)]
    [DataRow(SafeArrayMarshalling.None)]
    public void SafeArraysRoundTripThroughComWrappers(SafeArrayMarshalling mode)
    {
        // Like MarshallersRoundTripThroughComWrappers, the consumer implements IArrays and calls it through a runtime callable
        // wrapper of its own COM callable wrapper, so that the arrays and their ownership go through both directions of the
        // generated stubs. VARIANTs hold SAFEARRAYs of any rank in every mode.
        string root = TestEnvironment.CreateDirectory($"safearrays-{mode}");
        string interop = CreateProject(
            TestEnvironment.TestTypeLibrary,
            Path.Combine(root, "Interop"),
            SupportCodeMode.Package,
            singleFile: false,
            new MarshallingOptions { SafeArrays = mode });

        string program = """
            using System.Globalization;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using CsCom.Runtime;
            using Interop.CsComTest;

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var wrappers = new StrategyBasedComWrappers();
            var implementation = new ArraysImpl();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(implementation, CreateComInterfaceFlags.None);
            var proxy = (IArrays)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Marshal.Release(unknown);

            var cells = (object?[,])proxy.Values(new object?[,] { { 1, "a" }, { 2.5, null } })!;
            Console.WriteLine($"Values={cells.GetLength(0)}x{cells.GetLength(1)}|{Join(cells)}");
            var cube = new int[2, 3, 4];
            for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 4; k++) cube[i, j, k] = (i * 100) + (j * 10) + k;
            var cubeResult = (int[,,])proxy.Values(cube)!;
            Console.WriteLine($"Cube={cubeResult.GetLength(0)}x{cubeResult.GetLength(1)}x{cubeResult.GetLength(2)}|{cubeResult[1, 2, 3]}|{cubeResult[0, 1, 2]}");

            __CALLS__

            static string Join(Array array) => string.Join(",", array.Cast<object?>().Select(static v => v?.ToString() ?? "<null>"));

            [GeneratedComClass]
            internal sealed partial class ArraysImpl : IArrays
            {
                __IMPL__

                public object? Values(object? value) => value;
            }
            """;

        (string calls, string implementation, string[] expected) = mode switch
        {
            SafeArrayMarshalling.Array => (
                """
                Console.WriteLine($"Matrix={Join(proxy.Matrix([1, 2, 3])!)}");
                string?[]? names = ["a", "b"];
                proxy.Names(ref names);
                Console.WriteLine($"Names={Join(names!)}");
                """,
                """
                public double[]? Matrix(double[]? values) => values?.Select(static v => v * 2).ToArray();

                public void Names(ref string?[]? names) => names = names?.Select(static n => n + "!").ToArray();
                """,
                new[] { "Matrix=2,4,6", "Names=a!,b!" }),
            SafeArrayMarshalling.SysArray => (
                """
                var matrix = (double[,])proxy.Matrix(new double[,] { { 1, 2, 3 }, { 10, 20, 30 } })!;
                Console.WriteLine($"Matrix={matrix.GetLength(0)}x{matrix.GetLength(1)}|{Join(matrix)}");
                Array? names = new string?[,] { { "a", "b" }, { "c", null } };
                proxy.Names(ref names);
                Console.WriteLine($"Names={Join(names!)}");
                names = new string?[] { "one" };
                proxy.Names(ref names);
                Console.WriteLine($"Replaced={names!.Rank}|{Join(names)}");
                """,
                """
                public Array? Matrix(Array? values)
                {
                    var matrix = (double[,])values!;
                    var result = new double[matrix.GetLength(0), matrix.GetLength(1)];
                    for (int i = 0; i < matrix.GetLength(0); i++) for (int j = 0; j < matrix.GetLength(1); j++) result[i, j] = matrix[i, j] * 2;
                    return result;
                }

                public void Names(ref Array? names)
                {
                    if (names is string?[,] grid)
                    {
                        grid[0, 1] = "changed";
                    }
                    else
                    {
                        names = new[] { "new" };
                    }
                }
                """,
                new[] { "Matrix=2x3|2,4,6,20,40,60", "Names=a,changed,c,<null>", "Replaced=1|new" }),
            SafeArrayMarshalling.Wrapper => (
                """
                using var input = ComSafeArray<double>.Create(new double[,] { { 1, 2, 3 }, { 10, 20, 30 } });
                using var result = proxy.Matrix(input)!;
                Console.WriteLine($"Matrix={result.Rank}|{result.GetLowerBound(0)}|{result[2, 3]}|{Join(result.ToArray2D())}");
                Console.WriteLine($"InputAlive={input.Length}|ViewInvalid={IsDisposed(ArraysImpl.View!)}");
                var names = ComSafeArray<string?>.Create(new string?[,] { { "a", "b" }, { "c", null } });
                var original = names;
                proxy.Names(ref names);
                Console.WriteLine($"Names={ReferenceEquals(original, names)}|{Join(names!.ToArray2D())}");
                names.Dispose();
                names = ComSafeArray<string?>.Create(["one"]);
                original = names;
                proxy.Names(ref names);
                Console.WriteLine($"Replaced={ReferenceEquals(original, names)}|{IsDisposed(original)}|{Join(names!.ToArray())}");
                names.Dispose();

                static bool IsDisposed<T>(ComSafeArray<T> array)
                {
                    try
                    {
                        _ = array.Pointer;
                        return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        return true;
                    }
                }
                """,
                """
                public static ComSafeArray<double>? View { get; private set; }

                // Returns a 1-based array, like Visual Basic and Excel.
                public ComSafeArray<double>? Matrix(ComSafeArray<double>? values)
                {
                    View = values;
                    int rows = values!.GetLength(0);
                    int columns = values.GetLength(1);
                    var result = ComSafeArray<double>.Create(VarEnum.VT_R8, [rows, columns], [1, 1]);
                    for (int i = 0; i < rows; i++) for (int j = 0; j < columns; j++) result[i + 1, j + 1] = values[i, j] * 2;
                    return result;
                }

                public void Names(ref ComSafeArray<string?>? names)
                {
                    if (names!.Rank == 2)
                    {
                        names[0, 1] = "changed";
                    }
                    else
                    {
                        names = ComSafeArray<string?>.Create(["new"]);
                    }
                }
                """,
                new[] { "Matrix=2|1|60|2,4,6,20,40,60", "InputAlive=6|ViewInvalid=True", "Names=True|a,changed,c,<null>", "Replaced=False|True|new" }),
            _ => (
                """
                using var input = ComSafeArray<double>.Create(new double[,] { { 1, 2, 3 }, { 10, 20, 30 } });
                using var result = ComSafeArray<double>.FromPointer(proxy.Matrix(input.Pointer), ownsArray: true)!;
                Console.WriteLine($"Matrix={result.Rank}|{Join(result.ToArray2D())}");
                nint names = ComSafeArray<string?>.Create(new string?[,] { { "a", "b" }, { "c", null } }).Detach();
                proxy.Names(ref names);
                using (var grid = ComSafeArray<string?>.FromPointer(names, ownsArray: true)!)
                {
                    Console.WriteLine($"Names={Join(grid.ToArray2D())}");
                }

                names = ComSafeArray<string?>.Create(["one"]).Detach();
                proxy.Names(ref names);
                using (var replaced = ComSafeArray<string?>.FromPointer(names, ownsArray: true)!)
                {
                    Console.WriteLine($"Replaced={Join(replaced.ToArray())}");
                }
                """,
                """
                public nint Matrix(nint values)
                {
                    double[,] matrix = ComSafeArray<double>.FromPointer(values, ownsArray: false)!.ToArray2D();
                    var result = new double[matrix.GetLength(0), matrix.GetLength(1)];
                    for (int i = 0; i < matrix.GetLength(0); i++) for (int j = 0; j < matrix.GetLength(1); j++) result[i, j] = matrix[i, j] * 2;
                    return ComSafeArray<double>.Create(result).Detach();
                }

                // The callee destroys the array it replaces.
                public void Names(ref nint names)
                {
                    var view = ComSafeArray<string?>.FromPointer(names, ownsArray: false)!;
                    if (view.Rank == 2)
                    {
                        view[0, 1] = "changed";
                    }
                    else
                    {
                        ComSafeArray<string?>.FromPointer(names, ownsArray: true)!.Dispose();
                        names = ComSafeArray<string?>.Create(["new"]).Detach();
                    }
                }
                """,
                new[] { "Matrix=2|2,4,6,20,40,60", "Names=a,changed,c,<null>", "Replaced=new" }),
        };

        program = program
            .Replace("__CALLS__", calls, StringComparison.Ordinal)
            .Replace("__IMPL__", implementation, StringComparison.Ordinal);
        var output = RunConsumer(root, interop, program, allowUnsafe: true);

        string[] lines = ["Values=2x2|1,a,2.5,<null>", "Cube=2x3x4|123|12", .. expected];
        CollectionAssert.IsSubsetOf(lines, output.ToArray(), string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    public void DefaultOverloadsCompile()
    {
        // With the wrappers, the BSTR and VARIANT_BOOL default values of IShape.Move have no C# constants; the overloads
        // generated with OverloadsForDefaultValues allow omitting them, and the full method stays available.
        string root = TestEnvironment.CreateDirectory("default-overloads");
        string interop = new ProjectGenerator(_pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = Path.Combine(root, "Interop"),
            Marshalling = AllWrappers with { Bstrs = BstrMarshalling.Wrapper },
            OverloadsForDefaultValues = true,
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        var output = RunConsumer(root, interop, """
            using CsCom.Runtime;
            using Interop.CsComTest;

            if (args.Length > 100)
            {
                Use(null!);
            }

            Console.WriteLine("Compiled");

            static void Use(IShape shape)
            {
                using var label = ComBstr.Create("x");
                shape.Move(1);
                shape.Move(1, 2);
                shape.Move(1, 2, 3.5);
                shape.Move(1, 2, null, label);
                shape.Move(1, 2, null, label, Color.Blue, 2.5);
                shape.Move(1, 2, null, label, Color.Blue, 2.5, ComBool.False);
            }
            """);

        CollectionAssert.Contains(output.ToArray(), "Compiled");
    }

    [TestMethod]
    [DataRow(NamedIndexers.ClassProperty, false)]
    [DataRow(NamedIndexers.StructMethod, false)]
    [DataRow(NamedIndexers.ClassProperty, true)]
    [DataRow(NamedIndexers.StructMethod, true)]
    public void NamedIndexersOfScriptingRuntimeRun(NamedIndexers mode, bool lateBound)
    {
        // Key is a named property with a setter only: it renames a key of the dictionary. With lateBound, the dual interfaces
        // are turned into dispinterfaces, whose wrappers have the proxies as instance members.
        var pipeline = new InteropPipeline();
        if (lateBound)
        {
            pipeline.ModelTransformers.Add(new LateBindingTransformer());
        }

        string root = TestEnvironment.CreateDirectory($"named-indexers-{mode}-{lateBound}");
        string interop = new ProjectGenerator(pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.ScriptingRuntime],
            OutputDirectory = Path.Combine(root, "Interop"),
            NamedIndexers = mode,
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        // The member that returns the proxy is a property of the extension block or of the wrapper, or a method.
        bool isClass = mode == NamedIndexers.ClassProperty;
        string container = lateBound ? "IDictionary" : "IDictionaryExtensions";
        string member = (isClass, lateBound) switch
        {
            (true, false) => """typeof(IDictionaryExtensions).GetMethod("get_Key", [typeof(IDictionary)])!""",
            (true, true) => """typeof(IDictionary).GetProperty("Key")!.GetMethod!""",
            (false, false) => """typeof(IDictionaryExtensions).GetMethod("Key", [typeof(IDictionary)])!""",
            (false, true) => """typeof(IDictionary).GetMethod("Key", Type.EmptyTypes)!""",
        };
        var output = RunConsumer(root, interop, $$"""
            using System.Reflection;
            using Interop.Scripting;

            IDictionary dictionary = Dictionary.Create();
            dictionary.Add("a", 1);
            dictionary.{{(isClass ? "Key" : "Key()")}}["a"] = "z";
            Console.WriteLine($"Exists={dictionary.Exists("z")}|{dictionary.Exists("a")}");

            // The members of the proxy class are inlined, so that the JIT can allocate it on the stack.
            var proxy = typeof({{container}}.KeyIndexer);
            MethodBase[] members = [{{member}}, proxy.GetProperty("Item")!.SetMethod!, proxy.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()];
            Console.WriteLine($"Proxy={(proxy.IsValueType ? "struct" : "class")}");
            Console.WriteLine($"Inlining={string.Join(",", members.Select(static m => m.MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining)))}");
            """);

        string inlining = isClass ? "True,True,True" : "False,False,False";
        CollectionAssert.IsSubsetOf(
            new[] { "Exists=True|False", $"Proxy={(isClass ? "class" : "struct")}", $"Inlining={inlining}" },
            output.ToArray(),
            string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    public void PropertiesOfDerivedInterfacesHaveTheInheritedAccessors()
    {
        // ICircle adds the setters of the read-only Name and Item (the extension indexer of C# 15) of IShape. Its extension block
        // wins for ICircle receivers, so it must have the getters of IShape too. The moved setters no longer match the vtable of
        // a real object, so the code is only compiled.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new AccessorsInDerivedInterfaceTransformer());
        string root = TestEnvironment.CreateDirectory("derived-accessors");
        string interop = new ProjectGenerator(pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = Path.Combine(root, "Interop"),
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        var output = RunConsumer(root, interop, """
            using Interop.CsComTest;

            if (args.Length > 100)
            {
                Use(null!);
            }

            Console.WriteLine("Compiled");

            static void Use(ICircle circle)
            {
                circle.Name = circle.Name + "!";
                IShape shape = circle;
                Console.WriteLine(shape.Name);
            #if NET11_0_OR_GREATER
                circle[0] = circle[1];
                Console.WriteLine(shape[0]);
            #endif
            }
            """);

        CollectionAssert.Contains(output.ToArray(), "Compiled");
    }

    [TestMethod]
    public void RankedSafeArraysRoundTripThroughComWrappers()
    {
        // The ranks of IArrays.Matrix and IArrays.Names are set by the configuration, so they are marshalled as T[,] by the
        // marshallers generated into the project, in both directions of the generated stubs.
        string root = TestEnvironment.CreateDirectory("safearrays-ranked");
        string configuration = Path.Combine(root, "cscom.xml");
        File.WriteAllText(configuration, """
            <CsCom>
              <ModelTransformers>
                <SetSafeArrayRank TypeName="IArrays" Member="^(Matrix|Names)$" Rank="2" />
              </ModelTransformers>
            </CsCom>
            """);
        string interop = new ProjectGenerator(_pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = Path.Combine(root, "Interop"),
            ConfigurationPath = configuration,
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        var output = RunConsumer(root, interop, """
            using System.Globalization;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using Interop.CsComTest;

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            var wrappers = new StrategyBasedComWrappers();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(new ArraysImpl(), CreateComInterfaceFlags.None);
            var proxy = (IArrays)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Marshal.Release(unknown);

            double[,] matrix = proxy.Matrix(new double[,] { { 1, 2, 3 }, { 10, 20, 30 } })!;
            Console.WriteLine($"Matrix={matrix.GetLength(0)}x{matrix.GetLength(1)}|{matrix.GetLowerBound(0)}|{Join(matrix)}");
            string?[,]? names = new string?[,] { { "a", "b" }, { "c", null } };
            proxy.Names(ref names);
            Console.WriteLine($"Names={names!.GetLength(0)}x{names.GetLength(1)}|{Join(names)}");

            static string Join(Array array) => string.Join(",", array.Cast<object?>().Select(static v => v?.ToString() ?? "<null>"));

            [GeneratedComClass]
            internal sealed partial class ArraysImpl : IArrays
            {
                public double[,]? Matrix(double[,]? values)
                {
                    var result = new double[values!.GetLength(0), values.GetLength(1)];
                    for (int i = 0; i < values.GetLength(0); i++) for (int j = 0; j < values.GetLength(1); j++) result[i, j] = values[i, j] * 2;
                    return result;
                }

                public void Names(ref string?[,]? names) => names![0, 1] = "changed";

                public object? Values(object? value) => value;
            }
            """,
            allowUnsafe: true);

        CollectionAssert.IsSubsetOf(new[] { "Matrix=2x3|0|2,4,6,20,40,60", "Names=2x2|a,changed,c,<null>" }, output.ToArray(), string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    [DataRow(SupportCodeMode.Package)]
    [DataRow(SupportCodeMode.Public)]
    public void CustomComWrappersCreateTheObjects(SupportCodeMode supportCode)
    {
        // The objects of an activation, of an interface pointer and of a VARIANT are created by the instance of the consumer:
        // it returns the same objects for their pointers.
        string root = TestEnvironment.CreateDirectory($"com-wrappers-{supportCode}");
        string interop = CreateProject(
            TestEnvironment.ScriptingRuntime,
            Path.Combine(root, "Interop"),
            supportCode,
            singleFile: false);

        var output = RunConsumer(root, interop, """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using Interop.Scripting;
            using __SUPPORT__;

            // Values that are no objects are converted without the instance, which can still be set afterwards.
            VariantMarshaller.Free(VariantMarshaller.ConvertToUnmanaged(42));
            VariantMarshaller.Free(VariantMarshaller.ConvertToUnmanaged("text"));

            // Neither are null pointers and null objects.
            unsafe
            {
                _ = ComObjectMarshaller<object>.ConvertToManaged(null);
                _ = UniqueComObjectMarshaller<IDispatch>.ConvertToManaged(null);
                _ = ComObjectMarshaller<object>.ConvertToUnmanaged(null);
                _ = VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_DISPATCH, (nint)0));
            }

            var wrappers = new CountingComWrappers();
            ComMarshalling.ComWrappers = wrappers;

            IDictionary dictionary = Dictionary.Create();
            Console.WriteLine($"Activation={IsOwned(dictionary)}");

            IDictionary child = Dictionary.Create();
            dictionary.Add("child", child);
            Console.WriteLine($"Variant={ReferenceEquals(dictionary.get_Item("child"), child)}");

            IFileSystem3 fileSystem = FileSystemObject.Create();
            IFolder folder = fileSystem.GetFolder(Path.GetTempPath())!;
            Console.WriteLine($"Interface={IsOwned(folder)}");
            Console.WriteLine($"Argument={fileSystem.GetFolder(folder.Path)!.Path == folder.Path}");
            Console.WriteLine($"Created={wrappers.Created >= 4}");

            // An object that is passed by reference in a VARIANT.
            ComWrappers.TryGetComInstance(child, out nint reference);
            unsafe
            {
                var byRef = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_UNKNOWN, (nint)(&reference));
                Console.WriteLine($"ByRef={ReferenceEquals(VariantMarshaller.ConvertToManaged(byRef), child)}");
            }

            Marshal.Release(reference);

            // A managed object is exposed by the instance too, which therefore unwraps its pointer, also from the wrapper
            // that ComVariantMarshaller converts with the instance of .NET; other objects are rejected as without it.
            var exposed = new Exposed();
            foreach (object value in new object[] { exposed, new UnknownWrapper(exposed) })
            {
                ComVariant variant = VariantMarshaller.ConvertToUnmanaged(value);
                Console.WriteLine($"{value.GetType().Name}={variant.VarType == VarEnum.VT_UNKNOWN && ReferenceEquals(VariantMarshaller.ConvertToManaged(variant), exposed)}");
                VariantMarshaller.Free(variant);
            }

            // A unique object of the pointer is the object too, as the shared one is: the flags of the instance cannot unwrap it.
            unsafe
            {
                void* exposedPointer = ComObjectMarshaller<IExposed>.ConvertToUnmanaged(exposed);
                Console.WriteLine($"UniqueExposed={ReferenceEquals(UniqueComObjectMarshaller<IExposed>.ConvertToManaged(exposedPointer), exposed) && ReferenceEquals(UniqueComObjectMarshaller<object>.ConvertToManaged(exposedPointer), exposed)}");
                ComObjectMarshaller<IExposed>.Free(exposedPointer);
            }

            try
            {
                VariantMarshaller.ConvertToUnmanaged(new System.Text.StringBuilder());
            }
            catch (ArgumentException)
            {
                Console.WriteLine("Rejected");
            }

            IDictionary unique = Dictionary.Create(unique: true);
            ComWrappers.TryGetComInstance(unique, out nint pointer);
            ((ComObject)(object)unique).FinalRelease();
            Console.WriteLine($"Released={Marshal.Release(pointer) == 0}");

            // default converts to the unique parameter too, but calls the overload whose parameters all have arguments: the
            // empty context, which activates no class.
            try
            {
                _ = Dictionary.Create(default);
                Console.WriteLine("DefaultContext=Created");
            }
            catch (COMException e)
            {
                Console.WriteLine($"DefaultContext=0x{e.HResult:X8}");
            }

            // The object that another ComWrappers has created for a COM object is marshalled as that COM object: it is not
            // exposed as a managed object by the instance.
            ComWrappers.TryGetComInstance(child, out nint identity);
            object foreign = new ForeignWrappers().GetOrCreateObjectForComInstance(identity, CreateObjectFlags.None);
            unsafe
            {
                nint marshalled = (nint)ComObjectMarshaller<object>.ConvertToUnmanaged(foreign);
                Console.WriteLine($"Foreign={marshalled == identity}");
                Marshal.Release(marshalled);
            }

            Marshal.Release(identity);
            GC.KeepAlive(foreign);

            try
            {
                ComMarshalling.ComWrappers = new StrategyBasedComWrappers();
            }
            catch (InvalidOperationException)
            {
                Console.WriteLine("Frozen");
            }

            // Whether the instance of the consumer has created the object: it returns it for the pointer of the COM object.
            bool IsOwned(object value)
            {
                ComWrappers.TryGetComInstance(value, out nint unknown);
                try
                {
                    return ReferenceEquals(wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None), value);
                }
                finally
                {
                    Marshal.Release(unknown);
                }
            }

            [GeneratedComInterface]
            [Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A10F1")]
            internal partial interface IExposed
            {
                void Run();
            }

            [GeneratedComClass]
            internal sealed partial class Exposed : IExposed
            {
                public void Run()
                {
                }
            }

            internal sealed class CountingComWrappers : StrategyBasedComWrappers
            {
                private int _created;

                public int Created => _created;

                protected override IIUnknownStrategy GetOrCreateIUnknownStrategy()
                {
                    Interlocked.Increment(ref _created);
                    return base.GetOrCreateIUnknownStrategy();
                }
            }

            // A ComWrappers whose objects are no ComObjects of .NET.
            internal sealed unsafe class ForeignWrappers : ComWrappers
            {
                protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
                {
                    count = 0;
                    return null;
                }

                protected override object? CreateObject(nint externalComObject, CreateObjectFlags flags) => new ForeignObject();

                protected override void ReleaseObjects(System.Collections.IEnumerable objects) => throw new NotSupportedException();
            }

            internal sealed class ForeignObject;
            """.Replace("__SUPPORT__", supportCode == SupportCodeMode.Package ? "CsCom.Runtime" : "Interop.Scripting.ComSupport", StringComparison.Ordinal),
            allowUnsafe: true);

        CollectionAssert.IsSubsetOf(
            new[] { "Activation=True", "Variant=True", "Interface=True", "Argument=True", "Created=True", "ByRef=True", "Exposed=True", "UnknownWrapper=True", "UniqueExposed=True", "Rejected", "Released=True", "DefaultContext=0x80040154", "Foreign=True", "Frozen" },
            output.ToArray(),
            string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    public void InterfacesAreMarshalledAsUniqueInstances()
    {
        // A return value that is marshalled as a unique instance is released at once, which the shared object of the same
        // COM object is not.
        string root = TestEnvironment.CreateDirectory("interfaces-unique");
        string configuration = Path.Combine(root, "cscom.xml");
        File.WriteAllText(configuration, """
            <CsCom>
              <ModelTransformers>
                <SetMarshalling TypeName="IFileSystem" MemberName="GetFolder" Interfaces="unique" />
                <SetMarshalling TypeName="IDictionary" MemberName="_NewEnum" Interfaces="unique" />
              </ModelTransformers>
            </CsCom>
            """);
        string interop = new ProjectGenerator(_pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.ScriptingRuntime],
            OutputDirectory = Path.Combine(root, "Interop"),
            ConfigurationPath = configuration,
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        var output = RunConsumer(root, interop, """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using Interop.Scripting;

            IFileSystem3 fileSystem = FileSystemObject.Create();
            IFolder first = fileSystem.GetFolder(Path.GetTempPath())!;
            IFolder second = first.ParentFolder!;
            Console.WriteLine($"Unique={Release(first)}");
            Console.WriteLine($"Shared={Release(second)}");
            GC.KeepAlive(second);

            // The enumerator of _NewEnum is a unique object, which foreach releases at the end: it holds a reference to the
            // dictionary until then. The first loop makes the dictionary cache its interface pointers.
            IDictionary dictionary = Dictionary.Create();
            dictionary.Add("a", 1);
            dictionary.Add("b", 2);
            ComWrappers.TryGetComInstance(dictionary, out nint pointer);
            var keys = new List<object?>();
            foreach (object? key in dictionary)
            {
                keys.Add(key);
            }

            int references = Count(pointer);
            foreach (object? key in dictionary)
            {
                keys.Add(key);
            }

            Console.WriteLine($"Keys={string.Join(",", keys)}");
            Console.WriteLine($"Enumerator={Count(pointer) == references}");
            Marshal.Release(pointer);
            GC.KeepAlive(dictionary);

            static int Count(nint pointer)
            {
                Marshal.AddRef(pointer);
                return Marshal.Release(pointer);
            }

            // Whether FinalRelease releases the references of the object, so that the reference of the test is the last one.
            static bool Release(object value)
            {
                ComWrappers.TryGetComInstance(value, out nint unknown);
                ((ComObject)value).FinalRelease();
                return Marshal.Release(unknown) == 0;
            }
            """);

        CollectionAssert.IsSubsetOf(
            new[] { "Unique=True", "Shared=False", "Keys=a,b,a,b", "Enumerator=True" },
            output.ToArray(),
            string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ForeachEnumeratesTheObjectOfADispinterfaceWrapper(bool unique)
    {
        // ICalculator.Enumerator becomes a _NewEnum that returns the dispinterface DShapeEvents, whose late-bound wrapper is
        // no enumerator: foreach enumerates its object, through ComEnumerator.FromObject. No COM object of Windows returns a
        // dispinterface from _NewEnum, so the consumer implements the collection and the enumerator, and calls the
        // collection through a runtime callable wrapper of its own COM callable wrapper. The enumerator is a managed object,
        // which the marshallers unwrap: the release of a unique COM enumerator is not observable here, and is tested by
        // UniqueInstanceTests.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new DispatchEnumeratorTransformer());
        string root = TestEnvironment.CreateDirectory(unique ? "dispatch-enumerator-unique" : "dispatch-enumerator");
        string? configuration = null;
        if (unique)
        {
            configuration = Path.Combine(root, "cscom.xml");
            File.WriteAllText(configuration, """
                <CsCom>
                  <ModelTransformers>
                    <SetMarshalling TypeName="ICalculator" MemberName="enumerator" Interfaces="unique" />
                  </ModelTransformers>
                </CsCom>
                """);
        }

        string interop = new ProjectGenerator(pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = Path.Combine(root, "Interop"),
            ConfigurationPath = configuration,
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        // The transformer applies: GetEnumerator enumerates the object of the wrapper, which it owns and releases when it is
        // unique and the call went through COM.
        string generated = string.Concat(Directory.GetFiles(Path.GetDirectoryName(interop)!, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        const string Call = "global::CsCom.Runtime.ComEnumerator.FromObject(@this.enumerator()";
        StringAssert.Contains(generated, unique ? $"{Call}, ownsEnumerator: " : $"{Call});");

        var output = RunConsumer(root, interop, """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using CsCom.Runtime;
            using Interop.CsComTest;

            var wrappers = new StrategyBasedComWrappers();
            var implementation = new CalculatorImpl();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(implementation, CreateComInterfaceFlags.None);
            var proxy = (ICalculator)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Marshal.Release(unknown);

            // Through COM, where the marshaller creates the wrapper, and on the implementation itself.
            Console.WriteLine($"Proxy={string.Join(",", Enumerate(proxy))}");
            var owned = implementation.Last!;
            Console.WriteLine($"Direct={string.Join(",", Enumerate(implementation))}");

            // Disposing the enumerator at the end of foreach through COM, which owns it when it is unique, leaves the object
            // of the application alone.
            Console.WriteLine($"Usable={owned.Reset() == 0}");

            static List<object?> Enumerate(ICalculator calculator)
            {
                var items = new List<object?>();
                foreach (object? item in calculator)
                {
                    items.Add(item);
                }

                return items;
            }

            [GeneratedComClass]
            internal sealed unsafe partial class ItemsEnumerator(int[] items) : IDispatch, IEnumVARIANT
            {
                private const int E_NOTIMPL = unchecked((int)0x80004001);
                private int _index;

                public int GetTypeInfoCount(out uint pctinfo)
                {
                    pctinfo = 0;
                    return 0;
                }

                public int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
                {
                    ppTInfo = 0;
                    return E_NOTIMPL;
                }

                public int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId) => E_NOTIMPL;

                public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) => E_NOTIMPL;

                public int Next(uint celt, ComVariant* rgVar, uint* pCeltFetched)
                {
                    uint fetched = 0;
                    while (fetched < celt && _index < items.Length)
                    {
                        rgVar[fetched++] = ComVariant.Create(items[_index++]);
                    }

                    if (pCeltFetched is not null)
                    {
                        *pCeltFetched = fetched;
                    }

                    return fetched == celt ? 0 : 1;
                }

                public int Skip(uint celt)
                {
                    _index = (int)Math.Min(items.Length, _index + celt);
                    return _index == items.Length ? 1 : 0;
                }

                public int Reset()
                {
                    _index = 0;
                    return 0;
                }

                public int Clone(out IEnumVARIANT? ppEnum)
                {
                    ppEnum = null;
                    return E_NOTIMPL;
                }
            }

            [GeneratedComClass]
            internal sealed unsafe partial class CalculatorImpl : ICalculator
            {
                public ItemsEnumerator? Last { get; private set; }

                public DShapeEvents? enumerator() => DShapeEvents.Create(Last = new ItemsEnumerator([1, 2, 3]));

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
            }
            """, allowUnsafe: true);

        CollectionAssert.IsSubsetOf(
            new[] { "Proxy=1,2,3", "Direct=1,2,3", "Usable=True" },
            output.ToArray(),
            string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    [DataRow(BstrMarshalling.String)]
    [DataRow(BstrMarshalling.Wrapper)]
    [DataRow(BstrMarshalling.None)]
    public void BstrsRoundTripThroughComWrappers(BstrMarshalling mode)
    {
        // Like SafeArraysRoundTripThroughComWrappers, the consumer implements IStrings and calls it through a runtime callable
        // wrapper of its own COM callable wrapper, so that the strings and their ownership go through both directions of the
        // generated stubs.
        string root = TestEnvironment.CreateDirectory($"bstrs-{mode}");
        string interop = CreateProject(
            TestEnvironment.TestTypeLibrary,
            Path.Combine(root, "Interop"),
            SupportCodeMode.Package,
            singleFile: false,
            new MarshallingOptions { Bstrs = mode });

        string program = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using CsCom.Runtime;
            using Interop.CsComTest;

            var wrappers = new StrategyBasedComWrappers();
            var implementation = new StringsImpl();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(implementation, CreateComInterfaceFlags.None);
            var proxy = (IStrings)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Marshal.Release(unknown);

            __CALLS__

            [GeneratedComClass]
            internal sealed partial class StringsImpl : IStrings
            {
                __IMPL__
            }
            """;

        (string calls, string implementation, string[] expected) = mode switch
        {
            BstrMarshalling.String => (
                """
                Console.WriteLine($"Echo={proxy.Echo("hello")}");
                string? value = "a";
                proxy.Append(ref value);
                proxy.Append(ref value, "?");
                Console.WriteLine($"Append={value}");
                proxy.Split("a b c", out var first, out var rest);
                Console.WriteLine($"Split={first}|{rest}");
                """,
                """
                public string? Echo(string? value) => value;

                public void Append(ref string? value, string? suffix) => value += suffix;

                public void Split(string? value, out string? first, out string? rest)
                {
                    int i = value!.IndexOf(' ');
                    first = value[..i];
                    rest = value[(i + 1)..];
                }
                """,
                new[] { "Echo=hello", "Append=a!?", "Split=a|b c" }),
            BstrMarshalling.Wrapper => (
                """
                using var input = ComBstr.Create("hello");
                using (var echoed = proxy.Echo(input)!)
                {
                    Console.WriteLine($"Echo={echoed}|{echoed.OwnsString}|{!ReferenceEquals(echoed, input)}|{input}|ViewInvalid={IsDisposed(StringsImpl.View!)}");
                }

                using (var binary = ComBstr.CreateFromBytes([1, 2, 3]))
                using (var binaryEcho = proxy.Echo(binary)!)
                {
                    Console.WriteLine($"Binary={binaryEcho.ByteLength}|{string.Join(",", binaryEcho.AsBytes().ToArray())}");
                }

                using var empty = ComBstr.Create("");
                ComBstr? value = ComBstr.Create("a");
                var original = value;
                proxy.Append(ref value, empty);
                Console.WriteLine($"Unchanged={ReferenceEquals(original, value)}|{value}");
                using var suffix = ComBstr.Create("!");
                proxy.Append(ref value, suffix);
                Console.WriteLine($"Replaced={ReferenceEquals(original, value)}|{IsDisposed(original)}|{value}|{value!.OwnsString}");
                value.Dispose();

                using var text = ComBstr.Create("a b c");
                proxy.Split(text, out var first, out var rest);
                Console.WriteLine($"Split={first}|{rest}|{first!.OwnsString}");
                first.Dispose();
                rest!.Dispose();

                static bool IsDisposed(ComBstr bstr)
                {
                    try
                    {
                        _ = bstr.Pointer;
                        return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        return true;
                    }
                }
                """,
                """
                public static ComBstr? View { get; private set; }

                // Returns the view of the argument, which is copied for the caller.
                public ComBstr? Echo(ComBstr? value)
                {
                    View = value;
                    return value;
                }

                public void Append(ref ComBstr? value, ComBstr? suffix)
                {
                    if (suffix!.Length > 0)
                    {
                        value = ComBstr.Create(value!.ToString() + suffix.ToString());
                    }
                }

                public void Split(ComBstr? value, out ComBstr? first, out ComBstr? rest)
                {
                    string text = value!.ToString();
                    int i = text.IndexOf(' ');
                    first = ComBstr.Create(text.AsSpan(0, i));
                    rest = ComBstr.Create(text.AsSpan(i + 1));
                }
                """,
                new[] { "Echo=hello|True|True|hello|ViewInvalid=True", "Binary=3|1,2,3", "Unchanged=True|a", "Replaced=False|True|a!|True", "Split=a|b c|True" }),
            _ => (
                """
                nint input = Marshal.StringToBSTR("hello");
                nint echoed = proxy.Echo(input);
                Console.WriteLine($"Echo={Marshal.PtrToStringBSTR(echoed)}|{echoed != input}");
                Marshal.FreeBSTR(echoed);
                Marshal.FreeBSTR(input);

                nint value = Marshal.StringToBSTR("a");
                nint suffix = Marshal.StringToBSTR("!");
                proxy.Append(ref value, suffix);
                Console.WriteLine($"Append={Marshal.PtrToStringBSTR(value)}");
                Marshal.FreeBSTR(value);
                Marshal.FreeBSTR(suffix);

                nint text = Marshal.StringToBSTR("a b c");
                proxy.Split(text, out nint first, out nint rest);
                Console.WriteLine($"Split={Marshal.PtrToStringBSTR(first)}|{Marshal.PtrToStringBSTR(rest)}");
                Marshal.FreeBSTR(first);
                Marshal.FreeBSTR(rest);
                Marshal.FreeBSTR(text);
                """,
                """
                public nint Echo(nint value) => Marshal.StringToBSTR(Marshal.PtrToStringBSTR(value));

                // The callee frees the string it replaces.
                public void Append(ref nint value, nint suffix)
                {
                    string result = Marshal.PtrToStringBSTR(value) + Marshal.PtrToStringBSTR(suffix);
                    Marshal.FreeBSTR(value);
                    value = Marshal.StringToBSTR(result);
                }

                public void Split(nint value, out nint first, out nint rest)
                {
                    string text = Marshal.PtrToStringBSTR(value);
                    int i = text.IndexOf(' ');
                    first = Marshal.StringToBSTR(text[..i]);
                    rest = Marshal.StringToBSTR(text[(i + 1)..]);
                }
                """,
                new[] { "Echo=hello|True", "Append=a!", "Split=a|b c" }),
        };

        program = program
            .Replace("__CALLS__", calls, StringComparison.Ordinal)
            .Replace("__IMPL__", implementation, StringComparison.Ordinal);
        var output = RunConsumer(root, interop, program, allowUnsafe: true);

        CollectionAssert.IsSubsetOf(expected, output.ToArray(), string.Join(Environment.NewLine, output));
    }

    [TestMethod]
    [DataRow(EventStyle.Events)]
    [DataRow(EventStyle.Observable)]
    [DataRow(EventStyle.Interface)]
    public void EventsAreReceivedThroughEachKindOfEventInterface(EventStyle style)
    {
        ReceiveEvents(style, customComWrappers: false);
    }

    [TestMethod]
    public void EventsAreReceivedWithCustomComWrappers()
    {
        // The sinks are exposed to COM, and the connection points are wrapped, by the instance of the consumer.
        ReceiveEvents(EventStyle.Events, customComWrappers: true);
    }

    private void ReceiveEvents(EventStyle style, bool customComWrappers)
    {
        // The consumer implements a COM object that raises events through connection points and connects the generated sinks
        // to a runtime callable wrapper of it. The object calls the sinks through runtime callable wrappers as well, so that
        // the events are marshalled to native code and back: dispinterface and dual events through IDispatch::Invoke,
        // vtable and dual events through the vtable.
        string root = TestEnvironment.CreateDirectory(customComWrappers ? "events-com-wrappers" : $"events-{style}");
        string interop = CreateProject(
            TestEnvironment.TestTypeLibrary,
            Path.Combine(root, "Interop"),
            SupportCodeMode.Package,
            singleFile: false,
            eventStyle: style);

        (string Source, string Label)[] sources = [("_ShapeEvents", "Dispatch"), ("IShapeEventsVt", "Vt"), ("IShapeEventsDual", "Dual")];
        string program = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using CsCom.Runtime;
            using Interop.CsComTest;

            var wrappers = new StrategyBasedComWrappers();
            __COM_WRAPPERS__
            var source = new EventSource();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(source, CreateComInterfaceFlags.None);
            object proxy = wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Marshal.Release(unknown);
            var disposables = new List<IDisposable>();

            // The vtable source raises an event while the sink connects: only a handler passed to Connect can get it.
            source.Points[IShapeEventsVtSink.Iid].Advising = sink =>
                ((IShapeEventsVt)wrappers.GetOrCreateObjectForComInstance(sink, CreateObjectFlags.UniqueInstance)).Moved(7, 8);

            __CONNECT__

            Console.WriteLine($"Connections={source.Connections}");

            // The sinks are kept, so that they can be called after they are disconnected, as by a source that raises an
            // event at the same time: their handlers get no more events.
            var sinks = source.Points.ToDictionary(static p => p.Key, static p => p.Value.Sinks.Values.ToArray());
            foreach (nint sink in sinks.Values.SelectMany(static s => s))
            {
                Marshal.AddRef(sink);
            }

            Fire(wrappers, sinks);
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }

            Console.WriteLine("Late");
            Fire(wrappers, sinks);
            foreach (nint sink in sinks.Values.SelectMany(static s => s))
            {
                Marshal.Release(sink);
            }

            __ASYNC__
            Console.WriteLine($"Connections={source.Connections}");

            static void Fire(StrategyBasedComWrappers wrappers, Dictionary<Guid, nint[]> sinks)
            {
                foreach (nint sink in sinks[_ShapeEventsSink.Iid])
                {
                    var dispatch = (IDispatch)wrappers.GetOrCreateObjectForComInstance(sink, CreateObjectFlags.UniqueInstance);
                    Invoke(dispatch, "DispatchInvoke");
                    try
                    {
                        DispatchObject.Invoke(dispatch, 3, DispatchFlags.Method, "fail");
                    }
                    catch (COMException e)
                    {
                        Console.WriteLine($"Error={e.Message}");
                    }
                }

                foreach (nint sink in sinks[IShapeEventsVtSink.Iid])
                {
                    var events = (IShapeEventsVt)wrappers.GetOrCreateObjectForComInstance(sink, CreateObjectFlags.UniqueInstance);
                    events.Moved(1, 2);
                    bool cancel = false;
                    events.Closing(ref cancel);
                    Console.WriteLine($"VtFire.Cancel={cancel}");
                    events.Renamed("renamed");
                    string? description = events.Describe(42, out int length);
                    Console.WriteLine($"VtFire.Describe={description}|{length}");
                }

                foreach (nint sink in sinks[IShapeEventsDualSink.Iid])
                {
                    var events = (IShapeEventsDual)wrappers.GetOrCreateObjectForComInstance(sink, CreateObjectFlags.UniqueInstance);
                    events.Moved(1, 2);
                    bool cancel = false;
                    events.Closing(ref cancel);
                    Console.WriteLine($"DualFire.Cancel={cancel}");
                    events.Renamed("renamed");
                    string? description = events.Describe(42, out int length);
                    Console.WriteLine($"DualFire.Describe={description}|{length}");
                    Invoke(events, "DualInvoke");
                }
            }

            static void Invoke(IDispatch dispatch, string label)
            {
                DispatchObject.Invoke(dispatch, 1, DispatchFlags.Method, 1, 2);
                object?[] closing = [false];
                DispatchObject.InvokeByRef(dispatch, 2, DispatchFlags.Method, closing, [true]);
                Console.WriteLine($"{label}.Cancel={closing[0]}");
                DispatchObject.Invoke(dispatch, 3, DispatchFlags.Method, "renamed");
                object?[] describe = [42, null];
                object? description = DispatchObject.InvokeByRef(dispatch, 4, DispatchFlags.Method, describe, [false, true]);
                Console.WriteLine($"{label}.Describe={description}|{describe[1]}");
            }

            __EVENT_SOURCE__

            __HANDLERS__
            """;

        program = program
            .Replace("__EVENT_SOURCE__", EventSourceFixture, StringComparison.Ordinal)
            .Replace("__COM_WRAPPERS__", customComWrappers ? "ComMarshalling.ComWrappers = wrappers;" : string.Empty, StringComparison.Ordinal)
            .Replace("__CONNECT__", string.Join(Environment.NewLine, sources.Select(s => ConnectEvents(style, s.Source, s.Label))), StringComparison.Ordinal)
            .Replace("__HANDLERS__", style == EventStyle.Interface ? string.Join(Environment.NewLine, sources.Select(static s => HandlerClass(s.Source, s.Label))) : string.Empty, StringComparison.Ordinal)
            .Replace("__ASYNC__", DisposeAsyncSteps(style), StringComparison.Ordinal);
        var output = RunConsumer(root, interop, program, allowUnsafe: true);

        string[] expected =
        [
            "Connections=3",
            "Dispatch.Moved=1,2", "Dispatch.Closing=True", "DispatchInvoke.Cancel=True", "Dispatch.Renamed=renamed", "DispatchInvoke.Describe=value 42|7", "Error=boom",
            "Vt.Moved=1,2", "Vt.Closing=True", "VtFire.Cancel=True", "Vt.Renamed=renamed", "VtFire.Describe=value 42|7",
            "Dual.Moved=1,2", "Dual.Closing=True", "DualFire.Cancel=True", "Dual.Renamed=renamed", "DualFire.Describe=value 42|7",
            "DualInvoke.Cancel=True", "DualInvoke.Describe=value 42|7",
            .. style == EventStyle.Observable ? ["Dispatch.Completed", "Vt.Completed", "Dual.Completed"] : Array.Empty<string>(),
            .. style == EventStyle.Interface ? Array.Empty<string>() : ["AsyncConnections=1", "AsyncDisposed=0", "Waiting=True", "WaitHandlerDone", "Waited"],
            .. style == EventStyle.Observable
                ? ["AsyncCompleted", "WaitCompleted", "CompletedEarly=False", "SyncHandlerDone", "SyncCompleted", "SyncCompletedOnHandlerThread=True", "SyncOtherCompleted",
                    "SyncRaiseError=80131509", "FailedCompleted", "FailedDisposeError=8001010E", "FailedConnections=1", "FailedRetried=0",
                    "BothDisposeErrors=COMException:8001010E,InvalidOperationException:80131509", "BothConnections=1", "BothRetried=0"]
                : Array.Empty<string>(),
        ];
        string text = string.Join(Environment.NewLine, output);
        CollectionAssert.IsSubsetOf(expected, output.ToArray(), text);
        Assert.AreEqual("Connections=0", output.Last(), text);
        if (style != EventStyle.Interface)
        {
            // DisposeAsync completes after the running handler has returned.
            Assert.IsLessThan(output.IndexOf("Waited"), output.IndexOf("WaitHandlerDone"), text);
        }

        if (style == EventStyle.Observable)
        {
            Assert.IsLessThan(output.IndexOf("AsyncDisposed=0"), output.IndexOf("AsyncCompleted"), text);
            Assert.IsGreaterThan(output.IndexOf("AsyncConnections=1"), output.IndexOf("AsyncCompleted"), text);

            // The observables are completed after the running handler has returned, by DisposeAsync and by Dispose.
            Assert.IsGreaterThan(output.IndexOf("WaitHandlerDone"), output.IndexOf("WaitCompleted"), text);
            Assert.IsLessThan(output.IndexOf("Waited"), output.IndexOf("WaitCompleted"), text);
            Assert.IsGreaterThan(output.IndexOf("SyncHandlerDone"), output.IndexOf("SyncCompleted"), text);

            // The exception of the observer fails the event call of the source, which the handler completed in, once the other
            // observable is completed too.
            Assert.IsGreaterThan(output.IndexOf("SyncCompleted"), output.IndexOf("SyncOtherCompleted"), text);
            Assert.IsGreaterThan(output.IndexOf("SyncOtherCompleted"), output.IndexOf("SyncRaiseError=80131509"), text);

            // DisposeAsync completes the observables before it throws the error that left the sink connected.
            Assert.IsGreaterThan(output.IndexOf("FailedCompleted"), output.IndexOf("FailedDisposeError=8001010E"), text);
        }
        Assert.HasCount(2, output.Where(static line => line == "Dual.Moved=1,2"), text);

        // The event that the source raises during Advise reaches the handler of Connect, but no handler or observer of a sink,
        // which can only be subscribed once the constructor has returned; the constructor succeeds all the same.
        Assert.AreEqual(style == EventStyle.Interface, output.Contains("Vt.Moved=7,8"), text);

        // After the sinks are disposed, the events that are raised through them reach no handler, through the vtable as
        // through IDispatch::Invoke, and the results and out arguments are the defaults.
        var late = output.SkipWhile(static line => line != "Late").ToList();
        Assert.IsFalse(late.Any(static line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^(Dispatch|Vt|Dual)\.|^Error=")), text);
        CollectionAssert.IsSubsetOf(
            new[] { "DispatchInvoke.Cancel=False", "VtFire.Cancel=False", "VtFire.Describe=|0", "DualFire.Cancel=False", "DualInvoke.Cancel=False" },
            late.ToArray(),
            text);
    }

    // The steps of the consumer of ReceiveEvents that dispose generated sinks: DisposeAsync disconnects, and completes when
    // no handler runs any more, also when one is running on another thread; with the observable style it completes the
    // observables afterwards, and so does Dispose, which does not wait for the handler.
    private static string DisposeAsyncSteps(EventStyle style)
    {
        if (style == EventStyle.Interface)
        {
            return string.Empty;
        }

        bool observable = style == EventStyle.Observable;
        string asyncSubscription = observable
            ? """_ = asyncSink.Moved.Subscribe(new Observer<IShapeEventsDual_MovedEventArgs>(_ => { }, () => Console.WriteLine("AsyncCompleted")));"""
            : string.Empty;
        string waitSubscription = observable
            ? """_ = waitSink.Moved.Subscribe(new Observer<IShapeEventsDual_MovedEventArgs>(_ => { waitEntered.Set(); WaitFor(waitResume); Console.WriteLine("WaitHandlerDone"); }, () => Console.WriteLine("WaitCompleted")));"""
            : """waitSink.Moved += (x, y) => { waitEntered.Set(); WaitFor(waitResume); Console.WriteLine("WaitHandlerDone"); };""";
        string steps = $$"""
            // A signal that does not come fails the program, rather than letting it go on as if it had come.
            static void WaitFor(ManualResetEventSlim signal, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(signal))] string name = "")
            {
                if (!signal.Wait(TimeSpan.FromSeconds(30)))
                {
                    throw new TimeoutException($"{name} was not set.");
                }
            }

            await using (var asyncSink = new IShapeEventsDualSink(proxy))
            {
                {{asyncSubscription}}
                Console.WriteLine($"AsyncConnections={source.Connections}");
            }

            Console.WriteLine($"AsyncDisposed={source.Connections}");

            // A handler is running on another thread when the sink is disposed.
            using var waitEntered = new ManualResetEventSlim();
            using var waitResume = new ManualResetEventSlim();
            var waitSink = new IShapeEventsDualSink(proxy);
            {{waitSubscription}}
            var waitEvents = (IShapeEventsDual)wrappers.GetOrCreateObjectForComInstance(source.Points[IShapeEventsDualSink.Iid].Sinks.Values.Single(), CreateObjectFlags.UniqueInstance);
            var waitRaising = Task.Run(() => waitEvents.Moved(1, 2));
            WaitFor(waitEntered);
            var waitDisposing = waitSink.DisposeAsync().AsTask();
            Console.WriteLine($"Waiting={!waitDisposing.IsCompleted}");
            waitResume.Set();
            await waitDisposing;
            await waitRaising;
            Console.WriteLine("Waited");
            """;

        if (observable)
        {
            steps += """

                // Dispose does not wait for the running handler, which completes the observables on its thread when it has
                // returned; the exception of an observer then fails its event call, after the other observables are
                // completed too.
                using var syncEntered = new ManualResetEventSlim();
                using var syncResume = new ManualResetEventSlim();
                using var syncCompleted = new ManualResetEventSlim();
                int syncHandlerThread = 0;
                var syncSink = new IShapeEventsDualSink(proxy);
                _ = syncSink.Moved.Subscribe(new Observer<IShapeEventsDual_MovedEventArgs>(
                    _ => { syncHandlerThread = Environment.CurrentManagedThreadId; syncEntered.Set(); WaitFor(syncResume); Console.WriteLine("SyncHandlerDone"); },
                    () =>
                    {
                        Console.WriteLine("SyncCompleted");
                        Console.WriteLine($"SyncCompletedOnHandlerThread={Environment.CurrentManagedThreadId == syncHandlerThread}");
                        syncCompleted.Set();
                        throw new InvalidOperationException("SyncCompletionFailed");
                    }));
                _ = syncSink.Closing.Subscribe(new Observer<IShapeEventsDual_ClosingEventArgs>(_ => { }, () => Console.WriteLine("SyncOtherCompleted")));
                var syncEvents = (IShapeEventsDual)wrappers.GetOrCreateObjectForComInstance(source.Points[IShapeEventsDualSink.Iid].Sinks.Values.Single(), CreateObjectFlags.UniqueInstance);
                var syncRaising = Task.Run(() => syncEvents.Moved(1, 2));
                WaitFor(syncEntered);
                syncSink.Dispose();
                Console.WriteLine($"CompletedEarly={syncCompleted.IsSet}");
                syncResume.Set();
                WaitFor(syncCompleted);
                try
                {
                    await syncRaising;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"SyncRaiseError={e.HResult:X8}");
                }

                // A disconnection from the wrong thread leaves the sink connected: DisposeAsync throws, after completing the
                // observables, and Dispose disconnects the sink.
                var failSink = new IShapeEventsDualSink(proxy);
                _ = failSink.Moved.Subscribe(new Observer<IShapeEventsDual_MovedEventArgs>(_ => { }, () => Console.WriteLine("FailedCompleted")));
                source.Points[IShapeEventsDualSink.Iid].UnadviseError = unchecked((int)0x8001010E);
                try
                {
                    await failSink.DisposeAsync();
                }
                catch (COMException e)
                {
                    Console.WriteLine($"FailedDisposeError={e.HResult:X8}");
                }

                Console.WriteLine($"FailedConnections={source.Connections}");
                failSink.Dispose();
                Console.WriteLine($"FailedRetried={source.Connections}");

                // When an observer throws too, both exceptions are thrown, the one that left the sink connected first.
                var bothSink = new IShapeEventsDualSink(proxy);
                _ = bothSink.Moved.Subscribe(new Observer<IShapeEventsDual_MovedEventArgs>(_ => { }, () => throw new InvalidOperationException("BothCompletionFailed")));
                source.Points[IShapeEventsDualSink.Iid].UnadviseError = unchecked((int)0x8001010E);
                try
                {
                    await bothSink.DisposeAsync();
                }
                catch (AggregateException e)
                {
                    Console.WriteLine($"BothDisposeErrors={string.Join(",", e.InnerExceptions.Select(static x => $"{x.GetType().Name}:{x.HResult:X8}"))}");
                }

                Console.WriteLine($"BothConnections={source.Connections}");
                bothSink.Dispose();
                Console.WriteLine($"BothRetried={source.Connections}");
                """;
        }

        return steps;
    }

    // A COM object of the consumer that raises the events of the event interfaces of the test library through connection
    // points, and an observer of the observable event style.
    private const string EventSourceFixture = """
        [GeneratedComClass]
        internal sealed partial class EventSource : IConnectionPointContainer
        {
            public Dictionary<Guid, ConnectionPoint> Points { get; } = new()
            {
                [_ShapeEventsSink.Iid] = new(_ShapeEventsSink.Iid),
                [IShapeEventsVtSink.Iid] = new(IShapeEventsVtSink.Iid),
                [IShapeEventsDualSink.Iid] = new(IShapeEventsDualSink.Iid),
            };

            public int Connections => Points.Values.Sum(static p => p.Sinks.Count);

            public int EnumConnectionPoints(out nint ppEnum)
            {
                ppEnum = 0;
                return unchecked((int)0x80004001);
            }

            public int FindConnectionPoint(in Guid riid, out IConnectionPoint? ppCP)
            {
                ppCP = Points.GetValueOrDefault(riid);
                return ppCP is null ? unchecked((int)0x80040200) : 0;
            }
        }

        [GeneratedComClass]
        internal sealed partial class ConnectionPoint(Guid iid) : IConnectionPoint
        {
            private uint _cookie;

            public Dictionary<uint, nint> Sinks { get; } = [];

            // The HRESULT with which the next call of Unadvise fails, leaving the sink connected.
            public int UnadviseError { get; set; }

            // Called once with the next sink that connects, before Advise returns, like a source that raises its state then.
            public Action<nint>? Advising { get; set; }

            public int GetConnectionInterface(out Guid pIID)
            {
                pIID = iid;
                return 0;
            }

            public int GetConnectionPointContainer(out nint ppCPC)
            {
                ppCPC = 0;
                return unchecked((int)0x80004001);
            }

            // Like real event sources, asks the sink for the event interface.
            public int Advise(nint pUnkSink, out uint pdwCookie)
            {
                pdwCookie = 0;
                int hr = Marshal.QueryInterface(pUnkSink, iid, out nint sink);
                if (hr < 0)
                {
                    return hr;
                }

                pdwCookie = ++_cookie;
                Sinks.Add(pdwCookie, sink);
                if (Advising is { } advising)
                {
                    Advising = null;
                    advising(sink);
                }

                return 0;
            }

            public int Unadvise(uint dwCookie)
            {
                if (UnadviseError != 0)
                {
                    int error = UnadviseError;
                    UnadviseError = 0;
                    return error;
                }

                if (!Sinks.Remove(dwCookie, out nint sink))
                {
                    return unchecked((int)0x80040200);
                }

                Marshal.Release(sink);
                return 0;
            }

            public int EnumConnections(out nint ppEnum)
            {
                ppEnum = 0;
                return unchecked((int)0x80004001);
            }
        }

        internal sealed class Observer<T>(Action<T> next, Action? completed = null) : IObserver<T>
        {
            public void OnNext(T value) => next(value);

            public void OnError(Exception error) => throw error;

            public void OnCompleted() => completed?.Invoke();
        }
        """;

    private static string ConnectEvents(EventStyle style, string source, string label) => style switch
    {
        EventStyle.Events => $$"""
            var {{label}}Sink = new {{source}}Sink(proxy);
            {{label}}Sink.Moved += (x, y) => Console.WriteLine($"{{label}}.Moved={x},{y}");
            {{label}}Sink.Closing += (ref bool cancel) => cancel = true;
            {{label}}Sink.Closing += (ref bool cancel) => Console.WriteLine($"{{label}}.Closing={cancel}");
            {{label}}Sink.Renamed += name =>
            {
                if (name == "fail") throw new InvalidOperationException("boom");
                Console.WriteLine($"{{label}}.Renamed={name}");
            };
            {{label}}Sink.Describe += (object? value, out int length) =>
            {
                length = 7;
                return $"value {value}";
            };
            disposables.Add({{label}}Sink);
            """,
        EventStyle.Observable => $$"""
            var {{label}}Sink = new {{source}}Sink(proxy);
            disposables.Add({{label}}Sink);
            disposables.Add({{label}}Sink.Moved.Subscribe(new Observer<{{source}}_MovedEventArgs>(e => Console.WriteLine($"{{label}}.Moved={e.X},{e.Y}"), () => Console.WriteLine("{{label}}.Completed"))));
            disposables.Add({{label}}Sink.Closing.Subscribe(new Observer<{{source}}_ClosingEventArgs>(e => e.Cancel = true)));
            disposables.Add({{label}}Sink.Closing.Subscribe(new Observer<{{source}}_ClosingEventArgs>(e => Console.WriteLine($"{{label}}.Closing={e.Cancel}"))));
            disposables.Add({{label}}Sink.Renamed.Subscribe(new Observer<{{source}}_RenamedEventArgs>(e =>
            {
                if (e.Name == "fail") throw new InvalidOperationException("boom");
                Console.WriteLine($"{{label}}.Renamed={e.Name}");
            })));
            disposables.Add({{label}}Sink.Describe.Subscribe(new Observer<{{source}}_DescribeEventArgs>(e =>
            {
                e.length = 7;
                e.Result = $"value {e.Value}";
            })));
            """,
        _ => $"disposables.Add({source}Sink.Connect(proxy, new {label}Handler()));",
    };

    private static string HandlerClass(string source, string label) => $$"""
        internal sealed class {{label}}Handler : {{(source.StartsWith('_') || source.StartsWith('I') ? source : "I" + source)}}Handler
        {
            public void Moved(int X, int Y) => Console.WriteLine($"{{label}}.Moved={X},{Y}");

            public void Closing(ref bool Cancel)
            {
                Cancel = true;
                Console.WriteLine($"{{label}}.Closing={Cancel}");
            }

            public void Renamed(string? Name)
            {
                if (Name == "fail") throw new InvalidOperationException("boom");
                Console.WriteLine($"{{label}}.Renamed={Name}");
            }

            public string? Describe(object? Value, out int length)
            {
                length = 7;
                return $"value {Value}";
            }
        }
        """;

    [TestMethod]
    [DataRow(SupportCodeMode.Package)]
    [DataRow(SupportCodeMode.Internal)]
    public void LibrarySetBuildsWithoutWarnings(SupportCodeMode supportCode)
    {
        string directory = TestEnvironment.CreateDirectory($"set-{supportCode}");
        bool package = supportCode == SupportCodeMode.Package;
        var result = new LibrarySetGenerator(_pipeline).Generate(
            [new LibrarySetReference { Source = TestEnvironment.TestTypeLibrary }, new LibrarySetReference { Source = TestEnvironment.DrawingTypeLibrary, Namespace = "Drawing.Interop" }],
            new LibrarySetOptions
            {
                SupportCode = supportCode,
                SupportNamespace = "Consumer.ComSupport",
                DisableRuntimeMarshalling = true,
                SuppressedWarnings = package ? ["CS1591", "SYSLIB1230"] : ["CS1591"],
            });
        foreach (var document in result.Documents)
        {
            string path = Path.Combine(directory, "CsCom", document.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, document.GetText());
        }

        // A project that generates documentation and uses implicit usings, like many projects that would reference the libraries.
        // The COM generator's code for public interfaces reports CA2256 (IDispatch is in CsCom.Runtime) and CS1591 (it redeclares
        // the members of base interfaces without documentation); analyzer configurations do not apply to generated code.
        File.WriteAllText(Path.Combine(directory, "nuget.config"), ProjectFile.CreateNuGetConfig(TestEnvironment.LocalPackages, TestEnvironment.GlobalPackagesFolder));
        string project = Path.Combine(directory, "Consumer.csproj");
        File.WriteAllText(project, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{TestEnvironment.TargetFramework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
                <NoWarn Condition="{(package ? "true" : "false")}">$(NoWarn);CA2256;CS1591</NoWarn>
              </PropertyGroup>
              <ItemGroup Condition="{(package ? "true" : "false")}">
                <PackageReference Include="CsCom.Runtime" Version="{ToolInfo.Version}" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Usage.cs"), """
            using Drawing.Interop;
            using Interop.CsComTest;

            namespace Consumer;

            internal static class Usage
            {
                public static global::Interop.stdole.Font? GetFont(global::Drawing.Interop.ICanvas canvas, global::Interop.CsComTest.IShape shape) =>
                    canvas.Font ?? shape.Font();
            }
            """);

        // Types around and in the namespace of the support code, named like types of .NET that it uses, as type libraries
        // name theirs, e.g. Action of Excel, Exception of Outlook and Task of Word, and namespaces named System: the support
        // code imports the types of .NET in its namespace, which the types around it cannot hide, and names them with
        // global::, which the types in it cannot hide either, also in the code of the COM generators. It calls extension
        // methods through their classes, so that those of its namespace are not found first.
        File.WriteAllText(Path.Combine(directory, "Hostile.cs"), """
            namespace Consumer
            {
                internal sealed class Action { }

                internal sealed class Exception { }

                internal sealed class Task { }

                internal sealed class Marshal { }

                internal sealed class Guid { }

                internal sealed class Type { }
            }

            namespace Consumer.System
            {
                internal static class Placeholder { }
            }

            namespace Consumer.ComSupport
            {
                internal sealed class Action { }

                internal sealed class Exception { }

                internal sealed class Task { }

                internal sealed class ValueTask { }

                internal sealed class Marshal { }

                internal sealed class Guid { }

                internal sealed class Type { }

                internal sealed class Lock { }

                internal sealed class List<T> { }

                internal sealed class Interlocked { }

                internal sealed class Delegate { }

                internal sealed class GeneratedComInterfaceAttribute { }

                // Extension methods of the namespace, which a call through a receiver would find before those that the
                // using directives import, and fail to compile with.
                internal static class HostileExtensions
                {
                    private const string Message = "An extension method of the namespace of the support code.";

                    [global::System.Obsolete(Message, error: true)]
                    public static int AsSpan(this string text) => 0;

                    [global::System.Obsolete(Message, error: true)]
                    public static int AsSpan(this string text, int start) => 0;

                    [global::System.Obsolete(Message, error: true)]
                    public static int AsSpan<T>(this T[] array) => 0;

                    [global::System.Obsolete(Message, error: true)]
                    public static int AsSpan<T>(this T[] array, int start) => 0;

                    [global::System.Obsolete(Message, error: true)]
                    public static int AsSpan<T>(this T[] array, int start, int length) => 0;

                    [global::System.Obsolete(Message, error: true)]
                    public static int Select<TSource, TResult>(this global::System.Collections.Generic.IEnumerable<TSource> source, global::System.Func<TSource, TResult> selector) => 0;

                    [global::System.Obsolete(Message, error: true)]
                    public static int ToHashSet<T>(this global::System.Collections.Generic.IEnumerable<T> source) => 0;

                    [global::System.Obsolete(Message, error: true)]
                    public static int GetValueOrDefault<TKey, TValue>(this global::System.Collections.Generic.IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) => 0;
                }
            }

            namespace Consumer.ComSupport.System
            {
                internal static class Placeholder { }
            }
            """);

        Build(project);
    }

    [TestMethod]
    public void BuildAssemblyCopiesTheOutputsOfEachRuntime()
    {
        string output = TestEnvironment.CreateDirectory("assembly");

        var files = CreateAssemblyGenerator().BuildAssembly(new BuildAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            RuntimeIdentifiers = ["win-x64", "win-arm64"],
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        foreach (string runtime in new[] { "win-x64", "win-arm64" })
        {
            string directory = Path.Combine(output, TestEnvironment.TargetFramework, runtime);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "Interop.CsComTest.dll")), directory);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "Interop.CsComTest.xml")), directory);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "CsCom.Runtime.dll")), directory);
        }

        Assert.IsTrue(files.All(File.Exists));
    }

    [TestMethod]
    public void BuildAssemblyStopsTheBuildAndDeletesTheProjectWhenCancelled()
    {
        // The build is cancelled when it starts, as Ctrl+C does: dotnet is stopped and nothing is left behind or copied.
        string output = TestEnvironment.CreateDirectory("assembly-cancelled");
        string temporary = TestEnvironment.CreateDirectory("assembly-cancelled-temp");
        using var cancellation = new CancellationTokenSource();
        using var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory([new CancellingLoggerProvider(cancellation)]);
        var generator = new AssemblyGenerator(_pipeline, loggerFactory) { TemporaryRoot = temporary };
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() => generator.BuildAssembly(
            new BuildAssemblyRequest
            {
                Sources = [TestEnvironment.TestTypeLibrary],
                OutputDirectory = output,
                TargetFrameworks = [TestEnvironment.TargetFramework],
                PackageSource = TestEnvironment.LocalPackages,
                GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
            },
            cancellation.Token));

        Assert.IsTrue(cancellation.IsCancellationRequested, "The build did not start.");
        Assert.IsEmpty(Directory.GetFileSystemEntries(temporary));
        Assert.IsEmpty(Directory.GetFileSystemEntries(output));
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"The build was not stopped: {stopwatch.Elapsed}.");
    }

    // Cancels when dotnet is about to run.
    private sealed class CancellingLoggerProvider(CancellationTokenSource cancellation) : Microsoft.Extensions.Logging.ILoggerProvider, Microsoft.Extensions.Logging.ILogger
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Running dotnet", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        }

        public void Dispose()
        {
        }
    }

    [TestMethod]
    public void BuildAssemblyUsesFlatLayoutForSingleTarget()
    {
        string output = TestEnvironment.CreateDirectory("assembly-flat");

        CreateAssemblyGenerator().BuildAssembly(new BuildAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            SupportCode = SupportCodeMode.Internal,
            Name = "Flat.Interop",
            Properties = new Dictionary<string, string> { ["Version"] = "3.4.5", ["company"] = "CsCom Tests" },
            TargetFrameworks = [TestEnvironment.TargetFramework],
        });

        CollectionAssert.AreEquivalent(
            new[] { "Flat.Interop.dll", "Flat.Interop.pdb", "Flat.Interop.xml" },
            Directory.GetFiles(output).Select(Path.GetFileName).ToArray());

        // The MSBuild properties of the request are set in the project.
        string assembly = Path.Combine(output, "Flat.Interop.dll");
        Assert.AreEqual(new Version(3, 4, 5, 0), System.Reflection.AssemblyName.GetAssemblyName(assembly).Version);
        Assert.AreEqual("CsCom Tests", System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly).CompanyName);
    }

    [TestMethod]
    public void PackAssemblyCreatesThePackages()
    {
        string output = TestEnvironment.CreateDirectory("pack");

        var files = CreateAssemblyGenerator().PackAssembly(new PackAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            PackageVersion = "2.3.4-test",
            Properties = new Dictionary<string, string> { ["Authors"] = "CsCom Tests", ["PackageLicenseExpression"] = "MIT" },
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        CollectionAssert.AreEquivalent(
            new[] { "Interop.CsComTest.2.3.4-test.nupkg", "Interop.CsComTest.2.3.4-test.snupkg" },
            files.Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEquivalent(files.ToArray(), Directory.GetFiles(output));

        var (entries, nuspec) = ReadPackage(Path.Combine(output, "Interop.CsComTest.2.3.4-test.nupkg"));
        string lib = $"lib/{TestEnvironment.TargetFramework}";
        Assert.IsTrue(entries.Any(e => e.StartsWith(lib, StringComparison.Ordinal) && e.EndsWith("/Interop.CsComTest.dll", StringComparison.Ordinal)), string.Join(", ", entries));
        Assert.IsTrue(entries.Any(e => e.StartsWith(lib, StringComparison.Ordinal) && e.EndsWith("/Interop.CsComTest.xml", StringComparison.Ordinal)), string.Join(", ", entries));
        Assert.IsFalse(entries.Any(static e => e.EndsWith(".pdb", StringComparison.Ordinal)), string.Join(", ", entries));
        Assert.AreEqual("2.3.4-test", NuspecValue(nuspec, "version"));
        Assert.AreEqual("CsCom Tests", NuspecValue(nuspec, "authors"));
        Assert.AreEqual("MIT", NuspecValue(nuspec, "license"));
        Assert.AreEqual("CsCom test type library: COM interop types generated by CsCom.", NuspecValue(nuspec, "description"));
        var dependency = nuspec.Descendants().Single(static e => e.Name.LocalName == "dependency");
        Assert.AreEqual("CsCom.Runtime", (string?)dependency.Attribute("id"));
        Assert.AreEqual(ToolInfo.Version, (string?)dependency.Attribute("version"));

        var (symbols, _) = ReadPackage(Path.Combine(output, "Interop.CsComTest.2.3.4-test.snupkg"));
        Assert.IsTrue(symbols.Any(static e => e.EndsWith("/Interop.CsComTest.pdb", StringComparison.Ordinal)), string.Join(", ", symbols));
    }

    [TestMethod]
    public void PackAssemblyUsesTheTypeLibraryVersionByDefault()
    {
        string output = TestEnvironment.CreateDirectory("pack-default");

        var files = CreateAssemblyGenerator().PackAssembly(new PackAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            SupportCode = SupportCodeMode.Internal,
            TargetFrameworks = [TestEnvironment.TargetFramework],
        });

        CollectionAssert.AreEquivalent(
            new[] { "Interop.CsComTest.1.2.0.nupkg", "Interop.CsComTest.1.2.0.snupkg" },
            files.Select(Path.GetFileName).ToArray());
        var (_, nuspec) = ReadPackage(files.Single(static f => f.EndsWith(".nupkg", StringComparison.Ordinal)));
        Assert.AreEqual("1.2.0", NuspecValue(nuspec, "version"));
        Assert.IsFalse(nuspec.Descendants().Any(static e => e.Name.LocalName == "dependency"));
    }

    [TestMethod]
    public void PackAssemblyOfSeveralSources()
    {
        string output = TestEnvironment.CreateDirectory("pack-several");
        var request = new PackAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary],
            OutputDirectory = output,
            SupportCode = SupportCodeMode.Internal,
        };

        var exception = Assert.ThrowsExactly<CsComException>(() => CreateAssemblyGenerator().PackAssembly(request));
        var files = CreateAssemblyGenerator().PackAssembly(new PackAssemblyRequest
        {
            Sources = request.Sources,
            OutputDirectory = output,
            SupportCode = SupportCodeMode.Internal,
            Name = "Set.Interop",
            TargetFrameworks = [TestEnvironment.TargetFramework],
        });

        Assert.AreEqual("The name of the project or assembly must be specified when there are several sources.", exception.Message);
        CollectionAssert.AreEquivalent(new[] { "Set.Interop.1.0.0.nupkg", "Set.Interop.1.0.0.snupkg" }, files.Select(Path.GetFileName).ToArray());
        var (_, nuspec) = ReadPackage(files.Single(static f => f.EndsWith(".nupkg", StringComparison.Ordinal)));
        Assert.AreEqual("CsComTest, CsComDrawing: COM interop types generated by CsCom.", NuspecValue(nuspec, "description"));
    }

    [TestMethod]
    public void PackAssemblyForSeveralTargetFrameworks()
    {
        string output = TestEnvironment.CreateDirectory("pack-multi-target");

        var files = CreateAssemblyGenerator().PackAssembly(new PackAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            TargetFrameworks = SupportedTargetFrameworks,
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });

        // The package has an assembly and a dependency on CsCom.Runtime for each target framework, e.g. net11.0-windows7.0.
        var (entries, nuspec) = ReadPackage(files.Single(static f => f.EndsWith(".nupkg", StringComparison.Ordinal)));
        var groups = nuspec.Descendants().Where(static e => e.Name.LocalName == "group").ToList();
        foreach (string targetFramework in SupportedTargetFrameworks)
        {
            Assert.IsTrue(entries.Any(e => e.StartsWith($"lib/{targetFramework}", StringComparison.Ordinal) && e.EndsWith("/Interop.CsComTest.dll", StringComparison.Ordinal)), string.Join(", ", entries));
            var group = groups.Single(g => ((string?)g.Attribute("targetFramework"))?.StartsWith(targetFramework, StringComparison.OrdinalIgnoreCase) == true);
            Assert.AreEqual("CsCom.Runtime", (string?)group.Elements().Single().Attribute("id"));
        }
    }

    [TestMethod]
    [DataRow("CsCom.Runtime")]
    [DataRow("CsCom.Runtime.Dynamic")]
    public void RuntimePackagesHaveABuildForEachTargetFramework(string id)
    {
        // Generated interfaces that derive from IDispatch need CsCom.Runtime built for their target framework (SYSLIB1230).
        string[] targetFrameworks = ["net10.0", "net11.0"];
        string package = Path.Combine(TestEnvironment.LocalPackages, $"{id}.{ToolInfo.Version}");

        var (entries, nuspec) = ReadPackage(package + ".nupkg");
        var (symbols, _) = ReadPackage(package + ".snupkg");

        foreach (string targetFramework in targetFrameworks)
        {
            CollectionAssert.IsSubsetOf(new[] { $"lib/{targetFramework}/{id}.dll", $"lib/{targetFramework}/{id}.xml" }, entries, string.Join(", ", entries));
            CollectionAssert.Contains(symbols, $"lib/{targetFramework}/{id}.pdb", string.Join(", ", symbols));
        }

        var groups = nuspec.Descendants().Where(static e => e.Name.LocalName == "group").ToList();
        CollectionAssert.AreEquivalent(targetFrameworks, groups.Select(static g => (string?)g.Attribute("targetFramework")).ToArray());
        string[] dependencies = id == "CsCom.Runtime" ? [] : ["CsCom.Runtime"];
        foreach (var group in groups)
        {
            CollectionAssert.AreEqual(dependencies, group.Elements().Select(static d => (string?)d.Attribute("id")).ToArray());
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProjectOfSeveralSourcesBuildsWithoutWarnings(bool singleFile)
    {
        string root = TestEnvironment.CreateDirectory($"build-several-{singleFile}");
        string project = new ProjectGenerator(_pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary, "Scripting.Dictionary"],
            OutputDirectory = Path.Combine(root, "Interop"),
            Name = "Set.Interop",
            Namespaces = new Dictionary<string, string> { ["CsComDrawing"] = "Drawing.Interop" },
            SingleFile = singleFile,
            TargetFrameworks = [TestEnvironment.TargetFramework],
            PackageSource = TestEnvironment.LocalPackages,
            GlobalPackagesFolder = TestEnvironment.GlobalPackagesFolder,
        });
        Build(project);

        var output = RunConsumer(root, project, """
            using Interop.Scripting;

            var dictionary = Dictionary.Create();
            dictionary.Add("a", "one");
            Console.WriteLine($"Count={dictionary.Count}");
            Console.WriteLine(typeof(Drawing.Interop.ICanvas).FullName);
            Console.WriteLine(typeof(Interop.stdole.Font).FullName);
            Console.WriteLine(typeof(Interop.CsComTest.IShape).FullName);
            """);

        CollectionAssert.AreEqual(
            new[] { "Count=1", "Drawing.Interop.ICanvas", "Interop.stdole.Font", "Interop.CsComTest.IShape" },
            output.TakeLast(4).ToArray());
    }

    // Reads the names of the entries and the nuspec of a package.
    private static (List<string> Entries, System.Xml.Linq.XDocument Nuspec) ReadPackage(string path)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(path);
        var nuspec = archive.Entries.Single(static e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using var stream = nuspec.Open();
        return ([.. archive.Entries.Select(static e => e.FullName)], System.Xml.Linq.XDocument.Load(stream));
    }

    private static string? NuspecValue(System.Xml.Linq.XDocument nuspec, string name) =>
        nuspec.Descendants().SingleOrDefault(e => e.Name.LocalName == name)?.Value;

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
                <TargetFramework>{TestEnvironment.TargetFramework}</TargetFramework>
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

    // The supported .NET versions, which projects can target together.
    private static readonly string[] SupportedTargetFrameworks = ["net10.0-windows", "net11.0-windows"];

    private static readonly MarshallingOptions AllWrappers = new()
    {
        Dates = ValueMarshalling.Wrapper,
        Currencies = ValueMarshalling.Wrapper,
        Booleans = ValueMarshalling.Wrapper,
    };

    private string CreateProject(
        string typeLibrary,
        string directory,
        SupportCodeMode supportCode,
        bool singleFile,
        MarshallingOptions? marshalling = null,
        EventStyle eventStyle = EventStyle.Events,
        StructProperties structProperties = StructProperties.Auto,
        bool dynamic = false) =>
        new ProjectGenerator(_pipeline).CreateProject(new CreateProjectRequest
        {
            Sources = [typeLibrary],
            OutputDirectory = directory,
            SupportCode = supportCode,
            SingleFile = singleFile,
            Marshalling = marshalling ?? new MarshallingOptions(),
            EventStyle = eventStyle,
            StructProperties = structProperties,
            Dynamic = dynamic,
            TargetFrameworks = [TestEnvironment.TargetFramework],
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
}

/// <summary>
/// Moves the setters of <c>IShape.Name</c> and of its default member <c>Item</c> into <c>ICircle</c>, which derives from it, as
/// if a later version of the interface had added them to the read-only properties.
/// </summary>
internal sealed class AccessorsInDerivedInterfaceTransformer : IModelTransformer
{
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        // The transformers also run on the libraries whose types are imported, e.g. stdole.
        var interfaces = library.Types.OfType<InterfaceDefinition>().ToList();
        if (interfaces.SingleOrDefault(static i => i.Name == "IShape") is not { } shape
            || interfaces.SingleOrDefault(static i => i.Name == "ICircle") is not { } circle)
        {
            return;
        }

        foreach (var setter in shape.Methods.Where(static m => m.InvokeKind == InvokeKind.PropertyPut && m.Name is "Name" or "Item").ToList())
        {
            shape.Methods.Remove(setter);
            circle.Methods.Add(setter);
        }
    }
}

/// <summary>
/// Turns <c>ICalculator.Enumerator</c>, which returns an <c>IEnumVARIANT</c>, into a <c>_NewEnum</c> that returns the
/// dispinterface <c>DShapeEvents</c>, with the same vtable slot and layout.
/// </summary>
internal sealed class DispatchEnumeratorTransformer : IModelTransformer
{
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        // The transformers also run on the libraries whose types are imported, e.g. stdole.
        var interfaces = library.Types.OfType<InterfaceDefinition>().ToList();
        if (interfaces.SingleOrDefault(static i => i.Name == "ICalculator") is not { } calculator
            || interfaces.SingleOrDefault(static i => i.Name == "DShapeEvents") is not { } events)
        {
            return;
        }

        // MIDL stores the name of the method as that of its parameter, enumerator. The member identifier cannot be changed,
        // so the method is replaced.
        int index = calculator.Methods.FindIndex(static m => string.Equals(m.Name, "Enumerator", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException("The test type library has no method ICalculator.Enumerator.");
        }

        var method = calculator.Methods[index];
        var newEnum = new MethodDefinition
        {
            Name = method.Name,
            OriginalName = method.OriginalName,
            MemberId = -4,
            InvokeKind = method.InvokeKind,
            CallingConvention = method.CallingConvention,
            VtableSlot = method.VtableSlot,
            ReturnType = method.ReturnType,
            Flags = method.Flags,
            Documentation = method.Documentation,
        };
        var retval = method.RetvalParameter!;
        retval.Type = new PointerTypeReference(new PointerTypeReference(new UserDefinedTypeReference(events)));
        newEnum.Parameters.Add(retval);
        calculator.Methods[index] = newEnum;
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
