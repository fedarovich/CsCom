using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Gets the COM interface pointers of managed objects.
/// </summary>
internal static unsafe class ComObjects
{
    /// <summary>
    /// Gets an AddRef'd <c>IUnknown</c> pointer for a COM object, a late-bound wrapper or a managed object, or 0 for <see langword="null"/>.
    /// </summary>
    public static nint GetUnknown(object? value)
    {
        if (value is DispatchObject wrapper)
        {
            value = wrapper.Dispatch;
        }

        if (value is null)
        {
            return 0;
        }

        return ComWrappers.TryGetComInstance(value, out nint unknown)
            ? unknown
            : (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(value);
    }

    /// <summary>
    /// Gets an AddRef'd <c>IDispatch</c> pointer for an object, or 0 for <see langword="null"/>.
    /// </summary>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    public static nint GetDispatch(object? value)
    {
        nint unknown = GetUnknown(value);
        if (unknown == 0)
        {
            return 0;
        }

        nint dispatch = QueryDispatch(unknown, out bool isDispatch);
        if (!isDispatch)
        {
            Marshal.Release(dispatch);
            throw new InvalidCastException($"The object of type {value!.GetType()} does not implement IDispatch.");
        }

        return dispatch;
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
