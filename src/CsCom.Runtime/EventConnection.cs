using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
using System.Threading.Tasks;

namespace CsCom.Runtime;

/// <summary>
/// A connection of an event sink to a COM object that raises events through a connection point. Disposing the connection
/// disconnects the sink.
/// </summary>
/// <remarks>
/// <para>
/// Dispose the connection on the thread that created it: <c>IConnectionPoint::Unadvise</c> must be called from the apartment
/// the connection was made in. There is no finalizer, so a connection that is not disposed stays connected until the COM
/// object releases the sink.
/// </para>
/// <para>
/// Every connection has an <see cref="EventGate"/> of its own, which it closes first when it is disconnected, and when it
/// cannot be established. The generated sinks, the sinks of <see cref="AdviseDispatch"/> and the sinks created with the gate
/// (<see cref="Advise(object, Guid, Func{EventGate, object})"/>) start no handler call once the gate is closed, also when
/// the COM object cannot be told at the moment and still calls the sink. A call that started before still completes;
/// <see cref="DisposeAsync"/> waits for it, and <see cref="DisposeWithCompletion"/> runs a completion after it.
/// </para>
/// <para>
/// <see cref="Disconnect"/>, <see cref="Dispose"/> and <see cref="DisposeWithCompletion"/> never wait for another thread.
/// <see cref="Disconnect"/> and <see cref="Dispose"/> never throw; <see cref="DisposeWithCompletion"/> only throws the
/// exceptions of the completions that it runs itself, and the <see cref="ArgumentNullException"/> of a null completion,
/// which it throws before it closes the gate. The task of <see cref="DisposeAsync"/> fails when the sink stays
/// connected.
/// </para>
/// </remarks>
public sealed class EventConnection : IDisposable, IAsyncDisposable
{
    // Whether the connection created the object of the connection point, which it then releases.
    private readonly bool _ownsConnectionPoint;

    private readonly EventGate _gate;

    // Only read and cleared by the call of Disconnect whose Attempt is the state; null when disconnected, so that the
    // connection does not keep a connection point alive that is a managed object.
    private IConnectionPoint? _connectionPoint;

    // The state of the connection: null while it is connected, the Attempt of the call of IConnectionPoint::Unadvise that
    // is in progress, which DisposeAsync can wait for, or the Disconnection that ended the connection. A single field, so
    // that the call that takes the state publishes its attempt with it.
    private object? _state;

    private EventConnection(IConnectionPoint connectionPoint, bool ownsConnectionPoint, EventGate gate, uint cookie)
    {
        _connectionPoint = connectionPoint;
        _ownsConnectionPoint = ownsConnectionPoint;
        _gate = gate;
        Cookie = cookie;
    }

    /// <summary>Gets the cookie that identifies the connection.</summary>
    public uint Cookie { get; }

    /// <summary>
    /// Gets a value indicating whether the sink is still connected: <see langword="false"/> once <see cref="Disconnect"/>
    /// has disconnected it or given the connection up, <see langword="true"/> after a call that failed with an error that
    /// another call can succeed after.
    /// </summary>
    public bool IsConnected => Volatile.Read(ref _state) is not Disconnection;

