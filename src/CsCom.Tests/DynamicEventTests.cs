using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;
using Microsoft.CSharp.RuntimeBinder;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace CsCom.Tests;

/// <summary>
/// Tests the events and the type information of <c>dynamic</c> objects with a COM object implemented by the test, which
/// describes itself with the types of the test type library (<c>IShape</c> of the coclasses <c>EventShape</c> or <c>Shape</c>)
/// and raises events through connection points.
/// </summary>
[TestClass]
public sealed partial class DynamicEventTests
{
    private static readonly Guid ShapeIid = new("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1011");
    private static readonly Guid DShapeEventsIid = new("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1013");
    private static readonly Guid ShapeEventsIid = new("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1015");
    private static readonly Guid EventShapeClsid = new("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1023");
    private static readonly Lazy<ComTypes.ITypeLib> TypeLibrary = new(LoadTypeLibrary);

    private delegate void ClosingHandler(ref bool cancel);

    private delegate string DescribeHandler(object? value, out int length);

    [TestMethod]
    public void AddsAndRemovesHandlersWithOperators()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var moves = new List<(int X, int Y)>();
        var handler = (Action<int, int>)((x, y) => moves.Add((x, y)));

        shape.Moved += handler;
        int connected = source.Connections;
        Fire(source, ShapeEventsIid, 1, 1, 2);
        shape.Moved -= handler;

