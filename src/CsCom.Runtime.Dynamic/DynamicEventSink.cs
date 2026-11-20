using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace CsCom.Runtime;

/// <summary>
/// Receives the events of a source dispinterface of a COM object and passes them to the delegates added through
/// <c>dynamic</c> (<c>d.Event += handler</c>) or <see cref="ComDynamic.AddEventHandler"/>.
/// </summary>
/// <remarks>
/// <para>
/// The sink is connected while it has handlers. Every connection has a sink object of its own, so that the events of a
/// connection that is not the current one are ignored: a COM object passes each event to all the sinks that are connected
/// to it.
/// </para>
/// <para>
/// The lock is never held during a call of the COM object, and no call waits for a connection or a disconnection of another
/// thread: the COM object can raise an event while it is called, on the same thread or on another one, whose handler adds
/// or removes handlers. So several connections can exist at the same time, of which only one passes the events on:
/// </para>
/// <list type="bullet">
/// <item>The dispatcher is chosen when the COM object is known to have the sink of a connection: the first connection that
/// receives an event, also while it is being connected, so that the events that the COM object raises during the
/// connection reach the handlers, or else the first one that is established. The order in which the connections were
/// started says nothing about which sink the COM object knows.</item>
/// <item>The dispatcher is kept until the last handler is removed or its connection fails: switching between two sinks
/// that the COM object calls one after another would lose or double the event between the two calls.</item>
/// <item>Another connection that is established is retired once the dispatcher is established. Until then it is a backup,
/// which takes over if the connection of the dispatcher fails.</item>
/// <item>The connections are retired when the last handler is removed. A retired connection is disconnected outside the
/// lock; one that cannot be disconnected at the moment, e.g. because the COM object is busy or the call comes from the
/// wrong apartment, stays retired, passes no events on, and is disconnected again by the next change of the handlers. Any
/// other error ends the connection, also an exception of a connection point that is a managed object, which counts as its
/// <c>HRESULT</c>: the source keeps the closed sink until it releases it.</item>
/// </list>
/// <para>
/// The limitations, all while several threads connect the sink at the same time: an event that a multi-threaded COM object
/// raises while a sink is being added and passes on afterwards can reach only a connection that is not the dispatcher, and
/// be ignored; avoiding it would need a connection to wait for that of another thread, which can deadlock. When a
/// dispatcher that has received events then fails to connect, the events around the switch to the backup can be lost or
/// doubled. And a handler can receive the events raised while it is being added, also if adding it then fails.
/// </para>
/// <para>
/// Adding a handler connects the sink if it has no established connection, also when another thread is connecting it: it
/// returns only when a connection is established, and it throws, without adding the handler, when its own connection
/// fails and no other one is established. Removing handlers never connects the sink and never fails.
/// </para>
/// <para>
/// Concurrent additions and removals of equal handlers take effect one after another, each at some moment between its
/// call and its return, as for a .NET event. A removal that takes the entry of an addition that then fails counts as
/// made before the additions that are still connecting, which keep their handlers; the failed addition adds none.
/// </para>
/// </remarks>
[RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
internal sealed class DynamicEventSink(object source, EventInterface eventInterface)
{
    // The signatures of the handlers by delegate type, read once rather than for each event. The table does not keep the
    // types of collectible assemblies alive.
    private static readonly ConditionalWeakTable<Type, HandlerSignature> Signatures = new();

    // Guards the handlers and the connections. It is never held during a call of the COM object.
    private readonly Lock _lock = new();
    // The handlers by DISPID, each addition in an entry of its own, which is compared by reference: a failed addition removes
    // exactly what it added, and equal delegates that other threads added stay.
    private readonly Dictionary<int, List<Subscription>> _handlers = [];

    // The connections, oldest first: those that are being connected or established, and the retired ones that may still
    // be connected.
    private readonly List<Connection> _connections = [];

    // The connection that passes the events on: null, or a connection of the list that is not retired. It records a
    // choice rather than being derived from the connections, see the remarks: it is set by the first event or the first
    // established connection while there are handlers, and kept until the last handler is removed or its connection fails.
    private Connection? _dispatcher;

    private enum ConnectionState
    {
        Connecting,
        Connected,
        Retired,
    }

    /// <summary>
    /// Adds a handler of an event, connecting the sink if it has no established connection.
    /// </summary>
    /// <exception cref="ArgumentException">The handler has more parameters than the event.</exception>
    public void Add(EventMember member, Delegate handler)
    {
        int count = GetSignature(handler).Types.Length;
        if (count > member.ParameterCount)
        {
            throw new ArgumentException(
                $"The handler has {count} parameters, but the event '{member.Name}' has {member.ParameterCount}.", nameof(handler));
        }

        Connection? connection = null;
        Connection[] retired;
        Subscription subscription;
        lock (_lock)
        {
            subscription = AddHandler(member, handler);
            if (!HasEstablishedConnection())
            {
                connection = new Connection(this);
                _connections.Add(connection);
            }

            retired = TakeRetired();
        }

        Disconnect(retired);
        if (connection is null)
        {
            return;
        }

        EventConnection established;
        try
        {
            established = EventConnection.AdviseDispatch(source, eventInterface.Iid, connection);
        }
        catch
        {
            // The handler is served by a connection that another thread has established meanwhile; otherwise it is not
            // added, which the exception reports.
            bool served;
            lock (_lock)
            {
                // The COM object may still have the sink, e.g. when the connection broke after it was added: its events
                // are ignored, and a backup takes over if it was the dispatcher.
                connection.State = ConnectionState.Retired;
                if (ReferenceEquals(_dispatcher, connection))
                {
                    _dispatcher = null;
                }

                _connections.Remove(connection);

                // Only the entry of this addition is removed, if a removal has not taken it already: another thread may
                // have added an equal handler meanwhile, whose addition succeeded. A removal that took the entry counts
                // as made before the additions that are still connecting, which add their handlers after it, while this
                // one adds none. Taking another equal entry instead could take that of an addition that started after
                // the removal, e.g. -= and then += on one thread, which must keep its handler. Normalize comes
                // afterwards, so that a sink that has no handlers any more has no dispatcher either; it keeps an
                // established dispatcher, so the handler stays served.
                served = HasEstablishedConnection();
                if (!served)
                {
                    RemoveSubscription(member.DispId, subscription);
                }

                Normalize();
                retired = TakeRetired();
            }

            Disconnect(retired);
            if (served)
            {
                return;
            }

            throw;
        }

        lock (_lock)
        {
            connection.Established = established;
            connection.State = ConnectionState.Connected;
            Normalize();
            retired = TakeRetired();
        }

        Disconnect(retired);
    }

    /// <summary>
    /// Removes the last occurrence of a handler of an event, disconnecting the sink when no handlers remain.
    /// </summary>
    public void Remove(EventMember member, Delegate handler)
    {
        Connection[] retired;
        lock (_lock)
        {
            RemoveHandler(member, handler);
            Normalize();
            retired = TakeRetired();
        }

        Disconnect(retired);
    }

    /// <summary>
    /// Removes all handlers and disconnects the sink.
    /// </summary>
    public void RemoveAll()
    {
        Connection[] retired;
        lock (_lock)
        {
            foreach (var handlers in _handlers.Values)
            {
                foreach (var subscription in handlers)
                {
                    subscription.IsRemoved = true;
                }
            }

            _handlers.Clear();
            Normalize();
            retired = TakeRetired();
        }

        Disconnect(retired);
    }

    // Passes an event that arrives through a connection to the handlers, if the connection is the dispatcher. The first
    // event chooses the dispatcher, if none is chosen: the COM object has the sink of the connection, also when it is still
    // being connected. No connection needs to be retired then, since Normalize would have chosen an established one.
    // The handlers are called outside the lock, so a handler can be removed while the event runs: the entries rather than
    // the delegates are copied, and one that is removed before the event reaches it is skipped. One that the event has
    // reached can still be called after the removal returns; making it wait would need -= to wait, which can deadlock.
    private bool Invoke(Connection connection, int dispId, EventArguments arguments)
    {
        Subscription[] subscriptions;
        lock (_lock)
        {
            if (_dispatcher is null && _handlers.Count > 0 && connection.State != ConnectionState.Retired)
            {
                _dispatcher = connection;
            }

            subscriptions = ReferenceEquals(_dispatcher, connection) && _handlers.TryGetValue(dispId, out var list) ? [.. list] : [];
        }

        if (subscriptions.Length == 0)
        {
            return eventInterface.DispIds.Contains(dispId);
        }

        foreach (Subscription subscription in subscriptions)
        {
            foreach (Delegate single in subscription.Handler.GetInvocationList())
            {
                if (subscription.IsRemoved)
                {
                    break;
                }

                Invoke(single, arguments);
            }
        }

        return true;
    }

    // Restores the invariants after a change; the caller holds the lock. Without handlers, there is no dispatcher, the
    // established connections are retired, and a connection that is being connected retires itself when it is established.
    // With handlers, the oldest established connection becomes the dispatcher if none is chosen, and an established
    // dispatcher makes every other established connection redundant; a dispatcher that is still being connected keeps
    // them as backups.
    private void Normalize()
    {
        if (_handlers.Count == 0)
        {
            _dispatcher = null;
        }
        else
        {
            _dispatcher ??= _connections.Find(static c => c.State == ConnectionState.Connected);
        }

        Connection? dispatcher = _dispatcher;
        if (dispatcher is not null && dispatcher.State != ConnectionState.Connected)
        {
            return;
        }

        foreach (Connection connection in _connections)
        {
            if (connection.State == ConnectionState.Connected && !ReferenceEquals(connection, dispatcher))
            {
                connection.State = ConnectionState.Retired;
            }
        }
    }

    private bool HasEstablishedConnection() => _connections.Exists(static c => c.State == ConnectionState.Connected);

    // Forgets the retired connections that are disconnected, and returns those that are still connected, which the caller
    // disconnects outside the lock. The caller holds the lock. Usually none is retired, which allocates nothing; otherwise
    // the list holds a few connections, which a single loop that compacts it would walk only once, at the cost of the
    // three steps that say what happens (see docs/known-issues.md).
    private Connection[] TakeRetired()
    {
        if (!_connections.Exists(static c => c.State == ConnectionState.Retired))
        {
            return [];
        }

        _connections.RemoveAll(static c => c.State == ConnectionState.Retired && c.Established is not { IsConnected: true });
        return [.. _connections.FindAll(static c => c.State == ConnectionState.Retired)];
    }

    // Disconnects retired connections; their errors are not reported, since their handlers have been removed already, and
    // EventConnection.Disconnect does not throw. A connection that the server refuses to disconnect at the moment (a
    // transient HRESULT) stays, with its gate closed, until a later change of the handlers disconnects it: giving it up
    // would leave the sink to the server, and with it this object and its source. Any other result ends the connection,
    // also the exception of a connection point that is a managed object, which counts as its HRESULT.
    private void Disconnect(Connection[] retired)
    {
        foreach (Connection connection in retired)
        {
            connection.Established!.Disconnect();
            lock (_lock)
            {
                if (!connection.Established!.IsConnected)
                {
                    _connections.Remove(connection);
                }
            }
        }
    }

    // Removes the last occurrence of a handler of an event, like the removal of a .NET event: equal delegates are
    // interchangeable. The caller holds the lock.
    private void RemoveHandler(EventMember member, Delegate handler)
    {
        if (_handlers.TryGetValue(member.DispId, out var handlers))
        {
            for (int i = handlers.Count - 1; i >= 0; i--)
            {
                if (handlers[i].Handler.Equals(handler))
                {
                    handlers[i].IsRemoved = true;
                    handlers.RemoveAt(i);
                    break;
                }
            }

            RemoveIfEmpty(member.DispId, handlers);
        }
    }

    // Removes the entry of an addition, if it is still there; the caller holds the lock.
    private void RemoveSubscription(int dispId, Subscription subscription)
    {
        subscription.IsRemoved = true;
        if (_handlers.TryGetValue(dispId, out var handlers) && handlers.Remove(subscription))
        {
            RemoveIfEmpty(dispId, handlers);
        }
    }

    // An event without handlers has no entry, so that the count of the entries tells whether the sink has handlers.
    private void RemoveIfEmpty(int dispId, List<Subscription> handlers)
    {
        if (handlers.Count == 0)
        {
            _handlers.Remove(dispId);
        }
    }

    // Passes the arguments converted to the types of the parameters, and returns the new values of the by-reference
    // parameters and the result.
    private static void Invoke(Delegate handler, EventArguments arguments)
    {
        HandlerSignature signature = GetSignature(handler);
        var values = new object?[signature.Types.Length];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = DynamicDispatchObject.ConvertTo(arguments.GetValue(i), signature.Types[i]);
        }

        object? result;
        try
        {
            result = handler.DynamicInvoke(values);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(e.InnerException);
            throw;
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (signature.IsByRef[i])
            {
                arguments.Set(i, values[i]);
            }
        }

        if (signature.ReturnsValue)
        {
            arguments.SetResult(result);
        }
    }

    private static HandlerSignature GetSignature(Delegate handler) => Signatures.GetValue(handler.GetType(), HandlerSignature.Create);

    // Adds a handler of an event, and returns the entry of the addition; the caller holds the lock.
    private Subscription AddHandler(EventMember member, Delegate handler)
    {
        if (!_handlers.TryGetValue(member.DispId, out var handlers))
        {
            _handlers.Add(member.DispId, handlers = []);
        }

        var subscription = new Subscription(handler);
        handlers.Add(subscription);
        return subscription;
    }

    /// <summary>
    /// The entry of an addition of a handler, which is compared by reference, and which an event that is already running
    /// skips once it is removed.
    /// </summary>
    private sealed class Subscription(Delegate handler)
    {
        public volatile bool IsRemoved;

        public Delegate Handler { get; } = handler;
    }

    /// <summary>
    /// The parameters of the <c>Invoke</c> method of a handler delegate type: the types that the arguments are converted to,
    /// the element types for <c>ref</c> and <c>out</c> parameters, which of them are passed by reference, and whether the
    /// handler returns a value.
    /// </summary>
    private sealed record HandlerSignature(Type[] Types, bool[] IsByRef, bool ReturnsValue)
    {
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The Invoke method of a delegate type is kept with the type.")]
        public static HandlerSignature Create(Type delegateType)
        {
            MethodInfo method = delegateType.GetMethod("Invoke")!;
            ParameterInfo[] parameters = method.GetParameters();
            var types = new Type[parameters.Length];
            var isByRef = new bool[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                isByRef[i] = type.IsByRef;
                types[i] = type.IsByRef ? type.GetElementType()! : type;
            }

            return new HandlerSignature(types, isByRef, method.ReturnType != typeof(void));
        }
    }

    /// <summary>
    /// A connection of the sink to the COM object, with a sink object of its own, through which its events arrive.
    /// </summary>
    private sealed class Connection(DynamicEventSink owner) : IDispatchEventHandler
    {
        public ConnectionState State { get; set; }

        /// <summary>Gets or sets the connection, once it is established.</summary>
        public EventConnection? Established { get; set; }

        public bool Invoke(int dispId, EventArguments arguments) => owner.Invoke(this, dispId, arguments);
    }
}
