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

    internal enum Tint
    {
        Red = 1,
        Blue = 3,
    }

    [TestMethod]
    public void ConvertsArraysToTheParameterTypes()
    {
        // The CLR also considers an int[] to be a Tint[] and a string[] to be an object[], so the arrays are copied: the elements
        // of a string[] seen as an object[] could not be set to other objects. The int arrays are copied to enum arrays as is,
        // the elements of the others are converted.
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var moves = new List<(object?[] Names, Tint[] Tints)>();
        Tint[,]? grid = null;
        shape.Moved += (Action<object?[], Tint[]>)((names, tints) => moves.Add((names, tints)));
        shape.Renamed += (Action<Tint[,]>)(tints => grid = tints);

        Fire(source, ShapeEventsIid, 1, new[] { "a", "b" }, new[] { 1, 3 });
        Fire(source, ShapeEventsIid, 1, new object?[] { "c" }, new object?[] { 3, 1 });
        Fire(source, ShapeEventsIid, 3, new[,] { { 1, 3, 1 }, { 3, 1, 3 } });
        ComDynamic.RemoveEventHandlers((object)shape);

        Assert.AreEqual(2, moves.Count);
        Assert.AreEqual(typeof(object[]), moves[0].Names.GetType());
        moves[0].Names[0] = 1;
        CollectionAssert.AreEqual(new object?[] { 1, "b" }, moves[0].Names);
        CollectionAssert.AreEqual(new object?[] { "c" }, moves[1].Names);
        foreach (var (_, tints) in moves)
        {
            Assert.AreEqual(typeof(Tint[]), tints.GetType());
        }

        CollectionAssert.AreEqual(new[] { Tint.Red, Tint.Blue }, moves[0].Tints);
        CollectionAssert.AreEqual(new[] { Tint.Blue, Tint.Red }, moves[1].Tints);
        Assert.AreEqual(typeof(Tint[,]), grid!.GetType());
        Assert.AreEqual((2, 3), (grid.GetLength(0), grid.GetLength(1)));
        Assert.AreEqual(Tint.Blue, grid[0, 1]);
        Assert.AreEqual(Tint.Red, grid[1, 1]);
        Assert.AreEqual(Tint.Blue, grid[1, 2]);
    }

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
    [DataRow(unchecked((int)0x8001010E), DisplayName = "RPC_E_WRONG_THREAD")]
    [DataRow(unchecked((int)0x80010001), DisplayName = "RPC_E_CALL_REJECTED")]
    [DataRow(unchecked((int)0x8001010A), DisplayName = "RPC_E_SERVERCALL_RETRYLATER")]
    [DataRow(unchecked((int)0x8001010D), DisplayName = "RPC_E_CANTCALLOUT_ININPUTSYNCCALL")]
    public void DisconnectsAgainAfterACallThatCanSucceedLater(int error)
    {
        var source = new EventSource(provideClassInfo: true);
        object shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var connection = EventConnection.AdviseDispatch(shape, ShapeEventsIid, new IgnoringHandler());

        // The sink stays connected, and the connection can be disposed from the thread that made it, or when the server is
        // not busy any more.
        Assert.IsTrue(connection.IsConnected);
        point.UnadviseError = error;
        Assert.AreEqual(error, connection.Disconnect());
        Assert.AreEqual(1, source.Connections);
        Assert.IsTrue(connection.IsConnected);

        Assert.AreEqual(0, connection.Disconnect());
        Assert.AreEqual(0, source.Connections);
        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(0, connection.Disconnect(), "The sink is already disconnected.");

        // Another error gives the connection up, e.g. when the object has disconnected the sink itself.
        connection = EventConnection.AdviseDispatch(shape, ShapeEventsIid, new IgnoringHandler());
        point.UnadviseError = unchecked((int)0x80040200);
        Assert.AreEqual(unchecked((int)0x80040200), connection.Disconnect());
        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(unchecked((int)0x80040200), connection.Disconnect(), "The error that ended the connection.");
        Assert.AreEqual(1, source.Connections);
    }

    [TestMethod]
    public void DisconnectReturnsEverySuccessAsS_OK()
    {
        // S_FALSE only means that another call is disconnecting the sink: a connection point that returns it, or another
        // success, has disconnected the sink.
        const int S_FALSE = 1;
        var source = new EventSource(provideClassInfo: true);
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());

        source.Points[ShapeEventsIid].UnadviseError = S_FALSE;
        Assert.AreEqual(0, connection.Disconnect());
        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(0, connection.Disconnect(), "The HRESULT that ended the connection.");
        Assert.IsTrue(connection.DisposeAsync().IsCompletedSuccessfully);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "Disconnected")]
    [DataRow(unchecked((int)0x80010001), DisplayName = "RPC_E_CALL_REJECTED")]
    public void AHandlerAddedWhileTheSinkIsBeingDisconnectedGetsTheEventsOnce(int error)
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<(int X, int Y)>();
        var first = (Action<int, int>)((_, _) => { });
        var second = (Action<int, int>)((x, y) => moves.Add((x, y)));
        shape.Moved += first;

        // Another thread adds a handler while the removal of the last one is disconnecting the sink: the removal has retired
        // the connection, so the handler connects the sink anew. A connection that the busy server does not disconnect stays
        // until the next change of the handlers, but passes no events on: each event reaches the handler once.
        bool added = false;
        point.UnadviseError = error;
        point.Unadvising = () => added = Task.Run(() => shape.Moved += second).Wait(Timeout);
        shape.Moved -= first;

        Assert.IsTrue(added);
        Assert.AreEqual(error == 0 ? 1 : 2, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        CollectionAssert.AreEqual(new[] { (1, 2) }, moves);

        shape.Moved -= second;
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AHandlerAddedWhileTheSinkIsBeingDisconnectedReportsItsOwnError()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<string>();
        var first = (Action<int, int>)((_, _) => { });
        var second = (Action<int, int>)((x, y) => moves.Add($"second {x},{y}"));
        var third = (Action<int, int>)((x, y) => moves.Add($"third {x},{y}"));
        shape.Moved += first;

        // The handler is added while the removal of the last one disconnects the sink, and its own connection fails: the
        // addition reports the error and adds nothing, and the removal succeeds.
        Exception? error = null;
        point.Unadvising = () =>
        {
            point.AdviseError = unchecked((int)0x80010001);
            error = Task.Run(() =>
            {
                try
                {
                    shape.Moved += second;
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
            }).WaitAsync(Timeout).GetAwaiter().GetResult();
        };
        shape.Moved -= first;

        Assert.IsNotNull(error);
        Assert.AreEqual(0, source.Connections);
        shape.Moved += third;
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        CollectionAssert.AreEqual(new[] { "third 1,2" }, moves);

        shape.Moved -= third;
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AHandlerIsConnectedWhenTheConnectionOfAnotherThreadFails()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<string>();
        var first = (Action<int, int>)((x, y) => moves.Add($"first {x},{y}"));
        var second = (Action<int, int>)((x, y) => moves.Add($"second {x},{y}"));

        // The second handler connects the sink itself while the first one is connecting, and that connection then fails:
        // the second connection takes over, so both additions succeed and each event reaches each handler once.
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        point.Advising = () =>
        {
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
            point.AdviseError = unchecked((int)0x80010001);
        };
        var connecting = Task.Run(() => shape.Moved += first);
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            shape.Moved += second;
        }
        finally
        {
            resume.Set();
        }

        Assert.IsTrue(connecting.Wait(Timeout));
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        CollectionAssert.AreEquivalent(new[] { "first 1,2", "second 1,2" }, moves);

        ComDynamic.RemoveEventHandlers(shape);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void HandlersAddedAtTheSameTimeConnectTheSinkOnce()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<string>();
        var first = (Action<int, int>)((x, y) => moves.Add($"first {x},{y}"));
        var second = (Action<int, int>)((x, y) => moves.Add($"second {x},{y}"));

        // The second handler is added while the first one is connecting the sink: it does not wait, but connects the sink
        // itself, and its connection, which is established first, passes the events on at once, although the COM object
        // does not know the sink of the first one yet. The redundant connection is disconnected when it is established.
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        point.Advising = () =>
        {
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
        };
        var connecting = Task.Run(() => shape.Moved += first);
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            shape.Moved += second;
            Assert.AreEqual(1, source.Connections);
            Fire(source, ShapeEventsIid, 1, 1, 2);
        }
        finally
        {
            resume.Set();
        }

        CollectionAssert.AreEquivalent(new[] { "first 1,2", "second 1,2" }, moves);
        Assert.IsTrue(connecting.Wait(Timeout));
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 3, 4);
        CollectionAssert.AreEquivalent(new[] { "first 1,2", "second 1,2", "first 3,4", "second 3,4" }, moves);

        ComDynamic.RemoveEventHandlers(shape);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AnEstablishedConnectionPassesTheEventsOnWhileAnotherOneIsRegistering()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<string>();
        var first = (Action<int, int>)((x, y) => moves.Add($"first {x},{y}"));
        var second = (Action<int, int>)((x, y) => moves.Add($"second {x},{y}"));

        // The COM object has the sink of the first connection, whose Advise has not returned yet, when the second one is
        // established: the event reaches both sinks, and only the established one passes it on.
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        point.Advised = () =>
        {
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
        };
        var connecting = Task.Run(() => shape.Moved += first);
        int connections;
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            shape.Moved += second;
            connections = source.Connections;
            Fire(source, ShapeEventsIid, 1, 1, 2);
        }
        finally
        {
            resume.Set();
        }

        Assert.AreEqual(2, connections);
        CollectionAssert.AreEquivalent(new[] { "first 1,2", "second 1,2" }, moves);

        // The first connection is redundant when it is established.
        Assert.IsTrue(connecting.Wait(Timeout));
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 3, 4);
        CollectionAssert.AreEquivalent(new[] { "first 1,2", "second 1,2", "first 3,4", "second 3,4" }, moves);

        ComDynamic.RemoveEventHandlers(shape);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AConnectionThatReceivesAnEventWhileConnectingKeepsPassingThemOn(bool fails)
    {
        const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<string>();
        var first = (Action<int, int>)((x, y) => moves.Add($"first {x},{y}"));
        var second = (Action<int, int>)((x, y) => moves.Add($"second {x},{y}"));

        // The first connection receives an event while it is being connected, which shows that the COM object has its
        // sink: it stays the dispatcher, and the second connection, which is established first, is a backup.
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        point.Advised = () =>
        {
            Fire(source, ShapeEventsIid, 1, 1, 2);
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
            if (fails)
            {
                // The connection breaks after the COM object added the sink, which the test object removes again.
                throw new COMException("Disconnected.", RPC_E_DISCONNECTED);
            }
        };
        var connecting = Task.Run(() => shape.Moved += first);
        int connections;
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            shape.Moved += second;
            connections = source.Connections;
            Fire(source, ShapeEventsIid, 1, 3, 4);
        }
        finally
        {
            resume.Set();
        }

        Assert.AreEqual(2, connections);
        CollectionAssert.AreEquivalent(new[] { "first 1,2", "first 3,4", "second 3,4" }, moves);

        // When the first connection is established, the backup is redundant; when it fails, the backup takes over, and
        // serves the first handler too, whose addition therefore succeeds.
        Assert.IsTrue(connecting.Wait(Timeout));
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 5, 6);
        CollectionAssert.AreEquivalent(
            new[] { "first 1,2", "first 3,4", "second 3,4", "first 5,6", "second 5,6" },
            moves);

        ComDynamic.RemoveEventHandlers(shape);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AFailedConnectionRemovesOnlyItsOwnHandler()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        int calls = 0;
        var handler = (Action<int, int>)((_, _) => Interlocked.Increment(ref calls));

        // The first addition is connecting when the handler is removed and added again by another addition, which connects
        // the sink itself; then the first connection fails. The removal took the handler of the first addition, so its
        // failure removes nothing, not even the equal handler of the second addition, which started after the removal and
        // keeps its handler when it succeeds.
        using var enteredFirst = new ManualResetEventSlim();
        using var resumeFirst = new ManualResetEventSlim();
        using var enteredSecond = new ManualResetEventSlim();
        using var resumeSecond = new ManualResetEventSlim();
        point.Advising = () =>
        {
            enteredFirst.Set();
            Assert.IsTrue(resumeFirst.Wait(Timeout));
            point.AdviseError = unchecked((int)0x80010001);
        };
        var first = Task.Run(() => shape.Moved += handler);
        Task second;

        // The second addition is resumed whatever fails, also when the test fails before it waits for it.
        try
        {
            try
            {
                Assert.IsTrue(enteredFirst.Wait(Timeout));
                shape.Moved -= handler;
                point.Advising = () =>
                {
                    enteredSecond.Set();
                    Assert.IsTrue(resumeSecond.Wait(Timeout));
                };
                second = Task.Run(() => shape.Moved += handler);
                Assert.IsTrue(enteredSecond.Wait(Timeout));
            }
            finally
            {
                resumeFirst.Set();
            }

            Assert.IsTrue(((IAsyncResult)first).AsyncWaitHandle.WaitOne(Timeout));
            Assert.IsTrue(first.IsFaulted);
        }
        finally
        {
            resumeSecond.Set();
        }

        Assert.IsTrue(second.Wait(Timeout));
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(1, calls);

        ComDynamic.RemoveEventHandlers(shape);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AHandlerThatCannotBeConnectedIsNotAdded()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var moves = new List<string>();
        var first = (Action<int, int>)((x, y) => moves.Add($"first {x},{y}"));
        var second = (Action<int, int>)((x, y) => moves.Add($"second {x},{y}"));

        source.Points[ShapeEventsIid].AdviseError = unchecked((int)0x80010001);
        Assert.Throws<Exception>(() => shape.Moved += first);
        Assert.AreEqual(0, source.Connections);

        shape.Moved += second;
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        CollectionAssert.AreEqual(new[] { "second 1,2" }, moves);

        shape.Moved -= second;
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void ARefusedDisconnectionIsRetriedUntilItSucceeds()
    {
        // The server refuses to disconnect the sink for a while, e.g. because it is busy: the retired connections pass no
        // events on, and are not given up, so that the change of the handlers after the server is free again disconnects
        // all of them; the source would otherwise keep the sinks, and the sinks the object.
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        point.PersistentUnadviseError = RPC_E_CALL_REJECTED;
        int calls = 0;
        var handler = (Action<int, int>)((_, _) => calls++);

        for (int i = 0; i < 5; i++)
        {
            shape.Moved += handler;
            Fire(source, ShapeEventsIid, 1, 1, 2);
            Assert.AreEqual(i + 1, calls);
            shape.Moved -= handler;
            Assert.AreEqual(i + 1, source.Connections);
        }

        point.PersistentUnadviseError = 0;
        shape.Moved += handler;
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(6, calls);

        shape.Moved -= handler;
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AConnectionPointThatThrowsIsGivenUpAtOnce()
    {
        // Another attempt would throw again, so the connection is not disconnected again by later changes of the handlers.
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var handler = (Action<int, int>)((_, _) => { });

        shape.Moved += handler;
        point.Unadvising = () => throw new InvalidOperationException("Busy.");
        shape.Moved -= handler;
        int attempts = point.UnadviseCalls;

        shape.Moved += handler;
        Assert.AreEqual(attempts, point.UnadviseCalls);
        shape.Moved -= handler;
        Assert.AreEqual(attempts + 1, point.UnadviseCalls);

        // The source keeps the sink of the connection that was given up.
        Assert.AreEqual(1, source.Connections);
    }

    [TestMethod]
    public void ATransientExceptionOfAConnectionPointIsRetried()
    {
        // An exception with a transient HRESULT is like the same HRESULT returned: the next change disconnects the sink.
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var handler = (Action<int, int>)((_, _) => { });

        shape.Moved += handler;
        point.Unadvising = () => throw new COMException("Busy.", RPC_E_CALL_REJECTED);
        shape.Moved -= handler;
        Assert.AreEqual(1, source.Connections);

        shape.Moved += handler;
        Assert.AreEqual(1, source.Connections);
        shape.Moved -= handler;
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void RemovingTheLastHandlerDisconnectsLaterWhenTheServerIsBusy()
    {
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<(int X, int Y)>();
        var handler = (Action<int, int>)((x, y) => moves.Add((x, y)));

        // The sink stays connected, without handlers, and passes no events on.
        shape.Moved += handler;
        point.UnadviseError = RPC_E_CALL_REJECTED;
        shape.Moved -= handler;
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.IsEmpty(moves);

        // A handler that is added connects the sink anew, and the old connection is disconnected: each event reaches the
        // handler once.
        shape.Moved += handler;
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 3, 4);
        CollectionAssert.AreEqual(new[] { (3, 4) }, moves);

        // The next removal disconnects it.
        shape.Moved -= handler;
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void EventsRaisedWhileTheSinkIsBeingConnectedReachTheHandlersOnce()
    {
        // A connection that receives an event while it is being connected becomes the dispatcher: the events that the COM
        // object raises during Advise reach the handlers.
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var moves = new List<(int X, int Y)>();
        var handler = (Action<int, int>)((x, y) => moves.Add((x, y)));

        point.Advised = () => Fire(source, ShapeEventsIid, 1, 1, 2);
        shape.Moved += handler;
        CollectionAssert.AreEqual(new[] { (1, 2) }, moves);

        // A retired connection that cannot be disconnected before the new one is connected receives the event too, but
        // only the new one, which is the dispatcher from the start, passes it on.
        point.UnadviseError = RPC_E_CALL_REJECTED;
        shape.Moved -= handler;
        Assert.AreEqual(1, source.Connections);
        point.UnadviseError = RPC_E_CALL_REJECTED;
        int connectionsDuringAdvise = 0;
        point.Advised = () =>
        {
            connectionsDuringAdvise = source.Connections;
            Fire(source, ShapeEventsIid, 1, 3, 4);
        };
        shape.Moved += handler;
        Assert.AreEqual(2, connectionsDuringAdvise);
        CollectionAssert.AreEqual(new[] { (1, 2), (3, 4) }, moves);
        Assert.AreEqual(1, source.Connections);

        shape.Moved -= handler;
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void ADisconnectedSinkPassesNoEventsOn()
    {
        // The source cannot be told at the moment, and keeps the sink: its events no longer reach the handler.
        const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);
        var source = new EventSource(provideClassInfo: true);
        object shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var handler = new CountingHandler();
        var connection = EventConnection.AdviseDispatch(shape, ShapeEventsIid, handler);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(1, handler.Invocations);

        point.UnadviseError = RPC_E_WRONG_THREAD;
        Assert.AreEqual(RPC_E_WRONG_THREAD, connection.Disconnect());
        Assert.IsTrue(connection.IsConnected);
        Fire(source, ShapeEventsIid, 1, 3, 4);
        Assert.AreEqual(1, handler.Invocations);

        Assert.AreEqual(0, connection.Disconnect());
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AConnectionPointOfAManagedSourceIsNotReleased()
    {
        // A source that is a managed object returns its connection point directly, here a unique object that it keeps: the
        // connection does not release it.
        var inner = new ConnectionPoint(ShapeEventsIid);
        var wrappers = new StrategyBasedComWrappers();
        nint unknown = wrappers.GetOrCreateComInterfaceForObject(inner, CreateComInterfaceFlags.None);
        var point = (IConnectionPoint)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        Marshal.Release(unknown);

        var connection = EventConnection.AdviseDispatch(new ForwardingContainer(point), ShapeEventsIid, new IgnoringHandler());
        Assert.HasCount(1, inner.Sinks);
        Assert.AreEqual(0, connection.Disconnect());
        Assert.IsEmpty(inner.Sinks);

        Assert.AreEqual(0, point.GetConnectionInterface(out Guid iid));
        Assert.AreEqual(ShapeEventsIid, iid);
        ComMarshalling.Release(point);
    }

    private sealed class CountingHandler : IDispatchEventHandler
    {
        public int Invocations { get; private set; }

        public bool Invoke(int dispId, EventArguments arguments)
        {
            Invocations++;
            return true;
        }
    }

    [GeneratedComClass]
    internal sealed partial class ForwardingContainer(IConnectionPoint point) : IConnectionPointContainer
    {
        public int EnumConnectionPoints(out nint ppEnum)
        {
            ppEnum = 0;
            return unchecked((int)0x80004001);
        }

        public int FindConnectionPoint(in Guid riid, out IConnectionPoint? ppCP)
        {
            ppCP = point;
            return 0;
        }
    }

    [TestMethod]
    public void AConnectionReportsTheErrorsOfTheSource()
    {
        // Only the answers that say that the source does not raise the events are an InvalidOperationException: another
        // error, e.g. of a server that has ended or is busy, which the caller can retry, throws the exception of its
        // HRESULT, whether the source fails QueryInterface or FindConnectionPoint.
        const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
        const int E_NOINTERFACE = unchecked((int)0x80004002);
        const int E_NOTIMPL = unchecked((int)0x80004001);
        const int CONNECT_E_NOCONNECTION = unchecked((int)0x80040200);

        object disconnected = FailingComObject.Create(RPC_E_DISCONNECTED);
        Assert.AreEqual(
            RPC_E_DISCONNECTED,
            Assert.ThrowsExactly<COMException>(() => EventConnection.AdviseDispatch(disconnected, ShapeEventsIid, new IgnoringHandler())).HResult);
        ComMarshalling.Release(disconnected);

        object plain = FailingComObject.Create(E_NOINTERFACE);
        Assert.ThrowsExactly<InvalidOperationException>(() => EventConnection.AdviseDispatch(plain, ShapeEventsIid, new IgnoringHandler()));
        ComMarshalling.Release(plain);

        Assert.AreEqual(
            RPC_E_DISCONNECTED,
            Assert.ThrowsExactly<COMException>(
                () => EventConnection.AdviseDispatch(new ResultContainer(RPC_E_DISCONNECTED), ShapeEventsIid, new IgnoringHandler())).HResult);
        foreach (int result in new[] { CONNECT_E_NOCONNECTION, E_NOINTERFACE, E_NOTIMPL, 0 })
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => EventConnection.AdviseDispatch(new ResultContainer(result), ShapeEventsIid, new IgnoringHandler()));
        }
    }

    // A container that returns no connection point, with the specified result of FindConnectionPoint.
    [GeneratedComClass]
    internal sealed partial class ResultContainer(int result) : IConnectionPointContainer
    {
        public int EnumConnectionPoints(out nint ppEnum)
        {
            ppEnum = 0;
            return unchecked((int)0x80004001);
        }

        public int FindConnectionPoint(in Guid riid, out IConnectionPoint? ppCP)
        {
            ppCP = null;
            return result;
        }
    }

    [TestMethod]
    public void DisconnectDoesNotWaitForAnotherCall()
    {
        const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);
        const int S_FALSE = 1;
        var source = new EventSource(provideClassInfo: true);
        object shape = CreateProxy(source);
        var point = source.Points[ShapeEventsIid];
        var connection = EventConnection.AdviseDispatch(shape, ShapeEventsIid, new IgnoringHandler());

        // A call from the wrong thread is held while it fails.
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        point.UnadviseError = RPC_E_WRONG_THREAD;
        point.Unadvising = () =>
        {
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
        };
        var wrongThread = Task.Run(connection.Disconnect);
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));

            // The other call neither waits nor takes the sink for disconnected, which it is not.
            Assert.AreEqual(S_FALSE, connection.Disconnect());
        }
        finally
        {
            resume.Set();
        }

        Assert.IsTrue(wrongThread.Wait(Timeout));
        Assert.AreEqual(RPC_E_WRONG_THREAD, wrongThread.Result);
        Assert.AreEqual(1, source.Connections);

        Assert.AreEqual(0, connection.Disconnect());
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void DisconnectDuringACallOfTheSourceDoesNotDeadlock()
    {
        // The source calls out while it disconnects the sink, e.g. it raises an event whose handler disposes the connection
        // on another thread and waits for it.
        const int S_FALSE = 1;
        var source = new EventSource(provideClassInfo: true);
        object shape = CreateProxy(source);
        var connection = EventConnection.AdviseDispatch(shape, ShapeEventsIid, new IgnoringHandler());

        int? nested = null;
        source.Points[ShapeEventsIid].Unadvising = () =>
        {
            var other = Task.Run(connection.Disconnect);
            nested = other.Wait(Timeout) ? other.Result : null;
        };

        Assert.AreEqual(0, connection.Disconnect());
        Assert.AreEqual(S_FALSE, nested);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AGatedSinkPassesNoEventsOnceItsConnectionIsDisconnected()
    {
        // The gate is closed first, so the handler gets no more events also when the source cannot be told at the moment.
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        var source = new EventSource(provideClassInfo: true);
        GatedSink? sink = null;
        var connection = EventConnection.Advise(source, ShapeEventsIid, gate => sink = new GatedSink(gate));
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(1, sink!.Invocations);

        source.Points[ShapeEventsIid].UnadviseError = RPC_E_CALL_REJECTED;
        Assert.AreEqual(RPC_E_CALL_REJECTED, connection.Disconnect());
        Assert.IsTrue(connection.IsConnected);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(1, sink.Invocations);

        Assert.AreEqual(0, connection.Disconnect());
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void EachConnectionHasAGateOfItsOwn()
    {
        var source = new EventSource(provideClassInfo: true);
        GatedSink? first = null;
        GatedSink? second = null;
        var firstConnection = EventConnection.Advise(source, ShapeEventsIid, gate => first = new GatedSink(gate));
        using var secondConnection = EventConnection.Advise(source, ShapeEventsIid, gate => second = new GatedSink(gate));

        firstConnection.Dispose();
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(0, first!.Invocations);
        Assert.AreEqual(1, second!.Invocations);
    }

    [TestMethod]
    public void ASinkThatWrapsAnotherObjectIsConnectedItself()
    {
        // The connection point asks the sink for the event interface, which the object that it wraps does not implement.
        var source = new EventSource(provideClassInfo: true);
        var sink = new ProvidingSink(ComActivator.CreateInstance<IDispatch>("Scripting.Dictionary"));
        using var connection = EventConnection.Advise(source, ShapeEventsIid, sink);

        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(1, sink.Invocations);
    }

    [TestMethod]
    public void TheGateOfAFailedConnectionIsClosed()
    {
        // The source may keep the sink although Advise failed: its gate is closed. A new attempt gets a new sink and gate.
        var source = new EventSource(provideClassInfo: true);
        EventGate? failed = null;
        source.Points[ShapeEventsIid].AdviseError = unchecked((int)0x80010001);
        Assert.ThrowsExactly<COMException>(() => EventConnection.Advise(source, ShapeEventsIid, gate => new GatedSink(failed = gate)));
        Assert.IsTrue(failed!.IsClosed);

        EventGate? connected = null;
        var connection = EventConnection.Advise(source, ShapeEventsIid, gate => new GatedSink(connected = gate));
        Assert.IsFalse(connected!.IsClosed);
        Assert.AreEqual(1, source.Connections);

        connection.Dispose();
        Assert.IsTrue(connected.IsClosed);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public unsafe void InvokeHandlerRejectsANullHandlerBeforeItEntersTheGate()
    {
        // The handler would be called before the result is initialized, which a completion that fails in Exit would then
        // dispose: the handler is checked first, also when the gate is closed, and no event is counted.
        var source = new EventSource(provideClassInfo: true);
        EventGate? gate = null;
        var connection = EventConnection.Advise(source, ShapeEventsIid, g => new GatedSink(gate = g));
        DispParams parameters = default;
        ComVariant result = default;
        DispParams* parametersPointer = &parameters;
        ComVariant* resultPointer = &result;

        Assert.ThrowsExactly<ArgumentNullException>(() => DispatchEventSink.InvokeHandler(gate!, null!, 1, parametersPointer, resultPointer, null));
        bool completed = false;
        connection.DisposeWithCompletion(() => completed = true);
        Assert.IsTrue(completed, "No event runs.");

        Assert.ThrowsExactly<ArgumentNullException>(() => DispatchEventSink.InvokeHandler(gate!, null!, 1, parametersPointer, resultPointer, null));
    }

    [TestMethod]
    public void DisposeWithCompletionRejectsANullCompletionBeforeItDisconnects()
    {
        // An argument that is wrong changes nothing, like the subjects of EventSubject.Complete: the sink stays connected,
        // and its gate open, until it is disposed.
        var source = new EventSource(provideClassInfo: true);
        EventGate? gate = null;
        var connection = EventConnection.Advise(source, ShapeEventsIid, g => new GatedSink(gate = g));

        var exception = Assert.ThrowsExactly<ArgumentNullException>(() => connection.DisposeWithCompletion(null!));
        Assert.AreEqual("completion", exception.ParamName);
        Assert.IsTrue(connection.IsConnected);
        Assert.IsFalse(gate!.IsClosed);

        connection.Dispose();
        Assert.IsFalse(connection.IsConnected);
        Assert.IsTrue(gate.IsClosed);
    }

    [TestMethod]
    public void TheGateCountsTheRunningEvents()
    {
        // An event is running when the connection is disposed: no new event enters the gate, and DisposeAsync completes when
        // the running one returns. The source is busy, so that it keeps calling the sink.
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        var source = new EventSource(provideClassInfo: true);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        GatedSink? sink = null;
        var connection = EventConnection.Advise(source, ShapeEventsIid, gate => sink = new GatedSink(gate, () =>
        {
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
        }));

        var running = Task.Run(() => Fire(source, ShapeEventsIid, 1, 1, 2));
        Task disposing;
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            source.Points[ShapeEventsIid].UnadviseError = RPC_E_CALL_REJECTED;
            disposing = connection.DisposeAsync().AsTask();
            Assert.IsFalse(disposing.IsCompleted);

            Fire(source, ShapeEventsIid, 1, 3, 4);
            Assert.AreEqual(1, sink!.Invocations);
        }
        finally
        {
            resume.Set();
        }

        // The busy source left the sink connected, which DisposeAsync reports once the running handler has returned.
        var aggregate = Assert.ThrowsExactly<AggregateException>(() => disposing.Wait(Timeout));
        Assert.AreEqual(RPC_E_CALL_REJECTED, Assert.IsInstanceOfType<COMException>(aggregate.InnerException).HResult);
        Assert.IsTrue(running.Wait(Timeout));
        Assert.AreEqual(0, connection.Disconnect());
    }

    [TestMethod]
    public void DisposeAsyncOfAnIdleConnectionCompletesAtOnce()
    {
        var source = new EventSource(provideClassInfo: true);
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());

        Assert.IsTrue(connection.DisposeAsync().IsCompletedSuccessfully);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void DisposeAsyncThrowsWhenTheSinkStaysConnected()
    {
        // A disconnection that fails with an error that another call can succeed after leaves the sink connected: unlike
        // Dispose, DisposeAsync reports it, and another call disconnects. An error that gives the connection up, and a
        // disconnection that another call is making at the moment, are no failures.
        const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);
        const int CONNECT_E_NOCONNECTION = unchecked((int)0x80040200);
        var source = new EventSource(provideClassInfo: true);
        var point = source.Points[ShapeEventsIid];
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());

        point.UnadviseError = RPC_E_WRONG_THREAD;
        var failed = connection.DisposeAsync();
        Assert.IsTrue(failed.IsFaulted);
        Assert.AreEqual(RPC_E_WRONG_THREAD, Assert.ThrowsExactly<COMException>(() => failed.GetAwaiter().GetResult()).HResult);
        Assert.IsTrue(connection.IsConnected);
        Assert.AreEqual(1, source.Connections);

        Assert.IsTrue(connection.DisposeAsync().IsCompletedSuccessfully);
        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(0, source.Connections);

        var abandoned = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        point.UnadviseError = CONNECT_E_NOCONNECTION;
        Assert.IsTrue(abandoned.DisposeAsync().IsCompletedSuccessfully);
        Assert.IsFalse(abandoned.IsConnected);

        var concurrent = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        Task? nested = null;
        point.Unadvising = () => nested = concurrent.DisposeAsync().AsTask();
        concurrent.Dispose();
        Assert.IsTrue(nested!.Wait(Timeout));
        Assert.IsFalse(concurrent.IsConnected);
    }

    [TestMethod]
    public void DisposeAsyncWaitsForAConcurrentDisconnection()
    {
        // DisposeAsync is called while another call is disconnecting the sink, here in its Unadvise: it waits for that call
        // without blocking, and reports its result like its own, an error that leaves the sink connected or the success. It
        // does not disconnect the sink again, also when it could: that would have to be done on the thread of its caller,
        // which may be blocked waiting for it.
        const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001);
        var source = new EventSource(provideClassInfo: true);
        var point = source.Points[ShapeEventsIid];
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());

        Task? waiting = null;
        point.UnadviseError = RPC_E_CALL_REJECTED;
        point.Unadvising = () => waiting = connection.DisposeAsync().AsTask();
        Assert.AreEqual(RPC_E_CALL_REJECTED, connection.Disconnect());
        var aggregate = Assert.ThrowsExactly<AggregateException>(() => waiting!.Wait(Timeout));
        Assert.AreEqual(RPC_E_CALL_REJECTED, Assert.IsInstanceOfType<COMException>(aggregate.InnerException).HResult);
        Assert.IsTrue(connection.IsConnected);

        point.Unadvising = () => waiting = connection.DisposeAsync().AsTask();
        Assert.AreEqual(0, connection.Disconnect());
        Assert.IsTrue(waiting!.Wait(Timeout));
        Assert.IsFalse(connection.IsConnected);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    [DataRow(0, DisplayName = "Disconnected")]
    [DataRow(unchecked((int)0x80010001), DisplayName = "RPC_E_CALL_REJECTED")]
    public void DisposeAsyncFailsOnlyWhenTheSinkIsStillConnectedOnceNoHandlerRuns(int otherResult)
    {
        // DisposeAsync fails on the wrong thread while a handler runs, and before the handler returns, another call
        // disconnects the sink, e.g. on the thread of the connection, or leaves it connected too: the task fails only in the
        // second case, with the error that it knows.
        const int RPC_E_WRONG_THREAD = unchecked((int)0x8001010E);
        var source = new EventSource(provideClassInfo: true);
        var point = source.Points[ShapeEventsIid];
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new BlockingHandler(entered, resume));
        var running = Task.Run(() => Fire(source, ShapeEventsIid, 1, 1, 2));
        Task disposing;
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            point.UnadviseError = RPC_E_WRONG_THREAD;
            disposing = connection.DisposeAsync().AsTask();
            Assert.IsFalse(disposing.IsCompleted);

            point.UnadviseError = otherResult;
            Assert.AreEqual(otherResult, connection.Disconnect());
        }
        finally
        {
            resume.Set();
        }

        Assert.IsTrue(running.Wait(Timeout));
        if (otherResult == 0)
        {
            Assert.IsTrue(disposing.Wait(Timeout));
            Assert.IsFalse(connection.IsConnected);
            Assert.AreEqual(0, source.Connections);
        }
        else
        {
            var aggregate = Assert.ThrowsExactly<AggregateException>(() => disposing.Wait(Timeout));
            Assert.AreEqual(RPC_E_WRONG_THREAD, Assert.IsInstanceOfType<COMException>(aggregate.InnerException).HResult);
            Assert.IsTrue(connection.IsConnected);
            Assert.AreEqual(0, connection.Disconnect());
        }
    }

    [TestMethod]
    [DataRow("returns")]
    [DataRow("throws")]
    [DataRow("throwsWithoutMessage")]
    public void TheExceptionOfACompletionReplacesTheResultOfTheHandler(string handlerResult)
    {
        // The last call to leave the closed gate runs the completion, whose exception replaces the result of the handler,
        // also an unknown member (DISP_E_MEMBERNOTFOUND) or an exception, as a finally block would throw it: also one whose
        // message cannot be read, which is reported without it rather than thrown.
        var source = new EventSource(provideClassInfo: true);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var handler = new BlockingHandler(entered, resume, () => handlerResult switch
        {
            "throws" => throw new InvalidOperationException("handler"),
            "throwsWithoutMessage" => throw new EventRuntimeTests.UnreadableException(),
            _ => false,
        });
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, handler);
        var running = Task.Run(() => Fire(source, ShapeEventsIid, 1, 1, 2));
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            connection.DisposeWithCompletion(() => throw new InvalidOperationException("completion"));
        }
        finally
        {
            resume.Set();
        }

        var aggregate = Assert.ThrowsExactly<AggregateException>(() => running.Wait(Timeout));
        Assert.AreEqual("completion", Assert.IsInstanceOfType<COMException>(aggregate.InnerException).Message);
    }

    [TestMethod]
    public void AnObjectOfTheApplicationRaisesTheEventsOfTheObjectThatItWraps()
    {
        // An object that is exposed to COM and does not raise events itself, e.g. a decorator, is connected through the COM
        // object that it wraps.
        var source = new EventSource(provideClassInfo: true);
        var handler = new CountingHandler();
        using var connection = EventConnection.AdviseDispatch(new ProviderObject(source), ShapeEventsIid, handler);

        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(1, handler.Invocations);
    }

    [TestMethod]
    public void AWrapperOfAnObjectOfTheApplicationRaisesTheEventsOfTheObjectThatItWraps()
    {
        // A late-bound or dynamic wrapper stands for an object that is exposed to COM with its rule, e.g. the one that a
        // dispinterface wrapper makes of a decorator that a call returns: it is connected through the COM object that the
        // decorator wraps, as the decorator itself.
        var source = new EventSource(provideClassInfo: true);
        var decorator = new DispatchingProvider(source);
        var handler = new CountingHandler();
        using (EventConnection.AdviseDispatch(new PlainWrapper(decorator), ShapeEventsIid, handler))
        {
            Assert.AreEqual(1, source.Connections);
            Fire(source, ShapeEventsIid, 1, 1, 2);
            Assert.AreEqual(1, handler.Invocations);
        }

        using (EventConnection.AdviseDispatch((object)ComDynamic.ToDynamic(decorator)!, ShapeEventsIid, handler))
        {
            Assert.AreEqual(1, source.Connections);
            Fire(source, ShapeEventsIid, 1, 1, 2);
            Assert.AreEqual(2, handler.Invocations);
        }

        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void ASourceThatStandsForNoObjectIsRejectedLikeNull()
    {
        // The Dispatch of the wrapper breaks the contract of IDispatchProvider: the wrapper stands for no COM object, like
        // null, rather than for a null pointer that would be asked for the interface, and is rejected like null, as by
        // ComDynamic.
        var exception = Assert.ThrowsExactly<ArgumentNullException>(
            () => EventConnection.AdviseDispatch(new PlainWrapper(null!), ShapeEventsIid, new IgnoringHandler()));
        Assert.AreEqual("source", exception.ParamName);
        StringAssert.Contains(exception.Message, "has no COM object");
    }

    [TestMethod]
    public void ASinkThatStandsForNoObjectIsRejectedLikeNull()
    {
        // Such a sink would be passed as a null pointer, which a connection point rejects, or keeps under a cookie that is
        // lost: it is rejected like null before a connection point is looked up, a DispatchWrapper as everywhere, and a
        // factory that creates one fails like one that creates none, which nothing is connected for either.
        var source = new EventSource(provideClassInfo: true);
        Assert.AreEqual("sink", Assert.ThrowsExactly<ArgumentNullException>(() => EventConnection.Advise(source, ShapeEventsIid, new UnknownWrapper(null))).ParamName);
        Assert.AreEqual("sink", Assert.ThrowsExactly<ArgumentNullException>(() => EventConnection.Advise(source, ShapeEventsIid, (object)new PlainWrapper(null!))).ParamName);
        Assert.AreEqual("sink", Assert.ThrowsExactly<ArgumentException>(() => EventConnection.Advise(source, ShapeEventsIid, new DispatchWrapper(null))).ParamName);
        Assert.ThrowsExactly<InvalidOperationException>(() => EventConnection.Advise(source, ShapeEventsIid, _ => new UnknownWrapper(null)));
        Assert.ThrowsExactly<InvalidOperationException>(() => EventConnection.Advise(source, ShapeEventsIid, _ => new PlainWrapper(null!)));
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AnUnknownWrapperIsTheSourceOfItsObject()
    {
        // The wrapper stands for its object wherever an object goes to COM, with the whole rule: an object that is exposed
        // to COM and does not raise events itself is connected through the COM object that it wraps. A null object is
        // rejected like null.
        var source = new EventSource(provideClassInfo: true);
        var handler = new CountingHandler();
        using (EventConnection.AdviseDispatch(new UnknownWrapper(new ProviderObject(source)), ShapeEventsIid, handler))
        {
            Assert.AreEqual(1, source.Connections);
            Fire(source, ShapeEventsIid, 1, 1, 2);
            Assert.AreEqual(1, handler.Invocations);
        }

        var exception = Assert.ThrowsExactly<ArgumentNullException>(
            () => EventConnection.AdviseDispatch(new UnknownWrapper(null), ShapeEventsIid, new IgnoringHandler()));
        Assert.AreEqual("source", exception.ParamName);
        StringAssert.Contains(exception.Message, "wraps no object");
    }

    [TestMethod]
    public void ASinkConnectsToAConnectionPointOfIDispatch()
    {
        // A source can raise the events of a generic connection point, whose interface is IDispatch itself: the sink of
        // AdviseDispatch is asked for IDispatch, which it implements, and does not answer it itself, which would ask it
        // again without end.
        var dispatchIid = new Guid("00020400-0000-0000-C000-000000000046");
        var source = new EventSource(provideClassInfo: true);
        source.Points[dispatchIid] = new(dispatchIid);
        var handler = new CountingHandler();
        using var connection = EventConnection.AdviseDispatch(source, dispatchIid, handler);

        Assert.AreEqual(1, source.Points[dispatchIid].Sinks.Count);
        Fire(source, dispatchIid, 1, 1, 2);
        Assert.AreEqual(1, handler.Invocations);
    }

    [TestMethod]
    public void DisposeWithCompletionRunsItAtOnceWhenNoHandlerRuns()
    {
        // No handler runs, so the completion runs before the method returns, on the calling thread and after the
        // disconnection, also for a later call; the exception of a completion is thrown to the caller then.
        var source = new EventSource(provideClassInfo: true);
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        int completedOn = 0;
        int connections = -1;

        connection.DisposeWithCompletion(() =>
        {
            completedOn = Environment.CurrentManagedThreadId;
            connections = source.Connections;
        });
        Assert.AreEqual(Environment.CurrentManagedThreadId, completedOn);
        Assert.AreEqual(0, connections);

        bool again = false;
        connection.DisposeWithCompletion(() => again = true);
        Assert.IsTrue(again);

        var other = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => other.DisposeWithCompletion(() => throw new InvalidOperationException("completion")));
        Assert.AreEqual("completion", exception.Message);
        Assert.IsFalse(other.IsConnected);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void DisposeWithCompletionRunsItOnTheThreadOfTheLastHandler()
    {
        // A handler runs on another thread when the connection is disposed: the completion runs on that thread when the
        // handler returns, before the event call returns to the source, and DisposeAsync completes after it. It needs no
        // synchronization context of the caller, whose context here never runs what is posted to it, like the one of a
        // thread that stops pumping messages.
        var source = new EventSource(provideClassInfo: true);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int handlerThread = 0;
        var connection = EventConnection.Advise(source, ShapeEventsIid, gate => new GatedSink(gate, () =>
        {
            handlerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
        }));

        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var running = Task.Run(() =>
        {
            Fire(source, ShapeEventsIid, 1, 1, 2);
            calls.Enqueue("Fired");
        });

        var context = new NonPumpingContext();
        var original = SynchronizationContext.Current;
        Task? disposing = null;
        int completedOn = 0;
        bool? idleBeforeCompletion = null;
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            SynchronizationContext.SetSynchronizationContext(context);
            connection.DisposeWithCompletion(() =>
            {
                completedOn = Environment.CurrentManagedThreadId;
                idleBeforeCompletion = disposing!.IsCompleted;
                calls.Enqueue("Completed");
            });

            disposing = connection.DisposeAsync().AsTask();
            Assert.IsTrue(calls.IsEmpty);
            Assert.IsFalse(disposing.IsCompleted);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
            resume.Set();
        }

        Assert.IsTrue(running.Wait(Timeout));
        Assert.IsTrue(disposing.Wait(Timeout));
        CollectionAssert.AreEqual(new[] { "Completed", "Fired" }, calls.ToArray());
        Assert.AreEqual(handlerThread, completedOn);
        Assert.IsFalse(idleBeforeCompletion);
        Assert.AreEqual(0, context.Posts);
    }

    [TestMethod]
    public void DisposeWithCompletionInAHandlerRunsItWhenTheHandlerReturns()
    {
        // A handler disposes its own connection: the completion runs once the handler has returned, on its thread and in the
        // same event call.
        var source = new EventSource(provideClassInfo: true);
        var calls = new List<string>();
        EventConnection? connection = null;
        connection = EventConnection.Advise(source, ShapeEventsIid, gate => new GatedSink(gate, () =>
        {
            connection!.DisposeWithCompletion(() => calls.Add("Completed"));
            calls.Add("Handled");
        }));

        Fire(source, ShapeEventsIid, 1, 1, 2);
        calls.Add("Fired");
        CollectionAssert.AreEqual(new[] { "Handled", "Completed", "Fired" }, calls);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void AnExceptionOfTheCompletionFailsTheEventThatRunsIt()
    {
        // The completion runs in the event call of the last running handler: its exception fails that call like an exception
        // of the handler, as DISP_E_EXCEPTION with the error information, and the connection still completes.
        var source = new EventSource(provideClassInfo: true);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new BlockingHandler(entered, resume));
        var running = Task.Run(() => Fire(source, ShapeEventsIid, 1, 1, 2));
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            connection.DisposeWithCompletion(() => throw new InvalidOperationException("completion"));
        }
        finally
        {
            resume.Set();
        }

        var aggregate = Assert.ThrowsExactly<AggregateException>(() => running.Wait(Timeout));
        var exception = Assert.IsInstanceOfType<COMException>(aggregate.InnerException);
        Assert.AreEqual("completion", exception.Message);
        Assert.AreEqual(new InvalidOperationException().HResult, exception.HResult);
        Assert.IsTrue(connection.DisposeAsync().IsCompletedSuccessfully);
        Assert.AreEqual(0, source.Connections);
    }

    [TestMethod]
    public void ACompletionThatIsAddedWhileAnotherRunsRunsAfterIt()
    {
        // The completions run one at a time, in the order of the calls: one that is added while another runs on the thread
        // of the last handler is left to that thread, which runs it afterwards, without the caller waiting for it.
        // DisposeAsync completes after both.
        var source = new EventSource(provideClassInfo: true);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var completing = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new BlockingHandler(entered, resume));
        var calls = new System.Collections.Concurrent.ConcurrentQueue<(string Name, int Thread)>();
        var running = Task.Run(() => Fire(source, ShapeEventsIid, 1, 1, 2));
        Task? disposing = null;
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            connection.DisposeWithCompletion(() =>
            {
                completing.Set();
                Assert.IsTrue(finish.Wait(Timeout));
                calls.Enqueue(("First", Environment.CurrentManagedThreadId));
            });

            resume.Set();
            Assert.IsTrue(completing.Wait(Timeout));
            connection.DisposeWithCompletion(() => calls.Enqueue(("Second", Environment.CurrentManagedThreadId)));
            disposing = connection.DisposeAsync().AsTask();
            Assert.IsTrue(calls.IsEmpty);
            Assert.IsFalse(disposing.IsCompleted);
        }
        finally
        {
            resume.Set();
            finish.Set();
        }

        Assert.IsTrue(running.Wait(Timeout));
        Assert.IsTrue(disposing.Wait(Timeout));
        CollectionAssert.AreEqual(new[] { "First", "Second" }, calls.Select(static c => c.Name).ToArray());
        Assert.AreEqual(calls.First().Thread, calls.Last().Thread);
        Assert.AreNotEqual(Environment.CurrentManagedThreadId, calls.Last().Thread);
    }

    [TestMethod]
    public void AnExceptionOfACompletionDoesNotStopThoseThatRunAfterIt()
    {
        // A completion that was added while another runs, by a call that has returned, still runs after the exception of
        // the other, which then fails the event call that runs them, as it is; a completion that is added afterwards runs
        // at once.
        var source = new EventSource(provideClassInfo: true);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var completing = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new BlockingHandler(entered, resume));
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var running = Task.Run(() => Fire(source, ShapeEventsIid, 1, 1, 2));
        try
        {
            Assert.IsTrue(entered.Wait(Timeout));
            connection.DisposeWithCompletion(() =>
            {
                completing.Set();
                Assert.IsTrue(finish.Wait(Timeout));
                throw new InvalidOperationException("first");
            });

            resume.Set();
            Assert.IsTrue(completing.Wait(Timeout));
            connection.DisposeWithCompletion(() => calls.Enqueue("Second"));
        }
        finally
        {
            resume.Set();
            finish.Set();
        }

        var aggregate = Assert.ThrowsExactly<AggregateException>(() => running.Wait(Timeout));
        Assert.AreEqual("first", Assert.IsInstanceOfType<COMException>(aggregate.InnerException).Message);
        CollectionAssert.AreEqual(new[] { "Second" }, calls.ToArray());

        connection.DisposeWithCompletion(() => calls.Enqueue("Third"));
        CollectionAssert.AreEqual(new[] { "Second", "Third" }, calls.ToArray());
    }

    [TestMethod]
    public void TheExceptionsOfSeveralCompletionsAreThrownTogether()
    {
        // No handler runs, so the completions run at once: those that a completion adds run after it, also when it fails,
        // and the exceptions are thrown together once all of them ran, in the order of the calls.
        var source = new EventSource(provideClassInfo: true);
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        var calls = new List<string>();
        var exception = Assert.ThrowsExactly<AggregateException>(() => connection.DisposeWithCompletion(() =>
        {
            connection.DisposeWithCompletion(() =>
            {
                calls.Add("Second");
                throw new InvalidOperationException("second");
            });
            connection.DisposeWithCompletion(() => calls.Add("Third"));
            calls.Add("First");
            throw new InvalidOperationException("first");
        }));

        CollectionAssert.AreEqual(new[] { "First", "Second", "Third" }, calls);
        CollectionAssert.AreEqual(new[] { "first", "second" }, exception.InnerExceptions.Select(static e => e.Message).ToArray());

        // The run has ended, so a later completion runs at once.
        connection.DisposeWithCompletion(() => calls.Add("Fourth"));
        Assert.AreEqual("Fourth", calls[^1]);
    }

    [TestMethod]
    public void ACompletionThatAddsAnotherRunsItAfterItself()
    {
        // A completion that disposes the connection again adds a completion, which runs once it has returned, not within it.
        var source = new EventSource(provideClassInfo: true);
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        var calls = new List<string>();
        connection.DisposeWithCompletion(() =>
        {
            calls.Add("First");
            connection.DisposeWithCompletion(() => calls.Add("Second"));
            calls.Add("First returns");
        });

        CollectionAssert.AreEqual(new[] { "First", "First returns", "Second" }, calls);
    }

    [TestMethod]
    public void CompletionsThatAreAddedWhileTheLastHandlerReturnsRunOnceEach()
    {
        // Several threads add completions while the last handler returns: each of them runs exactly once, after the handler,
        // on the thread of the handler or on the one that adds it, whichever comes last.
        const int Count = 8;
        for (int iteration = 0; iteration < 30; iteration++)
        {
            var source = new EventSource(provideClassInfo: true);
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            bool returned = false;
            var connection = EventConnection.Advise(source, ShapeEventsIid, gate => new GatedSink(gate, () =>
            {
                entered.Set();
                Assert.IsTrue(resume.Wait(Timeout));
                Volatile.Write(ref returned, true);
            }));

            var running = RunOnOwnThread(() => Fire(source, ShapeEventsIid, 1, 1, 2));
            Assert.IsTrue(entered.Wait(Timeout));
            connection.Dispose();

            var runs = new int[Count];
            int early = 0;
            using var start = new Barrier(Count + 1);
            var adding = Enumerable.Range(0, Count).Select(i => RunOnOwnThread(() =>
            {
                Assert.IsTrue(start.SignalAndWait(Timeout));
                connection.DisposeWithCompletion(() =>
                {
                    if (!Volatile.Read(ref returned))
                    {
                        Interlocked.Increment(ref early);
                    }

                    Interlocked.Increment(ref runs[i]);
                });
            })).ToArray();

            Assert.IsTrue(start.SignalAndWait(Timeout));
            resume.Set();
            Assert.IsTrue(Task.WaitAll(adding, Timeout));
            Assert.IsTrue(running.Wait(Timeout));
            CollectionAssert.AreEqual(Enumerable.Repeat(1, Count).ToArray(), runs);
            Assert.AreEqual(0, early);
            Assert.AreEqual(0, source.Connections);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AnExceptionOfAManagedConnectionPointIsItsHResult(bool transient)
    {
        // The source is the managed object itself, whose connection point is called directly: its exception is returned as
        // its HRESULT, as a call through COM would return it. A transient one leaves the sink connected.
        var source = new EventSource(provideClassInfo: true);
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        Exception exception = transient ? new COMException("Busy.", unchecked((int)0x80010001)) : new InvalidOperationException("Broken.");

        source.Points[ShapeEventsIid].Unadvising = () => throw exception;
        Assert.AreEqual(exception.HResult, connection.Disconnect());
        Assert.AreEqual(transient, connection.IsConnected);
        if (!transient)
        {
            // A connection that has been given up returns the error that ended it.
            Assert.AreEqual(exception.HResult, connection.Disconnect());
        }

        // Dispose does not throw either.
        var other = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());
        source.Points[ShapeEventsIid].Unadvising = () => throw exception;
        other.Dispose();
        Assert.AreEqual(transient, other.IsConnected);

        // A transient error is retried; any other gives the connection up, and the source keeps the sink.
        connection.Dispose();
        other.Dispose();
        Assert.AreEqual(transient ? 0 : 2, source.Connections);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void AnExceptionWhoseHResultIsNoErrorIsAFailure(int hresult)
    {
        // S_OK and S_FALSE would read as a disconnection, or as another call that is disconnecting.
        const int E_FAIL = unchecked((int)0x80004005);
        var source = new EventSource(provideClassInfo: true);
        var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, new IgnoringHandler());

        source.Points[ShapeEventsIid].Unadvising = () => throw new COMException("Broken.", hresult);
        Assert.AreEqual(E_FAIL, connection.Disconnect());
        Assert.IsFalse(connection.IsConnected);
    }

    [TestMethod]
    public void ExitWithoutTryEnterThrows()
    {
        // A call of Exit without an entered event would corrupt the gate: it is rejected, and the gate keeps working.
        var source = new EventSource(provideClassInfo: true);
        EventGate? gate = null;
        GatedSink? sink = null;
        var connection = EventConnection.Advise(source, ShapeEventsIid, g => sink = new GatedSink(gate = g));

        Assert.ThrowsExactly<InvalidOperationException>(gate!.Exit);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        Assert.AreEqual(1, sink!.Invocations);

        var disposing = connection.DisposeAsync();
        Assert.ThrowsExactly<InvalidOperationException>(gate.Exit);
        Assert.IsTrue(gate.IsClosed);
        Assert.IsTrue(disposing.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void ARemovedHandlerIsNotCalledByARunningEvent()
    {
        var source = new EventSource(provideClassInfo: true);
        dynamic shape = CreateProxy(source);
        var calls = new List<string>();
        var second = (Action<int, int>)((_, _) => calls.Add("second"));
        var first = (Action<int, int>)((_, _) =>
        {
            calls.Add("first");
            shape.Moved -= second;
        });
        shape.Moved += first;
        shape.Moved += second;

        Fire(source, ShapeEventsIid, 1, 1, 2);
        CollectionAssert.AreEqual(new[] { "first" }, calls);

        ComDynamic.RemoveEventHandlers(shape);
        Assert.AreEqual(0, source.Connections);
    }

    // A sink of the dispinterface that counts its events while the gate of its connection is open. The connection point asks
    // the sink for the event interface, which is its IDispatch.
    [GeneratedComClass]
    internal sealed unsafe partial class GatedSink(EventGate gate, Action? onEvent = null) : IDispatch, ICustomQueryInterface
    {
        private const int E_NOTIMPL = unchecked((int)0x80004001);
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

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

        public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr)
        {
            if (pVarResult is not null)
            {
                *pVarResult = default;
            }

            if (!gate.TryEnter())
            {
                return 0;
            }

            try
            {
                Interlocked.Increment(ref _invocations);
                onEvent?.Invoke();
                return 0;
            }
            finally
            {
                gate.Exit();
            }
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
        {
            if (iid == ShapeEventsIid)
            {
                ppv = (nint)ComInterfaceMarshaller<IDispatch>.ConvertToUnmanaged(this);
                return CustomQueryInterfaceResult.Handled;
            }

            ppv = 0;
            return CustomQueryInterfaceResult.NotHandled;
        }
    }

    // A sink of the dispinterface without a gate that also wraps another object, which must not be connected instead.
    [GeneratedComClass]
    internal sealed unsafe partial class ProvidingSink(IDispatch wrapped) : IDispatch, IDispatchProvider, ICustomQueryInterface
    {
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        public IDispatch Dispatch => wrapped;

        public int GetTypeInfoCount(out uint pctinfo)
        {
            pctinfo = 0;
            return 0;
        }

        public int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
        {
            ppTInfo = 0;
            return unchecked((int)0x80004001);
        }

        public int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId) => unchecked((int)0x80004001);

        public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr)
        {
            if (pVarResult is not null)
            {
                *pVarResult = default;
            }

            Interlocked.Increment(ref _invocations);
            return 0;
        }

        public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
        {
            if (iid == ShapeEventsIid)
            {
                ppv = (nint)ComInterfaceMarshaller<IDispatch>.ConvertToUnmanaged(this);
                return CustomQueryInterfaceResult.Handled;
            }

            ppv = 0;
            return CustomQueryInterfaceResult.NotHandled;
        }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Runs an action that blocks until other ones run too on a thread of its own: the thread pool adds threads for blocked
    // work items slowly, and more slowly still while the test classes that run in parallel keep it busy, so on a small CI
    // runner the actions could wait for each other beyond the timeout.
    private static Task RunOnOwnThread(Action action) =>
        Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed class IgnoringHandler : IDispatchEventHandler
    {
        public bool Invoke(int dispId, EventArguments arguments) => true;
    }

    // A handler that signals when an event has entered it, and returns when it is resumed, the result of a function if any.
    private sealed class BlockingHandler(ManualResetEventSlim entered, ManualResetEventSlim resume, Func<bool>? result = null) : IDispatchEventHandler
    {
        public bool Invoke(int dispId, EventArguments arguments)
        {
            entered.Set();
            Assert.IsTrue(resume.Wait(Timeout));
            return result?.Invoke() ?? true;
        }
    }

    // A synchronization context that never runs what is posted to it, like the one of a thread that stops pumping messages.
    private sealed class NonPumpingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);

        public override void Send(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _posts);
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
    public void AddsHandlersThroughAnUnknownWrapper()
    {
        // The wrapper stands for its object as the source of events, as with EventConnection: the handler is one of the
        // object, which removes it.
        var source = new EventSource(provideClassInfo: true);
        object shape = CreateProxy(source);
        var calls = new List<string?>();
        Action handler = () => calls.Add("moved");

        ComDynamic.AddEventHandler(new UnknownWrapper(((IDispatchProvider)shape).Dispatch), "Moved", handler);
        Assert.AreEqual(1, source.Connections);
        Fire(source, ShapeEventsIid, 1, 1, 2);
        ComDynamic.RemoveEventHandler(shape, "Moved", handler);

        CollectionAssert.AreEqual(new[] { "moved" }, calls);
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
        /// <summary>Gets or sets the <c>HRESULT</c> with which the next call of <see cref="Advise"/> fails.</summary>
        public int AdviseError { get; set; }

        /// <summary>Gets or sets an action that the next call of <see cref="Advise"/> runs first, e.g. to hold the call.</summary>
        public Action? Advising { get; set; }

        public int Advise(nint pUnkSink, out uint pdwCookie)
        {
            pdwCookie = 0;
            if (Advising is { } advising)
            {
                Advising = null;
                advising();
            }

            if (AdviseError != 0)
            {
                int error = AdviseError;
                AdviseError = 0;
                return error;
            }

            int hr = Marshal.QueryInterface(pUnkSink, iid, out nint sink);
            if (hr < 0)
            {
                return hr;
            }

            pdwCookie = ++_cookie;
            Sinks.Add(pdwCookie, sink);
            if (Advised is { } advised)
            {
                Advised = null;
                try
                {
                    advised();
                }
                catch
                {
                    Sinks.Remove(pdwCookie);
                    Marshal.Release(sink);
                    pdwCookie = 0;
                    throw;
                }
            }

            return 0;
        }

        /// <summary>
        /// Gets or sets an action that the next successful call of <see cref="Advise"/> runs after it added the sink, e.g.
        /// to raise an event while the sink is being connected. If it throws, the sink is removed again and the call fails.
        /// </summary>
        public Action? Advised { get; set; }

        /// <summary>Gets or sets the <c>HRESULT</c> with which the next call of <see cref="Unadvise"/> fails.</summary>
        public int UnadviseError { get; set; }

        /// <summary>Gets or sets an action that the next call of <see cref="Unadvise"/> runs first, e.g. to hold the call.</summary>
        public Action? Unadvising { get; set; }

        /// <summary>Gets or sets the <c>HRESULT</c> with which every call of <see cref="Unadvise"/> fails while it is set.</summary>
        public int PersistentUnadviseError { get; set; }

        /// <summary>Gets the number of the calls of <see cref="Unadvise"/>.</summary>
        public int UnadviseCalls { get; private set; }

        public int Unadvise(uint dwCookie)
        {
            UnadviseCalls++;
            if (Unadvising is { } unadvising)
            {
                Unadvising = null;
                unadvising();
            }

            if (UnadviseError != 0)
            {
                int error = UnadviseError;
                UnadviseError = 0;
                return error;
            }

            if (PersistentUnadviseError != 0)
            {
                return PersistentUnadviseError;
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
}
