using System;

namespace CsCom.Runtime;

/// <summary>
/// The base class of the late-bound wrappers generated for dispinterfaces.
/// </summary>
public abstract class DispatchObject : IDispatchProvider
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchObject"/> class.
    /// </summary>
    /// <param name="dispatch">The wrapped <see cref="IDispatch"/> interface.</param>
    protected DispatchObject(IDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        Dispatch = dispatch;
    }

    /// <summary>
    /// Gets the wrapped <see cref="IDispatch"/> interface.
    /// </summary>
    public IDispatch Dispatch { get; }

    /// <summary>Invokes a method.</summary>
    protected object? InvokeMethod(int dispId, params ReadOnlySpan<object?> args)
    {
        return DispatchInvoker.Invoke(Dispatch, dispId, DispatchFlags.Method, args);
    }

    /// <summary>Invokes a method that has by-reference parameters. The values of by-reference arguments are updated in place.</summary>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="byRef">For each argument, whether it is passed by reference.</param>
    protected object? InvokeMethodByRef(int dispId, Span<object?> args, ReadOnlySpan<bool> byRef)
    {
        return DispatchInvoker.Invoke(Dispatch, dispId, DispatchFlags.Method, args, output: args, byRef: byRef);
    }

    /// <summary>Gets the value of a property.</summary>
    protected object? InvokeGet(int dispId, params ReadOnlySpan<object?> args)
    {
        return DispatchInvoker.Invoke(Dispatch, dispId, DispatchFlags.PropertyGet | DispatchFlags.Method, args);
    }

    /// <summary>Sets the value of a property.</summary>
    protected void InvokePut(int dispId, object? value, params ReadOnlySpan<object?> args)
    {
        InvokeSetter(dispId, DispatchFlags.PropertyPut, value, args);
    }

    /// <summary>Sets the value of a property by reference.</summary>
    protected void InvokePutRef(int dispId, object? value, params ReadOnlySpan<object?> args)
    {
        InvokeSetter(dispId, DispatchFlags.PropertyPutRef, value, args);
    }

    /// <summary>Converts an invocation result to a late-bound wrapper.</summary>
    protected static T? ToWrapper<T>(object? value) where T : class, IDispatchWrapper<T>
    {
        return ComConvert.ToWrapper<T>(value);
    }

    /// <summary>Converts an invocation result to a COM interface.</summary>
    protected static T? ToInterface<T>(object? value) where T : class
    {
        return ComConvert.ToInterface<T>(value);
    }

    /// <summary>Converts an invocation result to a zero-based two-dimensional array of <typeparamref name="T"/>.</summary>
    protected static T[,]? ToArray2D<T>(object? value)
    {
        return ComConvert.ToArray2D<T>(value);
    }

    /// <summary>Converts an invocation result to a zero-based three-dimensional array of <typeparamref name="T"/>.</summary>
    protected static T[,,]? ToArray3D<T>(object? value)
    {
        return ComConvert.ToArray3D<T>(value);
    }

    /// <summary>Gets an enumerator for a collection that exposes a <c>_NewEnum</c> member.</summary>
    protected ComEnumerator GetEnumeratorCore()
    {
        object? enumerator = DispatchInvoker.Invoke(Dispatch, DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet, default);
        return ComEnumerator.FromObject(enumerator);
    }

    private void InvokeSetter(int dispId, DispatchFlags flags, object? value, ReadOnlySpan<object?> args)
    {
        if (args.IsEmpty)
        {
            DispatchInvoker.Invoke(Dispatch, dispId, flags, [value]);
            return;
        }

        var allArgs = new object?[args.Length + 1];
        args.CopyTo(allArgs);
        allArgs[^1] = value;
        DispatchInvoker.Invoke(Dispatch, dispId, flags, allArgs);
    }

    /// <summary>
    /// Invokes a member of an <see cref="IDispatch"/> interface.
    /// </summary>
    /// <param name="dispatch">The interface to invoke the member on.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="args">The arguments, in the declaration order.</param>
    /// <returns>The result of the invocation.</returns>
    public static object? Invoke(IDispatch dispatch, int dispId, DispatchFlags flags, params ReadOnlySpan<object?> args)
    {
        return DispatchInvoker.Invoke(dispatch, dispId, flags, args);
    }

    /// <summary>
    /// Invokes a member of an <see cref="IDispatch"/> interface that has by-reference parameters.
    /// </summary>
    /// <param name="dispatch">The interface to invoke the member on.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="args">The arguments, in the declaration order. By-reference arguments are updated in place.</param>
    /// <param name="byRef">For each argument, whether it is passed by reference.</param>
    /// <returns>The result of the invocation.</returns>
    public static object? InvokeByRef(IDispatch dispatch, int dispId, DispatchFlags flags, Span<object?> args, ReadOnlySpan<bool> byRef)
    {
        return DispatchInvoker.Invoke(dispatch, dispId, flags, args, output: args, byRef: byRef);
    }
}
