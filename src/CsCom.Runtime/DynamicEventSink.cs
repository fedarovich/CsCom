using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace CsCom.Runtime;

/// <summary>
/// Receives the events of a source dispinterface of a COM object and passes them to the delegates added through
/// <c>dynamic</c> (<c>d.Event += handler</c>) or <see cref="ComDynamic.AddEventHandler"/>.
/// </summary>
/// <remarks>
/// The sink is connected when the first handler is added and disconnected when the last one is removed.
/// </remarks>
[RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
internal sealed class DynamicEventSink(object source, EventInterface eventInterface) : IDispatchEventHandler
{
    private readonly Dictionary<int, List<Delegate>> _handlers = [];
    private EventConnection? _connection;

    /// <summary>
    /// Adds a handler of an event, connecting the sink if it is not connected yet.
    /// </summary>
    /// <exception cref="ArgumentException">The handler has more parameters than the event.</exception>
    public void Add(EventMember member, Delegate handler)
    {
        int count = GetInvokeMethod(handler).GetParameters().Length;
        if (count > member.ParameterCount)
        {
            throw new ArgumentException(
                $"The handler has {count} parameters, but the event '{member.Name}' has {member.ParameterCount}.", nameof(handler));
        }

        lock (_handlers)
        {
            if (_connection is not null)
            {
                AddHandler(member, handler);
                return;
            }
        }

        // The sink is connected outside the lock, so that events raised meanwhile on other threads do not wait for it.
        EventConnection? connection = EventConnection.AdviseDispatch(source, eventInterface.Iid, this);
        lock (_handlers)
        {
            if (_connection is null)
            {
                _connection = connection;
                connection = null;
            }

            AddHandler(member, handler);
        }

        // Another thread has connected the sink meanwhile.
        connection?.Dispose();
    }

    /// <summary>
    /// Removes the last occurrence of a handler of an event, disconnecting the sink when no handlers remain.
    /// </summary>
    public void Remove(EventMember member, Delegate handler)
    {
        EventConnection? connection = null;
        lock (_handlers)
        {
            if (_handlers.TryGetValue(member.DispId, out var handlers))
            {
                int index = handlers.LastIndexOf(handler);
                if (index >= 0)
                {
                    handlers.RemoveAt(index);
                }

                if (handlers.Count == 0)
                {
                    _handlers.Remove(member.DispId);
                }
            }

            if (_handlers.Count == 0)
            {
                connection = _connection;
                _connection = null;
            }
        }

        connection?.Dispose();
    }

    /// <summary>
    /// Removes all handlers and disconnects the sink.
    /// </summary>
    public void RemoveAll()
    {
        EventConnection? connection;
        lock (_handlers)
        {
            _handlers.Clear();
            connection = _connection;
            _connection = null;
        }

        connection?.Dispose();
    }

    /// <inheritdoc/>
    public bool Invoke(int dispId, EventArguments arguments)
    {
        Delegate[] handlers;
        lock (_handlers)
        {
            handlers = _handlers.TryGetValue(dispId, out var list) ? [.. list] : [];
        }

        if (handlers.Length == 0)
        {
            return eventInterface.DispIds.Contains(dispId);
        }

        foreach (Delegate handler in handlers)
        {
            foreach (Delegate single in handler.GetInvocationList())
            {
                Invoke(single, arguments);
            }
        }

        return true;
    }

    // Passes the arguments converted to the types of the parameters, and returns the new values of the by-reference
    // parameters and the result.
    private static void Invoke(Delegate handler, EventArguments arguments)
    {
        MethodInfo method = GetInvokeMethod(handler);
        ParameterInfo[] parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            values[i] = DynamicDispatchObject.ConvertTo(arguments.GetValue(i), type.IsByRef ? type.GetElementType()! : type);
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

        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].ParameterType.IsByRef)
            {
                arguments.Set(i, values[i]);
            }
        }

        if (method.ReturnType != typeof(void))
        {
            arguments.SetResult(result);
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The Invoke method of a delegate type is kept with the type.")]
    private static MethodInfo GetInvokeMethod(Delegate handler) => handler.GetType().GetMethod("Invoke")!;

    private void AddHandler(EventMember member, Delegate handler)
    {
        if (!_handlers.TryGetValue(member.DispId, out var handlers))
        {
            _handlers.Add(member.DispId, handlers = []);
        }

        handlers.Add(handler);
    }
}
