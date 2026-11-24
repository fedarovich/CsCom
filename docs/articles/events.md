# Events

COM objects raise events by calling the methods of event interfaces, the `[source]` interfaces of their coclasses, on the sinks
connected to their connection points. For each event interface, CsCom generates `I<Interface>Handler` (without the `I` prefix for
names that already start with `I` or `_`), an interface with a method per event that does nothing by default, and `<Interface>Sink`,
which connects to the events. What else is generated
depends on `--event-style`:

```csharp
// events (default): .NET events, with TlbImp-style delegates such as AppEvents_WorkbookBeforeCloseEventHandler
using var events = new AppEventsSink(app);          // connects (IConnectionPoint::Advise); Dispose disconnects
events.NewWorkbook += workbook => Console.WriteLine(workbook?.Name);
events.WorkbookBeforeClose += (_Workbook? workbook, ref bool cancel) => cancel = true;

// observable: an IObservable<T> per event, with an args class whose ref and out parameters and Result can be set
using var events = new AppEventsSink(app);
using var subscription = events.WorkbookBeforeClose.Subscribe(e => e.Cancel = true);

// events, observable and interface: implement only the methods of the handler interface you need
sealed class Handler : IAppEventsHandler
{
    public void WorkbookBeforeClose(_Workbook? Wb, ref bool Cancel) => Cancel = true;
}

using var connection = AppEventsSink.Connect(app, new Handler());
```

With `interface`, `<Interface>Sink` is a static class with only `Connect`, and `none` generates no event code.

An event that the source raises while the sink connects, e.g. its state during `IConnectionPoint::Advise`, reaches no
handler or observer with `events` and `observable`, since they can only subscribe once the sink has been created. A
handler passed to `Connect` receives it, but gets the connection only after `Advise` has returned.

An event named like a member of the sink class or of `object`, e.g. `Dispose`, `DisposeAsync`, `Connect`, `Iid` or
`Equals`, gets the suffix `Event` for its .NET event or observable, e.g. `DisposeAsyncEvent`; the method of the handler
interface keeps its name. The sink class is partial and implements `IDisposable` and `IAsyncDisposable`, so a part of your
own cannot declare `Dispose` or `DisposeAsync`.

## Observables

The observables are hot: events are not buffered. They are plain `IObservable<T>`s, so they work with Reactive Extensions
without depending on it.

Disposing the sink completes them, once no handler runs any more:

- When no handler runs, `Dispose` completes them at once.
- It does not wait for a running handler, on another thread or the one that disposes the sink: the handler completes them
  itself when it returns, on its thread and in the event call of the source, like the values that it pushes.

So `OnCompleted` has the constraints of `OnNext`, and an exception of an observer fails that event call like one of
`OnNext`. Unlike the exception of `OnNext`, it stops neither the other observers nor the other observables, since an
observer that missed the completion would never be completed: once all of them are completed, it is thrown as it is, or
in an `AggregateException` when several observers threw.