    /// <summary>
    /// Connects a sink that implements the event interface to the events of a COM object. The sink has no gate, so it can
    /// get events until the COM object is told that it is disconnected; create it with the gate of the connection with
    /// <see cref="Advise(object, Guid, Func{EventGate, object})"/> to stop its events at once.
    /// </summary>
    /// <param name="source">
    /// The COM object: an object returned by a generated interface, a late-bound wrapper or a <c>dynamic</c> object. An
    /// object of the application that is exposed to COM, also one that such a wrapper wraps, raises the events itself, or
    /// else, when it implements <see cref="IDispatchProvider"/>, its <see cref="IDispatchProvider.Dispatch"/> does.
    /// </param>
    /// <param name="iid">The interface identifier of the event interface.</param>
    /// <param name="sink">The sink, a <see cref="GeneratedComClassAttribute"/>-attributed class that implements the event interface.</param>
    /// <returns>The connection, which must be disposed to disconnect the sink.</returns>
    /// <exception cref="ArgumentNullException">
    /// The source or the sink is <see langword="null"/>, or one of them stands for no object: an
    /// <see cref="UnknownWrapper"/> of <see langword="null"/>, or a late-bound wrapper whose
    /// <see cref="IDispatchProvider.Dispatch"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The object does not raise the events of the interface.</exception>
    /// <exception cref="ArgumentException">
    /// The source or the sink is, or wraps, a <see cref="DispatchWrapper"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The source is, or stands for, a released COM object.</exception>
    /// <exception cref="COMException">
    /// Finding the connection point or connecting the sink failed with another error, e.g. of a server that has ended or
    /// is busy.
    /// </exception>
    public static EventConnection Advise(object source, Guid iid, object sink)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        // The sink would be passed as a null pointer, which the connection point rejects, or keeps under a cookie that is
        // lost: it is rejected like null before a connection point is looked up.
        if (StandsForNoObject(sink, nameof(sink)))
        {
            throw NoObject(sink, nameof(sink));
        }

