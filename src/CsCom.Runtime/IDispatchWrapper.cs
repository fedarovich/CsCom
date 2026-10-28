using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Implemented by the late-bound wrappers generated for dispinterfaces.
/// </summary>
/// <typeparam name="TSelf">The wrapper type.</typeparam>
public interface IDispatchWrapper<TSelf> where TSelf : class, IDispatchWrapper<TSelf>
{
    /// <summary>
    /// Gets the wrapped <see cref="IDispatch"/> interface.
    /// </summary>
    IDispatch Dispatch { get; }

    /// <summary>
    /// Creates a wrapper for the specified <see cref="IDispatch"/> interface.
    /// </summary>
    static abstract TSelf Create(IDispatch dispatch);
}

/// <summary>
/// Marshals late-bound dispinterface wrappers as <c>IDispatch</c> pointers.
/// </summary>
/// <typeparam name="T">The wrapper type.</typeparam>
[CustomMarshaller(typeof(CustomMarshallerAttribute.GenericPlaceholder), MarshalMode.Default, typeof(DispatchWrapperMarshaller<>))]
public static unsafe class DispatchWrapperMarshaller<T> where T : class, IDispatchWrapper<T>
{
    /// <summary>Converts a wrapper to an <c>IDispatch</c> pointer.</summary>
    public static void* ConvertToUnmanaged(T? managed)
    {
        return managed is null ? null : ComInterfaceMarshaller<IDispatch>.ConvertToUnmanaged(managed.Dispatch);
    }

    /// <summary>Converts an <c>IDispatch</c> pointer to a wrapper.</summary>
    public static T? ConvertToManaged(void* unmanaged)
    {
        IDispatch? dispatch = ComInterfaceMarshaller<IDispatch>.ConvertToManaged(unmanaged);
        return dispatch is null ? null : T.Create(dispatch);
    }

    /// <summary>Releases the <c>IDispatch</c> pointer.</summary>
    public static void Free(void* unmanaged)
    {
        ComInterfaceMarshaller<IDispatch>.Free(unmanaged);
    }
}