`DisposeAsync` completes them in the synchronization context of the caller, and throws the exceptions of the observers to
it, see [DisposeAsync](#disposeasync).

## Event interfaces

All kinds of event interfaces are supported:

- **Dispinterfaces**, the most common ones (Office, VB6 and ATL components), are received by `DispatchEventSink` of the support
  code, which implements `IDispatch` and answers `QueryInterface` for the dispinterface. The arguments of `IDispatch::Invoke` are
  converted to the late-bound types, as in the late-bound wrappers, and `ref` and `out` arguments and results are passed back.
- **Vtable interfaces** are implemented by a generated `[GeneratedComClass]` sink with the signatures of the generated interface.
- **Dual interfaces** are implemented in the same way, and the sink also implements `IDispatch::Invoke`, which event sources often use.

## Event sources

The source can be any object returned by the generated code: a COM interface, a late-bound wrapper or a `dynamic` object.
An object of yours that is exposed to COM (`[GeneratedComClass]`), also in a late-bound or `dynamic` wrapper, raises
the events itself, or else, when it implements `IDispatchProvider`, the COM object in its `Dispatch` does, e.g. for a
decorator of a COM object.

Connecting to a source fails:

- with an `InvalidOperationException` when the source does not raise the events of the interface: it does not implement
  `IConnectionPointContainer`, or has no connection point of the interface;
- with the exception of its `HRESULT`, a `COMException`, for any other error of the source, e.g. of a server that has
  ended or is busy (`RPC_E_CALL_REJECTED`), so that a connection to a busy server can be tried again.

`EventConnection.Advise` and `EventConnection.AdviseDispatch` of the support code connect hand-written sinks, see
[Hand-written sinks](#hand-written-sinks).

## Threads

Events are delivered on the threads chosen by COM: a single-threaded apartment (STA) object, such as an Office application,
delivers them through the message loop of the thread that connected the sink. Dispose the sink on that thread, because
`IConnectionPoint::Unadvise` must be called from the same apartment; there is no finalizer that disconnects it.

## Exceptions of handlers

An exception thrown by a handler is returned to the source, and most sources ignore it:

- through `IDispatch::Invoke`, as `DISP_E_EXCEPTION` with the error information, whose code is `E_FAIL` when the
  `HResult` of the exception is no error code, e.g. `S_FALSE`, and without its description when its `Message` throws;
- through the vtable, as the `HRESULT`.

## Disconnecting

`EventConnection.Disconnect` returns the `HRESULT` of the disconnection and never throws: the exception of a connection
point that is a managed object is returned as its `HRESULT`, as a call through COM would return it.

| Result | Meaning |
| --- | --- |
| `S_OK` | The sink is disconnected. `Disconnect` returns any success of `Unadvise` as `S_OK`, so that `S_FALSE` means nothing else. |
| `S_FALSE` | Another call is disconnecting the same sink at the moment: `Disconnect` does not wait for it, but returns at once. |
| A [transient error](#transient-errors) | The disconnection can succeed later: the connection stays connected. |
| Any other error | The connection is given up. |

Once the connection is over, `Disconnect` returns the `HRESULT` that ended it: `S_OK`, or the error that gave it up.

### Transient errors

A disconnection that can succeed later leaves the connection connected (`EventConnection.IsConnected` stays `true`), and
another call of `Disconnect` or `Dispose` retries it. These transient errors are:

- `RPC_E_WRONG_THREAD`, from another thread than the one that connected the sink;
- `RPC_E_CALL_REJECTED` and `RPC_E_SERVERCALL_RETRYLATER`, from a busy server;
- `RPC_E_CANTCALLOUT_INASYNCCALL`, `RPC_E_CANTCALLOUT_INEXTERNALCALL` and `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`.

Any other error gives the connection up.

### The event gate

Every connection has an `EventGate` of its own, which it closes first when it is disconnected, and when it cannot be
established. Once the gate is closed, no handler call starts, also when the source could not be told and still calls the
sink: the sink accepts the events without passing them on, with default results.

A call that started before still completes, since COM does not wait for it either; `DisposeAsync` of the connection or of
a generated sink waits for it:

```csharp
await using var events = new AppEventsSink(app);  // DisposeAsync completes when no handler runs any more
```

### DisposeAsync

Like `Dispose`, `DisposeAsync` must run on the thread that connected the sink when the source is a single-threaded apartment
object, e.g. Office. An `await` before the end of the scope can resume on another thread, e.g. on the thread pool of a
console application, and the disconnection then fails with `RPC_E_WRONG_THREAD` and leaves the sink connected. Use
`await using` where the code resumes on the same thread, e.g. in the synchronization context of a user interface thread,
and otherwise call `Dispose` on that thread.

Unlike `Dispose`, `DisposeAsync` reports a transient error, which leaves the sink connected: once no handler runs any more,
it throws a `COMException` with the `HRESULT` if the sink is still connected then. A generated sink of the observable style
completes its observables first.

- Another call can have disconnected the sink in the meantime, e.g. `Dispose` on the right thread: `DisposeAsync` then
  completes.
- When another call is disconnecting the sink at the moment, `DisposeAsync` waits for it without blocking, and reports its
  error the same way, also when `DisposeAsync` is called on the right thread. It does not disconnect the sink again, which
  it would have to do on the thread of its caller, while that thread may be blocked waiting for it. Dispose the sink
  again on that thread then.
- When an observer throws too, the sink throws both in an `AggregateException`, the `COMException` first, so that an
  observer cannot hide it.

When `DisposeAsync` throws, dispose the sink again, e.g. on the right thread. The exception of `await using` replaces an
exception of its body.

Do not block on `DisposeAsync` in a handler of the same connection, since it waits for that handler too, nor in an
observer's `OnCompleted`. With the observable style, `DisposeAsync` completes the observables once no handler runs any more,
in the synchronization context of the caller, e.g. on the thread of a user interface. So do not block on it (`Wait()`,
`Result`) on a thread with a synchronization context either: when a handler is running, the completion waits for that
thread, which waits for the completion. Await it there, or call `Dispose`, which does not wait.

### DisposeWithCompletion

`EventConnection.DisposeWithCompletion(completion)` disconnects like `Dispose`, and calls the completion once no handler
call is running any more, without waiting for it and without a synchronization context:

- at once when none runs, and then its exception is thrown to the caller;
- otherwise on the thread of the last running call, when its handler has returned, and then its exception fails the event
  call like an exception of a handler.

`Dispose` of the observable style uses it.

Every call adds its completion: they run one at a time, in the order of the calls. An exception does not stop the others,
which other calls may have added: once all of them ran, the exception of the one that failed is thrown, or an
`AggregateException` of them when several failed. A completion that is added while others run, also by one of them, runs
after them on their thread, and its exception is that thread's. A sink that was passed to `Advise` itself has no gate, so
its completion runs at once.

`DisposeAsync` waits for the completions that the last running handler call runs, but not for one that runs at once on
another thread: when `Dispose` of the observable style and `DisposeAsync` run at the same time on two threads,
`DisposeAsync` can complete while the other thread still completes the observers.

## Hand-written sinks

`EventConnection.Advise` and `EventConnection.AdviseDispatch` connect hand-written sinks. The generated sinks and those of
`AdviseDispatch` use the [gate](#the-event-gate). A hand-written sink gets it when `EventConnection.Advise` creates the
sink for the connection, and passes an event to its handler between `TryEnter` and `Exit`:

```csharp
using var connection = EventConnection.Advise(source, iid, gate => new ChangeSink(handler, gate));

// in ChangeSink, for every event:
if (!gate.TryEnter()) return;   // the connection is disconnected
try { handler.Changed(); } finally { gate.Exit(); }
```

- Call `Exit` exactly once after each `TryEnter` that returned `true`, and never after one that returned `false`. The gate
  counts the running events, so an `Exit` too many while another event runs counts that event as finished, and
  `DisposeAsync` can complete while its handler still runs; one when no event runs throws an `InvalidOperationException`.
- The last `Exit` of a closed gate runs the completions of `DisposeWithCompletion`, and throws their exceptions as above:
  handle them like an exception of the handler.
- `EventGate.IsClosed` tells whether the gate is closed, which it is from the start of the first disconnection, also one
  that leaves the sink connected.

A sink that also implements `IDispatch`, e.g. for a dual event interface, passes the invocations of `IDispatch::Invoke` to
an `IDispatchEventHandler` with `DispatchEventSink.InvokeHandler(gate, handler, …)`. `InvokeHandler` enters and exits the
gate, and accepts the invocation with an empty result when the gate is closed, whatever its `DISPID`, since it does not
ask the handler any more. It returns the exception of a completion like one of the handler: the exception replaces the result of
the handler, also its error, as a `finally` block would, and as the generated sinks do.

A sink that is passed to `Advise` itself has no gate: it gets events until the source is told, and `DisposeAsync` does not
wait for its handler calls.
