using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CsCom.Runtime;

/// <summary>
/// The gate of an event connection, which <see cref="EventConnection"/> creates for each connection and passes to the sink
/// that it connects. The gate is closed once the connection is disconnected, or when it could not be established; a closed
/// sink accepts the events without passing them to its handler.
/// </summary>
/// <remarks>
/// A sink passes an event to its handler only between a successful <see cref="TryEnter"/> and <see cref="Exit"/>, so that no
/// handler call starts once the gate is closed, and the connection knows when the calls that started before have
/// completed (see <see cref="EventConnection.DisposeAsync"/>). The last of them runs the completions of
/// <see cref="EventConnection.DisposeWithCompletion"/> in its call of <see cref="Exit"/>.
/// </remarks>
public sealed class EventGate
{
    // The high bit of the state, which is set once the gate is closed; the other bits count the running events.
    private const int Closed = int.MinValue;

    // The continuations of the awaiters of DisposeAsync never run in Exit, i.e. in the event call of the COM object: the code
    // after an await could block it, call it back, or run in its apartment rather than in the one of the caller.
    private readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;

    // The completions of EventConnection.DisposeWithCompletion that wait to run, in the order of the calls, and whether a
    // thread runs them. The lock only guards them, and is taken once per connection by the last event that exits: the
    // completions run outside it.
    private readonly Lock _completionsLock = new();
    private List<Action>? _completions;
    private bool _runningCompletions;

    internal EventGate()
    {
    }

    /// <summary>
    /// Gets a value indicating whether the gate is closed: the sink must pass no more events to its handler.
    /// </summary>
    public bool IsClosed => (Volatile.Read(ref _state) & Closed) != 0;

    /// <summary>
    /// Completes once the gate is closed and no event that entered it is running any more.
    /// </summary>
    internal Task Idle => _idle.Task;

    /// <summary>
    /// Enters an event before its handler is called. Each successful call must be followed by a call of <see cref="Exit"/>
    /// when the handler returns.
    /// </summary>
    /// <returns><see langword="false"/> when the gate is closed: the sink must not call its handler then.</returns>
    public bool TryEnter()
    {
        int state = Volatile.Read(ref _state);
        while ((state & Closed) == 0)
        {
            int original = Interlocked.CompareExchange(ref _state, state + 1, state);
            if (original == state)
            {
                return true;
            }

            state = original;
        }

        return false;
    }

    /// <summary>
    /// Exits an event that <see cref="TryEnter"/> has entered, when its handler has returned. Call it exactly once after
    /// each call of <see cref="TryEnter"/> that returned <see langword="true"/>, and never after one that returned
    /// <see langword="false"/>: <c>if (!gate.TryEnter()) return; try { … } finally { gate.Exit(); }</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last event to exit a closed gate runs the completions of <see cref="EventConnection.DisposeWithCompletion"/>
    /// before the method returns, also those that are added while they run. Once all of them ran, the method throws the
    /// exception of one that failed, or an <see cref="AggregateException"/> of them when several failed: a sink handles it
    /// like an exception of its handler, e.g. returns it to the COM object. Called in a <c>finally</c> block, it replaces
    /// an exception of the handler, as <c>DispatchEventSink.InvokeHandler(gate, …)</c> does.
    /// </para>
    /// <para>
    /// The gate counts the running events, not which ones they are: a call too many is only detected when no event is
    /// running. While others are running, it counts one of them as finished, so that
    /// <see cref="EventConnection.DisposeAsync"/> can complete, and the completions run, while a handler still runs, and
    /// the call of that event then throws when it exits.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No event has entered the gate.</exception>
    public void Exit()
    {
        int state = Volatile.Read(ref _state);
        while (true)
        {
            // A call without an entered event would corrupt the count, and with it the closed bit.
            if ((state & ~Closed) == 0)
            {
                throw new InvalidOperationException("Exit was called without a successful call of TryEnter.");
            }

            int original = Interlocked.CompareExchange(ref _state, state - 1, state);
            if (original == state)
            {
                // Once the gate is closed, no event enters any more, so the last one to exit completes the closing: it runs
                // the completions first, so that DisposeAsync completes after them.
                if (state - 1 == Closed)
                {
                    try
                    {
                        RunWaitingCompletions();
                    }
                    finally
                    {
                        _idle.TrySetResult();
                    }
                }

                return;
            }

            state = original;
        }
    }

    /// <summary>
    /// Closes the gate: no event enters it any more.
    /// </summary>
    /// <remarks>
    /// It runs no completion, so that <see cref="EventConnection.Disconnect"/> never throws: a completion that is added
    /// when no event runs any more runs in <see cref="WhenIdle"/>.
    /// </remarks>
    internal void Close()
    {
        if (Interlocked.Or(ref _state, Closed) == 0)
        {
            _idle.TrySetResult();
        }
    }

    /// <summary>
    /// Runs a completion once the gate, which must be closed, is idle: at once on the calling thread when no event runs,
    /// otherwise on the thread of the last running event, when it exits. The completions run one at a time, in the order
    /// of the calls: one that is added while others run is left to the thread that runs them, which runs it after them.
    /// An exception does not stop the others: that thread throws it once all of them ran, in an
    /// <see cref="AggregateException"/> when several failed.
    /// </summary>
    /// <param name="completion">The completion.</param>
    internal void WhenIdle(Action completion)
    {
        lock (_completionsLock)
        {
            // Every call adds its own completion, so that a delegate of one call that has several methods still runs like a
            // delegate, and stops at its exception.
            (_completions ??= []).Add(completion);

            // The last event that exits runs this one: Exit writes the state before it takes the lock, so either it sees
            // the completion, or the state read here is closed and idle.
            if (Volatile.Read(ref _state) != Closed)
            {
                return;
            }
        }

        // A thread that claims the run before this one, e.g. another call or the last event, runs this completion too.
        RunWaitingCompletions();
    }

    // Runs the completions that wait, unless another thread runs them already, which runs them all: for the last event that
    // exits, and for WhenIdle once the gate is idle. The run is claimed only here.
    private void RunWaitingCompletions()
    {
        lock (_completionsLock)
        {
            if (_runningCompletions || _completions is null)
            {
                return;
            }

            _runningCompletions = true;
        }

        RunCompletions();
    }

    // Runs the completions that wait, and those that are added while they run, for the thread that has started the run. The
    // completions come from independent calls, which have returned when another thread runs them: the exception of one
    // does not stop the others, and is thrown once all of them ran.
    private void RunCompletions()
    {
        List<Exception>? exceptions = null;
        while (true)
        {
            List<Action>? completions;
            lock (_completionsLock)
            {
                // The run ends when none waits any more: a later completion runs on its own thread then.
                completions = _completions;
                _completions = null;
                if (completions is null)
                {
                    _runningCompletions = false;
                    break;
                }
            }

            foreach (Action completion in completions)
            {
                try
                {
                    completion();
                }
                catch (Exception exception)
                {
                    CompletionExceptions.Add(ref exceptions, exception);
                }
            }
        }

        CompletionExceptions.ThrowIfAny(exceptions);
    }
}
