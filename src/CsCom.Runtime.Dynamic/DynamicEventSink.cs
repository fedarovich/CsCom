using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    // The signatures of the handlers by delegate type, read once rather than for each event. The table does not keep the
    // types of collectible assemblies alive.
    private static readonly ConditionalWeakTable<Type, HandlerSignature> Signatures = new();

    private readonly Dictionary<int, List<Delegate>> _handlers = [];
    private EventConnection? _connection;

    /// <summary>
    /// Adds a handler of an event, connecting the sink if it is not connected yet.
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

    private void AddHandler(EventMember member, Delegate handler)
    {
        if (!_handlers.TryGetValue(member.DispId, out var handlers))
        {
            _handlers.Add(member.DispId, handlers = []);
        }

        handlers.Add(handler);
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
}