        // The connection has a gate of its own that the sink never enters, so that every connection is disconnected the
        // same way, and its completions run at once. A gate that all of them shared would run the completions of one
        // connection after those of another, on its thread, since a gate runs its completions one at a time.
        return Connect(source, iid, _ => sink);
    }

    /// <summary>
    /// Connects a sink that implements the event interface to the events of a COM object, created with the gate of the
    /// connection: the sink calls <see cref="EventGate.TryEnter"/> before it passes an event to its handler, and
    /// <see cref="EventGate.Exit"/> afterwards, so that no handler call starts once the connection is disconnected.
    /// </summary>
    /// <param name="source">
    /// The COM object: an object returned by a generated interface, a late-bound wrapper or a <c>dynamic</c> object. An
    /// object of the application that is exposed to COM, also one that such a wrapper wraps, raises the events itself, or
    /// else, when it implements <see cref="IDispatchProvider"/>, its <see cref="IDispatchProvider.Dispatch"/> does.
    /// </param>
    /// <param name="iid">The interface identifier of the event interface.</param>
    /// <param name="createSink">
    /// Creates the sink, a <see cref="GeneratedComClassAttribute"/>-attributed class that implements the event interface, with
    /// the gate of the connection. It is called for each connection, so that every sink has a gate of its own.
    /// </param>
    /// <returns>The connection, which must be disposed to disconnect the sink.</returns>
    /// <exception cref="ArgumentNullException">
    /// The source or the factory is <see langword="null"/>, or the source stands for no object: an
    /// <see cref="UnknownWrapper"/> of <see langword="null"/>, or a late-bound wrapper whose
    /// <see cref="IDispatchProvider.Dispatch"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The object does not raise the events of the interface, or the factory created no sink, also one that stands for no
    /// object.
    /// </exception>
    /// <exception cref="ArgumentException">The source is, or wraps, a <see cref="DispatchWrapper"/>.</exception>
    /// <exception cref="ObjectDisposedException">The source is, or stands for, a released COM object.</exception>
    /// <exception cref="COMException">
    /// Finding the connection point or connecting the sink failed with another error, e.g. of a server that has ended or
    /// is busy.
    /// </exception>
    public static EventConnection Advise(object source, Guid iid, Func<EventGate, object> createSink)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(createSink);
        return Connect(source, iid, createSink);
    }

    /// <summary>
    /// Connects a handler to the events of a dispinterface, which the COM object raises through <c>IDispatch::Invoke</c>.
    /// </summary>
    /// <param name="source">
    /// The COM object: an object returned by a generated interface, a late-bound wrapper or a <c>dynamic</c> object. An
    /// object of the application that is exposed to COM, also one that such a wrapper wraps, raises the events itself, or
    /// else, when it implements <see cref="IDispatchProvider"/>, its <see cref="IDispatchProvider.Dispatch"/> does.
    /// </param>
    /// <param name="iid">The interface identifier of the dispinterface.</param>
    /// <param name="handler">The handler of the invocations.</param>
    /// <returns>The connection, which must be disposed to disconnect the handler.</returns>
    /// <exception cref="ArgumentNullException">
    /// The source or the handler is <see langword="null"/>, or the source stands for no object: an
    /// <see cref="UnknownWrapper"/> of <see langword="null"/>, or a late-bound wrapper whose
    /// <see cref="IDispatchProvider.Dispatch"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The object does not raise the events of the interface.</exception>
    /// <exception cref="ArgumentException">The source is, or wraps, a <see cref="DispatchWrapper"/>.</exception>
    /// <exception cref="ObjectDisposedException">The source is, or stands for, a released COM object.</exception>
    /// <exception cref="COMException">
    /// Finding the connection point or connecting the sink failed with another error, e.g. of a server that has ended or
    /// is busy.
    /// </exception>
    public static EventConnection AdviseDispatch(object source, Guid iid, IDispatchEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        return Connect(source, iid, gate => new DispatchEventSink(iid, handler, gate));
    }

    /// <summary>
    /// Disconnects the sink and returns the <c>HRESULT</c> of <c>IConnectionPoint::Unadvise</c>, <c>S_OK</c> for any
    /// success. When the connection is already over, it returns the <c>HRESULT</c> that ended it: <c>S_OK</c> when the sink
    /// was disconnected, the error when the connection was given up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate of the connection is closed first, so that no handler call starts any more, whatever the result.
    /// </para>
    /// <para>
    /// The errors of a call that can succeed when it is made again leave the sink connected, to be disconnected by another
    /// call: <c>RPC_E_WRONG_THREAD</c> from another thread than that of the connection, <c>RPC_E_CALL_REJECTED</c> and
    /// <c>RPC_E_SERVERCALL_RETRYLATER</c> from a busy server, and <c>RPC_E_CANTCALLOUT_INASYNCCALL</c>,
    /// <c>RPC_E_CANTCALLOUT_INEXTERNALCALL</c> and <c>RPC_E_CANTCALLOUT_ININPUTSYNCCALL</c> from a call that cannot call
    /// out at the moment. Any other error gives the connection up. The exception of a connection point that is a managed
    /// object is returned as its <see cref="Exception.HResult"/>, as a call through COM would return it, or as
    /// <c>E_FAIL</c> when that is no error code.
    /// </para>
    /// <para>
    /// The method does not wait for another call that is disconnecting the sink at the moment, on another thread or as the
    /// caller of the COM object that calls back: it returns <c>S_FALSE</c> at once, and that call decides whether the sink
    /// is disconnected. <c>S_FALSE</c> means nothing else: a success of <c>Unadvise</c> is returned as <c>S_OK</c>.
    /// </para>
    /// </remarks>
    public int Disconnect()
    {
        // No handler call starts from here on, also when the COM object cannot be told now.
        _gate.Close();

        // Only one call of Unadvise at a time: the cookie of a second one could already belong to another sink. The other
        // callers must not wait for it, because the call can depend on their threads, e.g. on a lock of the COM object
        // that the caller of a handler holds. They are not told that the sink is disconnected either, since the call can
        // fail and leave it connected. The call that takes the state publishes its attempt with it, which a concurrent
        // DisposeAsync waits for.
        Attempt? attempt = null;
        object? state = Volatile.Read(ref _state);
        while (true)
        {
            switch (state)
            {
                case Disconnection disconnection:
                    return disconnection.Result;
                case Attempt:
                    return OleNativeMethods.S_FALSE;
            }

            // Only a call that calls Unadvise creates an attempt, also when nobody waits for it: a task next to a COM call,
            // which spares the waiters a protocol to install a task of their own into the state.
            attempt ??= new Attempt();
            state = Interlocked.CompareExchange(ref _state, attempt, null);
            if (state is null)
            {
                break;
            }
        }

        IConnectionPoint connectionPoint = _connectionPoint!;
        Disconnection? disconnected = null;
        int hr = OleNativeMethods.E_FAIL;
        try
        {
            try
            {
                hr = connectionPoint.Unadvise(Cookie);
            }
            catch (Exception e)
            {
                // A connection point that is a managed object is called directly: its exception is its HRESULT, as a call
                // through COM would return it. One that is no error code would read as a success, or as S_FALSE.
                hr = OleNativeMethods.GetFailure(e);
            }

            // S_FALSE only means that another call is disconnecting the sink: another success, which a connection point
            // can return, e.g. one that is a managed object, is returned as S_OK, now and by the later calls.
            if (hr >= 0)
            {
                hr = OleNativeMethods.S_OK;
            }

            if (!IsTransient(hr))
            {
                disconnected = hr == OleNativeMethods.S_OK ? Disconnection.Succeeded : new Disconnection(hr);
                _connectionPoint = null;
                if (_ownsConnectionPoint)
                {
                    ReleaseQuietly(connectionPoint);
                }
            }

            return hr;
        }
        finally
        {
            // The state must not stay the attempt, whatever happens: it is connected again, unless the connection is over.
            // The attempt completes after the state is written, so that whoever waits for it sees the final state.
            Volatile.Write<object?>(ref _state, disconnected);
            attempt.TrySetResult(hr);
        }
    }

    /// <summary>
    /// Disconnects the sink like <see cref="Disconnect"/>, but ignores the result. A disconnection that failed on the wrong
    /// thread or because the server was busy leaves the sink connected (<see cref="IsConnected"/>), and calling
    /// <see cref="Dispose"/> or <see cref="Disconnect"/> again retries it.
    /// </summary>
    public void Dispose() => Disconnect();

    /// <summary>
    /// Disconnects the sink like <see cref="Dispose"/>, and calls a completion once no handler call that entered the gate of
    /// the connection is running any more, without waiting for it: at once on the calling thread when none runs, otherwise
    /// on the thread of the last running call, when its handler has returned and before the call returns to the COM object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completion runs where <see cref="DisposeAsync"/> would complete, e.g. to complete observables once no handler
    /// pushes values any more, and needs no synchronization context: it is called in the event call of the COM object, like
    /// a handler, so that it does not depend on the thread that disposed the connection. Its exception is thrown to the
    /// caller when it runs at once, and otherwise fails the event call, like an exception of a handler. Do not block on
    /// <see cref="DisposeAsync"/> in the completion: the task completes after it.
    /// </para>
    /// <para>
    /// A sink that was passed to <see cref="Advise(object, Guid, object)"/> itself does not enter the gate, so the completion
    /// then runs at once. Every call adds its completion; they run one at a time, in the order of the calls. An exception
    /// does not stop the others, which other calls may have added: once all of them ran, the exception of the one that
    /// failed is thrown, or an <see cref="AggregateException"/> of them when several failed. A completion that is added
    /// while others run, also by one of them, is left to the thread that runs them, which runs it after them; its
    /// exception is then that thread's.
    /// </para>
    /// <para>
    /// <see cref="DisposeAsync"/> waits for the completions that the last running handler call runs, not for one that runs
    /// at once: on another thread, it can complete while such a completion still runs, e.g. while the
    /// <see cref="IDisposable.Dispose"/> of a generated sink of the observable style completes the observables.
    /// </para>
    /// </remarks>
    /// <param name="completion">The completion.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="completion"/> is <see langword="null"/>; then the sink is not disconnected, and the gate stays open.
    /// </exception>
    public void DisposeWithCompletion(Action completion)
    {
        // The argument is checked before anything is done, like the subjects of EventSubject.Complete, so that a call with a
        // null completion, an error of the caller, changes nothing: a disconnection followed by the exception would leave
        // the caller unsure whether the sink is disconnected. The gate would fail on null only later, in an event call.
        ArgumentNullException.ThrowIfNull(completion);

        // Disconnect closes the gate first, and never throws: the completion is added to a closed gate, which runs it once
        // the calls that started before have returned.
        Disconnect();
        _gate.WhenIdle(completion);
    }

    /// <summary>
    /// Disconnects the sink like <see cref="Dispose"/>, and completes when no handler call that entered the gate of the
    /// connection is running any more, after the completions of <see cref="DisposeWithCompletion"/> that the last of them
    /// runs, but not after one that runs at once on another thread. A sink that was passed to
    /// <see cref="Advise(object, Guid, object)"/> itself does not enter the gate, so the task then completes at once. Do not
    /// block on the task in a handler of the connection: the task waits for that call too.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Dispose"/>, it reports a disconnection that failed with an error that leaves the sink connected
    /// (see <see cref="Disconnect"/>): once no handler call is running any more, since the gate is closed all the same, the
    /// task fails with a <see cref="COMException"/> of the <c>HRESULT</c> if the sink is still connected. Another call can
    /// have disconnected it in the meantime, e.g. <see cref="Dispose"/> on the thread of the connection: the task then
    /// completes, and one that is disconnecting it at that moment is waited for. When the task fails,
    /// <see cref="IsConnected"/> is <see langword="true"/>, and <see cref="Dispose"/>, <see cref="Disconnect"/> or another
    /// call of the method retries the disconnection. Any other error gives the connection up, without an exception, like
    /// <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// When another call is disconnecting the sink at the moment, it waits for that call without blocking, and reports its
    /// result like its own: the task fails with the error of that call when it leaves the sink connected, also when this
    /// call is made on the right thread. It does not disconnect the sink again then: that would have to be done on the
    /// thread of the caller, which may be blocked waiting for the task. Dispose the connection again on that thread. When
    /// that call has left the sink connected already, it disconnects the sink itself.
    /// </para>
    /// <para>
    /// Like <see cref="Dispose"/>, call it on the thread that created the connection when the COM object is a single-threaded
    /// apartment object: an <c>await</c> before it can resume on another thread, from which the disconnection fails with
    /// <c>RPC_E_WRONG_THREAD</c>. The exception of <c>await using</c> replaces an exception of its body.
    /// </para>
    /// </remarks>
    /// <returns>A task that completes when no handler call is running any more.</returns>
    public ValueTask DisposeAsync()
    {
        while (true)
        {
            int hr = Disconnect();
            if (hr != OleNativeMethods.S_FALSE)
            {
                return Complete(hr);
            }

            // Another call is disconnecting the sink, or has done it in the meantime. One that is still disconnecting it is
            // waited for; one that has left the sink connected already is followed by a disconnection on this thread, the
            // caller's.
            switch (Volatile.Read(ref _state))
            {
                case Disconnection disconnection:
                    return Complete(disconnection.Result);
                case Attempt attempt:
                    return WaitForAttempt(attempt);
            }
        }
    }

    // Completes once no handler call is running any more, and fails then if the disconnection left the sink connected and
    // it still is.
    private ValueTask Complete(int hr) => IsTransient(hr) ? FailWhenIdle(hr) : new ValueTask(_gate.Idle);

    // Waits for the call that is disconnecting the sink without blocking, and reports the result like its own; a call that
    // follows it is waited for by FailWhenIdle. It does not disconnect the sink again: it does not resume on the thread of
    // the caller, from which a call could succeed that failed on another thread, and waiting for that thread could deadlock
    // a caller that blocks.
    private async ValueTask WaitForAttempt(Attempt attempt) =>
        await Complete(await attempt.Task.ConfigureAwait(false)).ConfigureAwait(false);

    // Reports a disconnection that left the sink connected once no handler call is running any more, like a disconnection
    // that succeeded: the gate is closed either way, so that the caller can rely on it, e.g. to complete observables. Only
    // if the sink is still connected then: another call can have disconnected it in the meantime, e.g. on the thread of
    // the connection, or be disconnecting it, which is waited for. The HRESULT is that of the last call that left it
    // connected, as far as it is known.
    private async ValueTask FailWhenIdle(int hr)
    {
        await _gate.Idle.ConfigureAwait(false);
        while (Volatile.Read(ref _state) is { } state)
        {
            if (state is not Attempt attempt)
            {
                // Disconnected, or given up, which Dispose does not report either.
                return;
            }

            hr = await attempt.Task.ConfigureAwait(false);
        }

        throw new COMException(
            $"The sink could not be disconnected, and stays connected: {Marshal.GetExceptionForHR(hr)?.Message} Dispose the connection again, on the thread that connected it or when the server is not busy any more.",
            hr);
    }

    // Connects the sink that the factory creates with the gate of the connection.
    // Only the members that convert pointers are unsafe, so that DisposeAsync can await.
    private static unsafe EventConnection Connect(object source, Guid iid, Func<EventGate, object> createSink)
    {
        IConnectionPoint connectionPoint = FindConnectionPoint(source, iid, out bool ownsConnectionPoint);
        var gate = new EventGate();
        try
        {
            object sink = createSink(gate) ?? throw new InvalidOperationException("The factory created no sink.");

            // The connection point asks the sink for the event interface: the sink, a [GeneratedComClass], is exposed
            // itself, also when it implements IDispatchProvider. The source, by contrast, can be a late-bound wrapper of
            // the COM object.
            // A sink that stands for no object has no pointer: it fails like no sink, before the connection point gets a
            // null pointer, which it could keep under a cookie that is lost.
            nint sinkUnknown = (nint)ComMarshalling.ToUnmanaged(sink);
            if (sinkUnknown == 0)
            {
                throw new InvalidOperationException(
                    $"The factory created no sink: the {sink.GetType()} stands for no object, as an UnknownWrapper of null or a late-bound wrapper whose Dispatch is null.");
            }

            try
            {
                Marshal.ThrowExceptionForHR(connectionPoint.Advise(sinkUnknown, out uint cookie));
                return new EventConnection(connectionPoint, ownsConnectionPoint, gate, cookie);
            }
            finally
            {
                Marshal.Release(sinkUnknown);
            }
        }
        catch
        {
            // The source may keep the sink although Advise failed, e.g. when the connection broke after it added the sink:
            // the gate is closed first, so that no event reaches the handler of a connection that failed. The gate and the
            // sink belong to this attempt only.
            gate.Close();
            if (ownsConnectionPoint)
            {
                ReleaseQuietly(connectionPoint);
            }

            throw;
        }
    }

    // Finds the connection point of the event interface: a unique object of its own when the source is a COM object,
    // which the caller then owns.
    private static unsafe IConnectionPoint FindConnectionPoint(object source, Guid iid, out bool ownsConnectionPoint)
    {
        // An object of the application that is exposed to COM is the source itself when it raises events, and otherwise the
        // COM object that it wraps, e.g. a decorator of a COM object: the object that raises the events. A late-bound
        // wrapper is asked through its COM object, also such an object.
        int hr = ComMarshalling.QueryInterface(source, OleNativeMethods.IID_IConnectionPointContainer, out nint pointer);
        if (hr >= 0 && pointer == 0)
        {
            // A source that stands for no object, an UnknownWrapper of null or a late-bound wrapper whose Dispatch breaks
            // its contract, is rejected like null, as ComDynamic rejects it.
            throw NoObject(source, nameof(source));
        }

        // Only E_NOINTERFACE says that the object does not raise events: another error, e.g. of a server that has ended or
        // is busy, which a caller can retry, throws the exception of its HRESULT, as in ComObjects.GetDispatch.
        if (hr < 0)
        {
            throw hr == OleNativeMethods.E_NOINTERFACE
                ? new InvalidOperationException(
                    $"The object of type {source.GetType()} does not raise events: it does not implement IConnectionPointContainer.",
                    Marshal.GetExceptionForHR(hr))
                : Marshal.GetExceptionForHR(hr)!;
        }

        IConnectionPointContainer container;
        try
        {
            // The container and the connection point are objects of the connection only, which are unique, so that they can
            // be released at once: shared ones would keep the source alive until they are garbage collected, also a unique
            // source that is released with FinalRelease.
            container = ComMarshalling.ToManaged<IConnectionPointContainer>((void*)pointer, unique: true)!;
        }
        finally
        {
            Marshal.Release(pointer);
        }

        // A container that is a COM object is called through COM, and the marshaller creates a unique object of the
        // connection point for the connection. A managed container, which the pointer is unwrapped to, is called directly
        // and returns an object of its own, which is not released.
        ownsConnectionPoint = (object)container is ComObject;
        int result;
        IConnectionPoint? connectionPoint;
        try
        {
            result = container.FindConnectionPoint(iid, out connectionPoint);
        }
        finally
        {
            ReleaseQuietly(container);
        }

        if (result < 0 || connectionPoint is null)
        {
            if (ownsConnectionPoint)
            {
                ReleaseQuietly(connectionPoint);
            }

            throw RaisesNoEvents(result)
                ? new InvalidOperationException(
                    $"The object of type {source.GetType()} does not raise the events of the interface {iid:B}.",
                    result < 0 ? Marshal.GetExceptionForHR(result) : null)
                : Marshal.GetExceptionForHR(result)!;
        }

        return connectionPoint;
    }

    // Whether the result of IConnectionPointContainer::FindConnectionPoint says that the object does not raise the events:
    // a success without a connection point, CONNECT_E_NOCONNECTION, which the method is documented to return, and
    // E_NOINTERFACE and E_NOTIMPL, which implementations without the connection point return. Another error, e.g. of a
    // server that has ended or is busy, which a caller can retry, throws the exception of its HRESULT.
    private static bool RaisesNoEvents(int result) => result is >= 0
        or OleNativeMethods.CONNECT_E_NOCONNECTION
        or OleNativeMethods.E_NOINTERFACE
        or OleNativeMethods.E_NOTIMPL;

    // Releases an object of the connection, the container or the connection point, when it is no longer needed. A failure,
    // e.g. of a ComWrappers of the application, leaves the object to the garbage collector: Disconnect does not throw, and
    // a failed connection reports its own error.
    private static void ReleaseQuietly(object? value)
    {
        try
        {
            ComMarshalling.ReleaseObject(value);
        }
        catch (Exception)
        {
            // The object is not needed any more all the same.
        }
    }

    // Whether a value stands for no object, like null: an UnknownWrapper of null, or a late-bound wrapper whose Dispatch
    // breaks its contract. A DispatchWrapper is rejected with an ArgumentException for the parameter, as everywhere.
    private static bool StandsForNoObject(object value, string paramName) =>
        ComMarshalling.UnwrapUnknownWrapper(value, paramName) is not { } unwrapped
        || ComMarshalling.IsLateBoundWrapper(unwrapped, out IDispatch? dispatch) && dispatch is null;

    // The exception of a value that stands for no object, with the messages of ComDynamic. Unwrapping it again on this
    // path finds what the caller found: an UnknownWrapper does not change, and a DispatchWrapper has thrown already.
    private static ArgumentNullException NoObject(object value, string paramName) =>
        new(paramName, ComMarshalling.UnwrapUnknownWrapper(value, paramName) is { } wrapper
            ? $"The late-bound wrapper of type {wrapper.GetType()} has no COM object: its Dispatch is null."
            : "The UnknownWrapper wraps no object.");

    // The errors of a call that can succeed when it is made again, from the right thread or when the server is not busy
    // any more, e.g. an application that shows a modal dialog. Any other error gives the connection up: the COM object has
    // disconnected the sink itself, or it does not exist any more.
    private static bool IsTransient(int hr) => hr is OleNativeMethods.RPC_E_WRONG_THREAD
        or OleNativeMethods.RPC_E_CALL_REJECTED
        or OleNativeMethods.RPC_E_SERVERCALL_RETRYLATER
        or OleNativeMethods.RPC_E_CANTCALLOUT_INASYNCCALL
        or OleNativeMethods.RPC_E_CANTCALLOUT_INEXTERNALCALL
        or OleNativeMethods.RPC_E_CANTCALLOUT_ININPUTSYNCCALL;

    // The state of a call of IConnectionPoint::Unadvise in progress, which completes with its HRESULT once the state that
    // follows it is written. Its continuations never run in that call, which can be the event call of the COM object.
    private sealed class Attempt() : TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

    // The state of a connection that is over, with the HRESULT that ended it, which Disconnect returns again.
    private sealed class Disconnection(int result)
    {
        public static readonly Disconnection Succeeded = new(OleNativeMethods.S_OK);

        public int Result => result;
    }
}
