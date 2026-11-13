using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// The events, which COM objects raise through <c>IDispatch::Invoke</c> of a sink, and the <c>dynamic</c> objects.
/// </summary>
public sealed unsafe partial class MemoryLeakTests
{
    // _ShapeEvents of the test type library, which DynamicEventTests.EventSource raises.
    private static readonly Guid ShapeEventsIid = new("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1015");

    private delegate string DescribeHandler(object? value, out int length);

    [TestMethod]
    public void EventArgumentsFreeTheValuesTheyReplace()
    {
        object native = CreateDictionary();
        var managed = new Server();

        LeakDetector.AssertNoLeaks(
            () =>
            {
                // The variables of the source of the event, which owns their values before and after the event.
                nint text = Marshal.StringToBSTR("old");
                nint array = SafeArrayMarshaller<string>.ConvertToUnmanaged(["old"]);
                nint dispatch = (nint)ComInterfaceMarshaller<IDispatch>.ConvertToUnmanaged((IDispatch)native);
                nint unknown = 0;
                ComVariant any = VariantMarshaller.ConvertToUnmanaged(new[] { "old" });
                ComVariant result = default;
                var variants = stackalloc ComVariant[6];
                variants[5] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_BSTR, (nint)(&text));
                variants[4] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_ARRAY | VarEnum.VT_BSTR, (nint)(&array));
                variants[3] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_DISPATCH, (nint)(&dispatch));
                variants[2] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_UNKNOWN, (nint)(&unknown));
                variants[1] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_VARIANT, (nint)(&any));
                variants[0] = VariantMarshaller.ConvertToUnmanaged(new[] { "by value" });
                var parameters = new DispParams { rgvarg = variants, cArgs = 6 };
                try
                {
                    var arguments = new EventArguments(&parameters, &result);
                    Assert.AreEqual("old", arguments.Get<string?>(0));
                    CollectionAssert.AreEqual(new[] { "old" }, arguments.GetArray<string>(1));
                    Assert.AreSame(native, arguments.Get<object?>(2));
                    CollectionAssert.AreEqual(new[] { "old" }, arguments.GetArray<string>(4));
                    CollectionAssert.AreEqual(new[] { "by value" }, arguments.GetArray<string>(5));

                    arguments.Set(0, "new");
                    arguments.Set<string?>(0, null);
                    arguments.Set(0, "last");
                    arguments.Set<string?[]?>(1, ["new", null]);
                    arguments.Set<string?[]?>(1, null);
                    arguments.Set<string?[]?>(1, ["last"]);
                    arguments.Set<object?>(2, managed);
                    arguments.Set<object?>(2, null);
                    arguments.Set<object?>(2, native);
                    arguments.Set<object?>(3, native);
                    arguments.Set<object?>(3, managed);
                    arguments.Set<object?>(4, "new");
                    arguments.Set<object?>(4, new object?[] { "a", native });
                    arguments.Set<object?>(4, managed);
                    arguments.Set(5, "ignored");

                    // A value that cannot be converted to the type of the argument is freed, and the argument keeps its value.
                    try
                    {
                        arguments.Set(2, "not an object");
                        Assert.Fail("A string is not an object.");
                    }
                    catch (COMException)
                    {
                    }

                    Assert.AreEqual("last", arguments.Get<string?>(0));
                    CollectionAssert.AreEqual(new[] { "last" }, arguments.GetArray<string>(1));
                    Assert.AreSame(native, arguments.Get<object?>(2));
                    Assert.AreSame(managed, arguments.Get<object?>(3));
                    Assert.AreSame(managed, arguments.Get<object?>(4));

                    arguments.SetResult("first");
                    arguments.SetResult(new[] { "second" });
                    arguments.SetResult<object?>(native);
                }
                finally
                {
                    Marshal.FreeBSTR(text);
                    SafeArrayMarshaller<string>.Free(array);
                    Release(dispatch);
                    Release(unknown);
                    any.Dispose();
                    result.Dispose();
                    variants[0].Dispose();
                }
            },
            native,
            managed);

        static void Release(nint pointer)
        {
            if (pointer != 0)
            {
                Marshal.Release(pointer);
            }
        }
    }

    [TestMethod]
    public void HandlersThatFailFreeTheirResults()
    {
        LeakDetector.AssertNoLeaks(static () =>
        {
            var handler = new EventHandler();
            var parameters = new DispParams();
            ComVariant result = default;
            ExcepInfo excepInfo = default;

            // A failed invocation has no result; the caller frees the strings of the exception.
            Assert.AreEqual(DISP_E_EXCEPTION, DispatchEventSink.InvokeHandler(handler, EventHandler.FailId, &parameters, &result, &excepInfo));
            Assert.AreEqual(VarEnum.VT_EMPTY, result.VarType);
            Assert.AreEqual("failed", Marshal.PtrToStringBSTR(excepInfo.bstrDescription));
            Marshal.FreeBSTR(excepInfo.bstrSource);
            Marshal.FreeBSTR(excepInfo.bstrDescription);

            Assert.AreEqual(new InvalidOperationException().HResult, DispatchEventSink.InvokeHandler(handler, EventHandler.FailId, &parameters, &result, null));
            Assert.AreEqual(VarEnum.VT_EMPTY, result.VarType);
        });
    }

    [TestMethod]
    public void EventConnectionsReleaseTheSinkAndTheSource()
    {
        object native = CreateDictionary();
        var source = new DynamicEventTests.EventSource(provideClassInfo: true);
        var handler = new EventHandler { Replacement = native };

        LeakDetector.AssertNoLeaks(
            () =>
            {
                using (EventConnection.AdviseDispatch(source, ShapeEventsIid, handler))
                {
                    // The source calls the sink through its COM interface, like a native one.
                    IDispatch sink = GetSinks(source).Single();
                    DispatchObject.Invoke(sink, EventHandler.ReadId, DispatchFlags.Method, CreateValues(native, new Server()));

                    object?[] args = CreateArguments();
                    Assert.HasCount(1, (string[])DispatchObject.InvokeByRef(sink, EventHandler.ReplaceId, DispatchFlags.Method, args, [true, true, true, true, true, false])!);
                    AssertReplaced(args);

                    // The arrays are first passed as references to VARIANTs, which the sink accepts.
                    args = CreateArguments();
                    VarEnum[] types = [VarEnum.VT_BSTR, VarEnum.VT_ARRAY | VarEnum.VT_BSTR, VarEnum.VT_DISPATCH, VarEnum.VT_I4, VarEnum.VT_BSTR, VarEnum.VT_EMPTY];
                    DispatchObject.InvokeByRef(sink, EventHandler.ReplaceId, DispatchFlags.Method, args, types);
                    AssertReplaced(args);

                    // References of the types of the values, e.g. to a SAFEARRAY.
                    args = CreateArguments();
                    var kinds = new DispatchArgumentKind[args.Length];
                    kinds.AsSpan().Fill(DispatchArgumentKind.Typed);
                    kinds[^1] = DispatchArgumentKind.Value;
                    ComDispatch.Invoke(sink, EventHandler.ReplaceId, DispatchFlags.Method, args, output: args, byRef: kinds);
                    AssertReplaced(args);

                    var exception = Assert.ThrowsExactly<COMException>(() => DispatchObject.Invoke(sink, EventHandler.FailId, DispatchFlags.Method, CreateArguments()));
                    Assert.AreEqual("failed", exception.Message);
                    Assert.ThrowsExactly<COMException>(() => DispatchObject.Invoke(sink, 100, DispatchFlags.Method, CreateArguments()));
                }

                Assert.AreEqual(0, source.Connections);
            },
            native,
            source);

        Assert.AreEqual(4 * 6, handler.Invocations, "The detector runs the action four times.");

        object?[] CreateArguments() => ["text", new[] { "a", "b" }, source, 5, null, new[] { "by value" }];

        void AssertReplaced(object?[] args)
        {
            Assert.AreEqual("new", args[0]);
            CollectionAssert.AreEqual(new[] { "new", "array" }, (string?[])args[1]!);
            Assert.AreSame(native, args[2]);
            Assert.AreEqual(6, args[3]);
            Assert.AreEqual("new", args[4]);
            CollectionAssert.AreEqual(new[] { "by value" }, (string?[])args[5]!);
        }
    }

    [TestMethod]
    public void DisconnectedHandlersCanBeCollected()
    {
        // The source keeps a reference to the sink, which keeps the handler alive, until the connection is disposed.
        var source = new DynamicEventTests.EventSource(provideClassInfo: true);

        (WeakReference handler, EventConnection connection) = Connect(source);
        LeakDetector.Collect();
        Assert.IsTrue(handler.IsAlive);

        connection.Dispose();
        LeakDetector.Collect();
        Assert.IsFalse(handler.IsAlive);
        GC.KeepAlive(source);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (WeakReference Handler, EventConnection Connection) Connect(DynamicEventTests.EventSource source)
        {
            var handler = new EventHandler();
            var connection = EventConnection.AdviseDispatch(source, ShapeEventsIid, handler);
            DispatchObject.Invoke(GetSinks(source).Single(), EventHandler.ReadId, DispatchFlags.Method, "text");
            Assert.AreEqual(1, handler.Invocations);
            return (new WeakReference(handler), connection);
        }
    }

    [TestMethod]
    public void UnsubscribedObserversCanBeCollected()
    {
        var subject = new EventSubject<int>();

        (WeakReference unsubscribed, WeakReference completed) = Subscribe(subject);
        LeakDetector.Collect();
        Assert.IsFalse(unsubscribed.IsAlive);
        Assert.IsTrue(completed.IsAlive);

        subject.OnCompleted();
        LeakDetector.Collect();
        Assert.IsFalse(completed.IsAlive);
        GC.KeepAlive(subject);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (WeakReference Unsubscribed, WeakReference Completed) Subscribe(EventSubject<int> subject)
        {
            var unsubscribed = new Observer();
            var completed = new Observer();
            IDisposable subscription = subject.Subscribe(unsubscribed);
            subject.Subscribe(completed);
            subject.OnNext(1);
            subscription.Dispose();
            return (new WeakReference(unsubscribed), new WeakReference(completed));
        }
    }

    [TestMethod]
    public void DynamicObjectsFreeTheirArgumentsAndResults()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        object native = ((IDispatchProvider)dictionary).Dispatch;
        object other = CreateDictionary();

        LeakDetector.AssertNoLeaks(
            () =>
            {
                dictionary.Add("text", "value");
                dictionary.Add("array", new[] { "a", "b" });
                dictionary["object"] = other;
                dictionary["text"] = new object?[] { "replaced", other };

                Assert.AreEqual(3, (int)dictionary.Count);
                Assert.AreEqual("replaced", (string)dictionary["text"][0]);
                Assert.AreEqual("b", (string)dictionary.Item("array")[1]);
                Assert.HasCount(3, (object?[])dictionary.Keys());

                int count = 0;
                foreach (object? key in dictionary)
                {
                    count++;
                }

                Assert.AreEqual(3, count);
                Assert.ThrowsExactly<COMException>(() => dictionary.Add("text", new[] { "again" }));
                dictionary.RemoveAll();
                Assert.AreEqual("IDictionary", ComDynamic.GetTypeName((object)dictionary));
            },
            native,
            other);
    }

    [TestMethod]
    public void DynamicEventHandlersAreDisconnected()
    {
        object native = CreateDictionary();
        var source = new DynamicEventTests.EventSource(provideClassInfo: true);

        LeakDetector.AssertNoLeaks(
            () =>
            {
                dynamic shape = CreateProxy<IDispatch>(source).AsDynamic();
                int moves = 0;
                var moved = (Action<object?, object?>)((_, _) => moves++);
                shape.Moved += moved;
                shape.Describe += new DescribeHandler((object? value, out int length) =>
                {
                    length = ((Array)value!).Length;
                    return "description";
                });

                // Moved has the identifier 1 and Describe, which returns a string and a length by reference, the identifier 4.
                IDispatch sink = GetSinks(source).Single();
                DispatchObject.Invoke(sink, 1, DispatchFlags.Method, "text", new object?[] { "a", native });
                object?[] args = [new[] { "a", "b" }, null];
                Assert.AreEqual("description", DispatchObject.InvokeByRef(sink, 4, DispatchFlags.Method, args, [false, true]));
                Assert.AreEqual((1, 2), (moves, args[1]));

                shape.Moved -= moved;
                Assert.AreEqual(1, source.Connections);
                ComDynamic.RemoveEventHandlers((object)shape);
                Assert.AreEqual(0, source.Connections);
            },
            native);
    }

    // The sinks connected to the source, called through their COM interfaces rather than as the managed objects they are.
    private static List<IDispatch> GetSinks(DynamicEventTests.EventSource source)
    {
        var wrappers = new StrategyBasedComWrappers();
        return [.. source.Points[ShapeEventsIid].Sinks.Values.Select(sink => (IDispatch)wrappers.GetOrCreateObjectForComInstance(sink, CreateObjectFlags.UniqueInstance))];
    }

    // A wrapper of a COM interface of a managed object, whose members are called through the interface like those of a
    // native object: the wrappers of the marshallers would return the managed object itself.
    private static T CreateProxy<T>(object implementation)
        where T : class
    {
        var wrappers = new StrategyBasedComWrappers();
        nint unknown = wrappers.GetOrCreateComInterfaceForObject(implementation, CreateComInterfaceFlags.None);
        try
        {
            return (T)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Handles events like the generated sinks: reads the arguments, returns values by reference and a result.</summary>
    private sealed class EventHandler : IDispatchEventHandler
    {
        /// <summary>Reads its arguments.</summary>
        public const int ReadId = 1;

        /// <summary>Replaces the values of its arguments and returns an array.</summary>
        public const int ReplaceId = 2;

        /// <summary>Sets a result and throws an exception.</summary>
        public const int FailId = 3;

        public int Invocations { get; private set; }

        /// <summary>Gets or sets the object that replaces the objects passed by reference.</summary>
        public object? Replacement { get; set; }

        public bool Invoke(int dispId, EventArguments arguments)
        {
            Invocations++;
            switch (dispId)
            {
                case ReadId:
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        _ = arguments.GetValue(i);
                    }

                    return true;

                case ReplaceId:
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        arguments.Set(i, arguments.GetValue(i) switch
                        {
                            null or string => "new",
                            int number => number + 1,
                            Array => new[] { "new", "array" },
                            _ => Replacement,
                        });
                    }

                    arguments.SetResult(new[] { "result" });
                    return true;

                case FailId:
                    arguments.SetResult(new[] { "lost" });
                    throw new InvalidOperationException("failed");

                default:
                    return false;
            }
        }
    }

    private sealed class Observer : IObserver<int>
    {
        public void OnNext(int value)
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
