using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Implemented by the late-bound wrappers generated for dispinterfaces.
/// </summary>
/// <remarks>
/// A wrapper is an <see cref="IDispatchProvider"/>, also one of your own: the support code passes the COM object of its
/// <see cref="IDispatchProvider.Dispatch"/> instead of the wrapper, and <see cref="ComMarshalling.Release"/> releases that
/// object, unless the wrapper is exposed to COM itself (<see cref="ComMarshalling.IsExposedToCom"/>). Such a wrapper is
/// passed itself where it implements <c>IDispatch</c>, so that its pointer converts back to it, and its
/// <see cref="IDispatchProvider.Dispatch"/> otherwise.
/// </remarks>
/// <typeparam name="TSelf">The wrapper type.</typeparam>
public interface IDispatchWrapper<TSelf> : IDispatchProvider where TSelf : class, IDispatchWrapper<TSelf>
{
    /// <summary>
    /// Creates a wrapper for the specified <see cref="IDispatch"/> interface.
    /// </summary>
    static abstract TSelf Create(IDispatch dispatch);
}

/// <summary>
/// Marshals late-bound dispinterface wrappers as <c>IDispatch</c> pointers, like <see cref="ComObjectMarshaller{T}"/>.
/// </summary>
/// <typeparam name="T">The wrapper type.</typeparam>
[CustomMarshaller(typeof(CustomMarshallerAttribute.GenericPlaceholder), MarshalMode.Default, typeof(DispatchWrapperMarshaller<>))]
public static unsafe class DispatchWrapperMarshaller<T> where T : class, IDispatchWrapper<T>
{
    /// <summary>Converts a wrapper to an <c>IDispatch</c> pointer.</summary>
    public static void* ConvertToUnmanaged(T? managed)
    {
        // Where an IDispatch is required (see IDispatchProvider): a generated wrapper, which is not exposed to COM, passes
        // the cached pointer of its object, and a wrapper of the application that is exposed to COM is passed itself where
        // it implements IDispatch, like in a VARIANT, and its Dispatch otherwise.
        return (void*)ComObjects.GetDispatch(managed);
    }

    /// <summary>Converts an <c>IDispatch</c> pointer to a wrapper.</summary>
    public static T? ConvertToManaged(void* unmanaged)
    {
        IDispatch? dispatch = ComObjectMarshaller<IDispatch>.ConvertToManaged(unmanaged);
        return dispatch is null ? null : Wrap(dispatch);
    }

    // Wraps an IDispatch, unless it is the wrapper itself: the pointer of a wrapper that is exposed to COM converts back to
    // it, like in ComConvert.ToWrapper.
    internal static T Wrap(IDispatch dispatch) => dispatch as T ?? T.Create(dispatch);

    /// <summary>Releases the <c>IDispatch</c> pointer.</summary>
    public static void Free(void* unmanaged)
    {
        ComObjectMarshaller<IDispatch>.Free(unmanaged);
    }
}

/// <summary>
/// Marshals late-bound dispinterface wrappers as <c>IDispatch</c> pointers, with a managed object of its own for every
/// pointer, like <see cref="UniqueComObjectMarshaller{T}"/>.
/// </summary>
/// <typeparam name="T">The wrapper type.</typeparam>
[CustomMarshaller(typeof(CustomMarshallerAttribute.GenericPlaceholder), MarshalMode.Default, typeof(UniqueDispatchWrapperMarshaller<>))]
public static unsafe class UniqueDispatchWrapperMarshaller<T> where T : class, IDispatchWrapper<T>
{
    /// <summary>Converts a wrapper to an <c>IDispatch</c> pointer.</summary>
    public static void* ConvertToUnmanaged(T? managed) => DispatchWrapperMarshaller<T>.ConvertToUnmanaged(managed);

    /// <summary>Converts an <c>IDispatch</c> pointer to a wrapper of a new managed object.</summary>
    public static T? ConvertToManaged(void* unmanaged)
    {
        IDispatch? dispatch = UniqueComObjectMarshaller<IDispatch>.ConvertToManaged(unmanaged);
        return dispatch is null ? null : CreateWrapper(dispatch);
    }

    // Creates the wrapper of a unique object that was created for it, and releases the object if the wrapper cannot be
    // created: nobody else has it. A wrapper that the pointer converts back to is returned itself.
    internal static T CreateWrapper(IDispatch dispatch)
    {
        try
        {
            return DispatchWrapperMarshaller<T>.Wrap(dispatch);
        }
        catch
        {
            ComMarshalling.ReleaseObject(dispatch);
            throw;
        }
    }

    /// <summary>Releases the <c>IDispatch</c> pointer.</summary>
    public static void Free(void* unmanaged) => DispatchWrapperMarshaller<T>.Free(unmanaged);
}