        Assert.AreEqual(1, connected);
        Assert.AreEqual(0, source.Connections);
        CollectionAssert.AreEqual(new[] { (1, 2) }, moves);
    }

    [TestMethod]
    public void ReturnsByRefValuesAndResults()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        shape.Closing += new ClosingHandler((ref bool cancel) => cancel = true);
        shape.Describe += new DescribeHandler((object? value, out int length) =>
        {
            length = 3;
            return $"value {value}";
        });

        object?[] closing = [false];
        FireByRef(source, ShapeEventsIid, 2, closing, [true]);
        object?[] describe = [42, null];
        object? description = FireByRef(source, ShapeEventsIid, 4, describe, [false, true]);

        Assert.AreEqual(true, closing[0]);
        Assert.AreEqual("value 42", description);
        Assert.AreEqual(3, describe[1]);
    }

    [TestMethod]
    public void CombinesHandlers()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var calls = new List<string>();
        var first = (Action<int, int>)((x, y) => calls.Add($"first {x}"));
        var second = (Action<int>)(x => calls.Add($"second {x}"));

        shape.Moved += first;
        shape.Moved += second;
        Fire(source, ShapeEventsIid, 1, 1, 2);
        shape.Moved -= first;
        Fire(source, ShapeEventsIid, 1, 3, 4);
        int connected = source.Connections;
        shape.Moved -= second;

        CollectionAssert.AreEqual(new[] { "first 1", "second 1", "second 3" }, calls);
        Assert.AreEqual(1, connected);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AddsHandlersByName()
    {
        var source = new EventSource(provideClassInfo: true);
        object shape = CreateProxy(source);
        var calls = new List<string?>();

        ComDynamic.AddEventHandler(shape, "renamed", (Action<string?>)calls.Add);
        ComDynamic.AddEventHandler(shape, "Moved", (Action)(() => calls.Add("moved")));
        Fire(source, ShapeEventsIid, 3, "a");
        Fire(source, ShapeEventsIid, 1, 1, 2);
        ComDynamic.RemoveEventHandlers(shape);

        CollectionAssert.AreEqual(new[] { "a", "moved" }, calls);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void RejectsUnknownEventsAndIncompatibleHandlers()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);

        Assert.ThrowsExactly<RuntimeBinderException>(() => shape.NoSuchEvent += (Action)(() => { }));
        var unknown = Assert.ThrowsExactly<ArgumentException>(() => ComDynamic.AddEventHandler((object)shape, "NoSuchEvent", (Action)(() => { })));
        var tooMany = Assert.ThrowsExactly<ArgumentException>(() => shape.Moved += (Action<int, int, int>)((x, y, z) => { }));

        StringAssert.StartsWith(unknown.Message, "The COM object has no event named 'NoSuchEvent'.");
        StringAssert.StartsWith(tooMany.Message, "The handler has 3 parameters, but the event 'Moved' has 2.");
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void FindsTheClassInTheTypeLibraryWithoutClassInfo()
    {
        // Without IProvideClassInfo, the coclass is the first one whose default interface is IShape: Shape, whose events are
        // those of DShapeEvents.
        var source = new EventSource(provideClassInfo: false);
        dynamic shape = CreateProxy(source);
        var changes = new List<string?>();

        shape.Changed += (Action<string?>)changes.Add;
        Fire(source, DShapeEventsIid, 10, "Name");
        ComDynamic.RemoveEventHandlers((object)shape);

        CollectionAssert.AreEqual(new[] { "Name" }, changes);
        Assert.ThrowsExactly<RuntimeBinderException>(() => shape.Moved += (Action)(() => { }));
    }

    [TestMethod]
    public void ListsPropertiesForDebuggers()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);

        var names = ((DynamicDispatchObject)shape).GetDynamicMemberNames().ToList();

        // Item and Coordinate require arguments, WriteOnly cannot be read and _NewEnum is restricted.
        CollectionAssert.AreEqual(new[] { "Name", "Color", "Area", "Parent", "Tag" }, names);
        Assert.AreEqual("IShape", ComDynamic.GetTypeName((object)shape));
    }

    private static dynamic CreateProxy(EventSource source)
    {
        var wrappers = new StrategyBasedComWrappers();
        nint unknown = wrappers.GetOrCreateComInterfaceForObject(source, CreateComInterfaceFlags.None);
        try
        {
            return ((IDispatch)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance)).AsDynamic();
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static void Fire(EventSource source, Guid iid, int dispId, params object?[] args)
    {
        foreach (IDispatch sink in GetSinks(source, iid))
        {
            DispatchObject.Invoke(sink, dispId, DispatchFlags.Method, args);
        }
    }

    private static object? FireByRef(EventSource source, Guid iid, int dispId, object?[] args, bool[] byRef)
    {
        object? result = null;
        foreach (IDispatch sink in GetSinks(source, iid))
        {
            result = DispatchObject.InvokeByRef(sink, dispId, DispatchFlags.Method, args, byRef);
        }

        return result;
    }

    private static List<IDispatch> GetSinks(EventSource source, Guid iid)
    {
        var wrappers = new StrategyBasedComWrappers();
        return [.. source.Points[iid].Sinks.Values.Select(sink => (IDispatch)wrappers.GetOrCreateObjectForComInstance(sink, CreateObjectFlags.UniqueInstance))];
    }

    private static ComTypes.ITypeLib LoadTypeLibrary()
    {
        Marshal.ThrowExceptionForHR(LoadTypeLibEx(TestEnvironment.TestTypeLibrary, 0, out ComTypes.ITypeLib typeLibrary));
        return typeLibrary;
    }

    private static nint GetTypeInfo(Guid guid)
    {
        TypeLibrary.Value.GetTypeInfoOfGuid(ref guid, out ComTypes.ITypeInfo typeInfo);
        return Marshal.GetComInterfaceForObject<ComTypes.ITypeInfo, ComTypes.ITypeInfo>(typeInfo);
    }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadTypeLibEx(string file, int registerKind, out ComTypes.ITypeLib typeLibrary);

    /// <summary>
    /// A COM object described by <c>IShape</c>, optionally of the coclass <c>EventShape</c> through <c>IProvideClassInfo</c>,
    /// which raises the events of <c>_ShapeEvents</c> and <c>DShapeEvents</c>.
    /// </summary>
    [GeneratedComClass]
    internal sealed unsafe partial class EventSource(bool provideClassInfo) : IDispatch, IProvideClassInfo, IConnectionPointContainer, ICustomQueryInterface
    {
        private static readonly Guid IID_IProvideClassInfo = typeof(IProvideClassInfo).GUID;

        public Dictionary<Guid, ConnectionPoint> Points { get; } = new()
        {
            [ShapeEventsIid] = new(ShapeEventsIid),
            [DShapeEventsIid] = new(DShapeEventsIid),
        };

        public int Connections => Points.Values.Sum(static p => p.Sinks.Count);

        public int GetTypeInfoCount(out uint pctinfo)
        {
            pctinfo = 1;
            return 0;
        }

        public int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
        {
            ppTInfo = DynamicEventTests.GetTypeInfo(ShapeIid);
            return 0;
        }

        public int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId)
        {
            for (uint i = 0; i < cNames; i++)
            {
                rgDispId[i] = -1;
            }

            return unchecked((int)0x80020006);
        }

        public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr)
        {
            return unchecked((int)0x80020003);
        }

        public int GetClassInfo(out nint ppTI)
        {
            ppTI = DynamicEventTests.GetTypeInfo(EventShapeClsid);
            return 0;
        }

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

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
        {
            ppv = 0;
            return iid == IID_IProvideClassInfo && !provideClassInfo ? CustomQueryInterfaceResult.Failed : CustomQueryInterfaceResult.NotHandled;
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
}
