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
    [DataRow(SupportCodeMode.Package, false, "default", EventStyle.Events)]
    [DataRow(SupportCodeMode.Public, false, "default", EventStyle.Events)]
    [DataRow(SupportCodeMode.Internal, true, "default", EventStyle.Events)]
    [DataRow(SupportCodeMode.Package, false, "com", EventStyle.Events)]
    [DataRow(SupportCodeMode.Internal, false, "wrapper", EventStyle.Events)]
    [DataRow(SupportCodeMode.Public, true, "default", EventStyle.Observable)]
    [DataRow(SupportCodeMode.Internal, false, "wrapper", EventStyle.Interface)]
    [DataRow(SupportCodeMode.Package, false, "default", EventStyle.None)]
    public void GeneratedTestProjectBuildsWithoutWarnings(SupportCodeMode supportCode, bool singleFile, string marshalling, EventStyle eventStyle)
    {
        string directory = TestEnvironment.CreateDirectory($"build-{supportCode}-{marshalling}-{eventStyle}");
        var options = marshalling switch
        {
            "com" => new MarshallingOptions { DateParameters = ParameterMarshalling.Com, CurrencyParameters = ParameterMarshalling.Com, BoolParameters = ParameterMarshalling.Com },
            "wrapper" => AllWrappers,
            _ => new MarshallingOptions(),
        };
        string project = CreateProject(TestEnvironment.TestTypeLibrary, directory, supportCode, singleFile, options, eventStyle);

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
            .Replace("__RECORD__", wrappers
                ? """
                  var record = new Record { Date = date, Money = 12.5m, Flag = true };
                  DateTime recordDate = record.Date;
                  decimal recordMoney = record.Money;
                  bool recordFlag = record.Flag;
                  Console.WriteLine($"Record={recordDate:yyyy-MM-dd}|{recordMoney:0.##}|{recordFlag}|{record.Flag.Value}");
                  """
                : string.Empty, StringComparison.Ordinal);
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
            .. wrappers ? ["Record=2024-02-29|12.5|True|-1"] : Array.Empty<string>(),
        ];
        CollectionAssert.IsSubsetOf(
            expected,
            output.ToArray(),
            string.Join(Environment.NewLine, output));
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
        DateParameters = ParameterMarshalling.Wrapper,
        DateFields = FieldMarshalling.Wrapper,
        CurrencyParameters = ParameterMarshalling.Wrapper,
        CurrencyFields = FieldMarshalling.Wrapper,
        BoolParameters = ParameterMarshalling.Wrapper,
        BoolFields = FieldMarshalling.Wrapper,
    };

    private string CreateProject(
        string typeLibrary,
        string directory,
        SupportCodeMode supportCode,
        bool singleFile,
        MarshallingOptions? marshalling = null,
        EventStyle eventStyle = EventStyle.Events) =>
        new ProjectGenerator(_pipeline).CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = typeLibrary,
            OutputDirectory = directory,
            SupportCode = supportCode,
            SingleFile = singleFile,
            Marshalling = marshalling ?? new MarshallingOptions(),
            EventStyle = eventStyle,
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
