using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Gets the <c>IDispatch</c> pointers of objects; <see cref="ComMarshalling.ToUnmanaged{T}(T)"/> gets their <c>IUnknown</c> pointers.
/// </summary>
internal static unsafe class ComObjects
{
    /// <summary>
    /// Gets an AddRef'd <c>IDispatch</c> pointer for a COM object, a late-bound or <c>dynamic</c> wrapper or a managed
    /// object, or 0 for <see langword="null"/>, by the rule of <see cref="IDispatchProvider"/>: a managed object that is
    /// exposed to COM is passed itself, unless it does not implement <c>IDispatch</c> and implements
    /// <see cref="IDispatchProvider"/>: its <see cref="IDispatchProvider.Dispatch"/> is passed then. An
    /// <see cref="UnknownWrapper"/> stands for its object (see <see cref="ComMarshalling.QueryInterface"/>).
    /// </summary>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    /// <exception cref="ArgumentException">The object is a <see cref="DispatchWrapper"/>.</exception>
    /// <exception cref="COMException">Asking the object for <c>IDispatch</c> failed with another error.</exception>
    public static nint GetDispatch(object? value)
    {
        // An UnknownWrapper stands for its object, as in QueryInterface below, which is unwrapped first, so that the
        // exception names that object, and a COM object in it takes the fast path; one of null stands for no object.
        value = ComMarshalling.UnwrapUnknownWrapper(value, nameof(value));
        switch (value)
        {
            case null:
                return 0;

            // A COM object passes the IDispatch that it keeps, which the type test asks it for only once: QueryInterface
            // would ask it on every call, through the proxy manager when it is in another apartment. The type test says no
            // also when the strategy of a custom ComWrappers has no details of IDispatch, so the object itself is asked
            // below then, like any other object: where an IDispatch is required, this costs a QueryInterface only for an
            // object that would be rejected, unlike in a VARIANT.
            case ComObject when value is IDispatch dispatch:
                return (nint)ComMarshalling.ToUnmanaged(dispatch);
        }

        // A late-bound wrapper passes its COM object the same way: it is converted itself, not unwrapped again, also when
        // it is a managed object that wraps another one, or provides itself.
        if (ComMarshalling.IsLateBoundWrapper(value, out IDispatch? wrapped))
        {
            return (nint)ComMarshalling.ToUnmanaged(wrapped, unwrap: false);
        }

        // An object of the application that is exposed to COM without IDispatch, e.g. a decorator of a COM object, provides
        // the IDispatch that it lacks, like the source of events: where an IDispatch is required, its Dispatch is passed
        // rather than nothing. Another error than E_NOINTERFACE, e.g. of a disconnected proxy, throws the exception of its
        // HRESULT, as the cast of a COM object to IDispatch does.
        int hr = ComMarshalling.QueryInterface(value, OleNativeMethods.IID_IDispatch, out nint pointer);
        if (hr >= 0)
        {
            return pointer;
        }

        throw hr == OleNativeMethods.E_NOINTERFACE
            ? new InvalidCastException($"The object of type {value.GetType()} does not implement IDispatch.", hr)
            : Marshal.GetExceptionForHR(hr)!;
    }

    /// <summary>
    /// Returns the <c>IDispatch</c> pointer of the object if it implements <c>IDispatch</c>, otherwise the <c>IUnknown</c> pointer.
    /// Takes ownership of the <paramref name="unknown"/> reference.
    /// </summary>
    public static nint QueryDispatch(nint unknown, out bool isDispatch)
    {
        if (Marshal.QueryInterface(unknown, OleNativeMethods.IID_IDispatch, out nint dispatch) == OleNativeMethods.S_OK)
        {
            Marshal.Release(unknown);
            isDispatch = true;
            return dispatch;
        }

        isDispatch = false;
        return unknown;
    }
}
