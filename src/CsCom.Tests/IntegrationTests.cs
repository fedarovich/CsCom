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
    [DataRow(SupportCodeMode.Package, false, "default", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Public, false, "default", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Internal, true, "default", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Package, false, "none", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Internal, false, "wrapper", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Public, true, "default", EventStyle.Observable, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Internal, false, "wrapper", EventStyle.Interface, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Package, false, "default", EventStyle.None, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Package, false, "safearray-sysarray", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Internal, true, "safearray-wrapper", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Public, false, "safearray-none", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Internal, true, "bstr-wrapper", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Package, false, "bstr-none", EventStyle.Events, StructProperties.NonBlittable)]
    [DataRow(SupportCodeMode.Public, false, "default", EventStyle.Events, StructProperties.Always)]
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
        // The consumer implements a COM object that raises events through connection points and connects the generated sinks
        // to a runtime callable wrapper of it. The object calls the sinks through runtime callable wrappers as well, so that
        // the events are marshalled to native code and back: dispinterface and dual events through IDispatch::Invoke,
        // vtable and dual events through the vtable.
        string root = TestEnvironment.CreateDirectory($"events-{style}");
        string interop = CreateProject(TestEnvironment.TestTypeLibrary, Path.Combine(root, "Interop"), SupportCodeMode.Package, singleFile: false, eventStyle: style);

        (string Source, string Label)[] sources = [("_ShapeEvents", "Dispatch"), ("IShapeEventsVt", "Vt"), ("IShapeEventsDual", "Dual")];
        string program = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using CsCom.Runtime;
            using Interop.CsComTest;

            var wrappers = new StrategyBasedComWrappers();
            var source = new EventSource();
            nint unknown = wrappers.GetOrCreateComInterfaceForObject(source, CreateComInterfaceFlags.None);
            object proxy = wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Marshal.Release(unknown);
            var disposables = new List<IDisposable>();

            __CONNECT__

            Console.WriteLine($"Connections={source.Connections}");
            Fire(wrappers, source);
            foreach (var disposable in disposables)
            {
                disposable.Dispose();
            }

            Console.WriteLine($"Connections={source.Connections}");

            static void Fire(StrategyBasedComWrappers wrappers, EventSource source)
            {
                foreach (nint sink in source.Points[_ShapeEventsSink.Iid].Sinks.Values)
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

                foreach (nint sink in source.Points[IShapeEventsVtSink.Iid].Sinks.Values)
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

                foreach (nint sink in source.Points[IShapeEventsDualSink.Iid].Sinks.Values)
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
                    return 0;
                }

                public int Unadvise(uint dwCookie)
                {
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

            __HANDLERS__
            """;

        program = program
            .Replace("__CONNECT__", string.Join(Environment.NewLine, sources.Select(s => ConnectEvents(style, s.Source, s.Label))), StringComparison.Ordinal)
            .Replace("__HANDLERS__", style == EventStyle.Interface ? string.Join(Environment.NewLine, sources.Select(static s => HandlerClass(s.Source, s.Label))) : string.Empty, StringComparison.Ordinal);
        var output = RunConsumer(root, interop, program, allowUnsafe: true);

        string[] expected =
        [
            "Connections=3",
            "Dispatch.Moved=1,2", "Dispatch.Closing=True", "DispatchInvoke.Cancel=True", "Dispatch.Renamed=renamed", "DispatchInvoke.Describe=value 42|7", "Error=boom",
            "Vt.Moved=1,2", "Vt.Closing=True", "VtFire.Cancel=True", "Vt.Renamed=renamed", "VtFire.Describe=value 42|7",
            "Dual.Moved=1,2", "Dual.Closing=True", "DualFire.Cancel=True", "Dual.Renamed=renamed", "DualFire.Describe=value 42|7",
            "DualInvoke.Cancel=True", "DualInvoke.Describe=value 42|7",
            .. style == EventStyle.Observable ? ["Dispatch.Completed", "Vt.Completed", "Dual.Completed"] : Array.Empty<string>(),
        ];
        CollectionAssert.IsSubsetOf(expected, output.ToArray(), string.Join(Environment.NewLine, output));
        Assert.AreEqual("Connections=0", output.Last(), string.Join(Environment.NewLine, output));
        Assert.HasCount(2, output.Where(static line => line == "Dual.Moved=1,2"), string.Join(Environment.NewLine, output));
    }

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
        StructProperties structProperties = StructProperties.NonBlittable) =>
        new ProjectGenerator(_pipeline).CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = typeLibrary,
            OutputDirectory = directory,
            SupportCode = supportCode,
            SingleFile = singleFile,
            Marshalling = marshalling ?? new MarshallingOptions(),
            EventStyle = eventStyle,
            StructProperties = structProperties,
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
