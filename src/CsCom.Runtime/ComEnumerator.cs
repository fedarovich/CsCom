using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Adapts an <see cref="IEnumVARIANT"/> to <see cref="IEnumerator{T}"/>, so that COM collections can be used in <c>foreach</c> loops.
/// </summary>
public sealed unsafe class ComEnumerator : IEnumerator<object?>
{
    private readonly IEnumVARIANT _enumerator;
    private readonly bool _ownsEnumerator;
    private object? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComEnumerator"/> class, which does not own the COM enumerator:
    /// <see cref="Dispose"/> does not release it.
    /// </summary>
    /// <param name="enumerator">The COM enumerator.</param>
    public ComEnumerator(IEnumVARIANT enumerator)
        : this(enumerator, ownsEnumerator: false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ComEnumerator"/> class.
    /// </summary>
    /// <param name="enumerator">The COM enumerator.</param>
    /// <param name="ownsEnumerator">
    /// Whether <see cref="Dispose"/> releases the COM enumerator at once if it is a unique object, which must not be used
    /// afterwards.
    /// </param>
    public ComEnumerator(IEnumVARIANT enumerator, bool ownsEnumerator)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        _enumerator = enumerator;
        _ownsEnumerator = ownsEnumerator;
    }

    /// <summary>
    /// Creates an enumerator from an object that implements <c>IEnumVARIANT</c>, which it does not own:
    /// <see cref="Dispose"/> does not release it.
    /// </summary>
    /// <param name="enumerator">
    /// A COM object that implements <c>IEnumVARIANT</c>, or an object that stands for one, see
    /// <see cref="FromObject(object, bool)"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">The object is <see langword="null"/> or stands for none.</exception>
    /// <exception cref="InvalidCastException">The object does not implement <c>IEnumVARIANT</c>.</exception>
    /// <exception cref="ArgumentException">The object is a <see cref="DispatchWrapper"/>.</exception>
    /// <exception cref="ObjectDisposedException">The object is, or stands for, a released COM object.</exception>
    /// <exception cref="COMException">
    /// Asking the COM object for <c>IEnumVARIANT</c> failed with another error, e.g. of a server that has ended.
    /// </exception>
    public static ComEnumerator FromObject(object? enumerator) => FromObject(enumerator, ownsEnumerator: false);

    /// <summary>
    /// Creates an enumerator from an object that implements <c>IEnumVARIANT</c>.
    /// </summary>
    /// <param name="enumerator">
    /// A COM object that implements <c>IEnumVARIANT</c>, or an object that stands for one by the rule of
    /// <see cref="IDispatchProvider"/>: a late-bound or <c>dynamic</c> wrapper stands for the COM object of its
    /// <see cref="IDispatchProvider.Dispatch"/>, and for no object when that is <see langword="null"/>; an object of the
    /// application that is exposed to COM (see <see cref="ComMarshalling.IsExposedToCom"/>) is enumerated itself, or
    /// through the <see cref="IDispatchProvider.Dispatch"/> that it provides when it does not implement
    /// <c>IEnumVARIANT</c>, also when a late-bound wrapper wraps it. An <see cref="UnknownWrapper"/> stands for its object,
    /// and a <see cref="DispatchWrapper"/> is rejected, as where an object is passed to COM.
    /// </param>
    /// <param name="ownsEnumerator">
    /// Whether the object is released at once if it is a unique object, like by <see cref="ComMarshalling.Release"/>: by
    /// <see cref="Dispose"/>, or by this method when the object does not implement <c>IEnumVARIANT</c>. The COM object of
    /// a late-bound wrapper is released, and an object of the application that is exposed to COM is left alone, also the
    /// object that it provides and when a wrapper wraps it. The released object must not be used afterwards.
    /// </param>
    /// <exception cref="InvalidOperationException">The object is <see langword="null"/> or stands for none.</exception>
    /// <exception cref="InvalidCastException">The object does not implement <c>IEnumVARIANT</c>.</exception>
    /// <exception cref="ArgumentException">The object is a <see cref="DispatchWrapper"/>.</exception>
    /// <exception cref="ObjectDisposedException">The object is, or stands for, a released COM object.</exception>
    /// <exception cref="COMException">
    /// Asking the COM object for <c>IEnumVARIANT</c> failed with another error, e.g. of a server that has ended.
    /// </exception>
    public static ComEnumerator FromObject(object? enumerator, bool ownsEnumerator)
    {
        // An UnknownWrapper stands for its object, as where an object is passed to COM; the message names that object.
        object? source = ComMarshalling.UnwrapUnknownWrapper(enumerator, nameof(enumerator));
        object? value = Resolve(source, ref ownsEnumerator);
        if (value is null)
        {
            throw new InvalidOperationException("The collection did not return an enumerator.");
        }

        // When the object is no enumerator, no ComEnumerator is created that could be disposed to release an object that is
        // owned, so it is released here: also when asking it for the interface throws, e.g. when it has been released, or
        // when the strategy of ComMarshalling.ComWrappers fails.
        IEnumVARIANT enumVariant;
        try
        {
            enumVariant = value as IEnumVARIANT ?? throw NoEnumerator(value, source!);
        }
        catch
        {
            if (ownsEnumerator)
            {
                ComMarshalling.ReleaseObject(value);
            }

            throw;
        }

        return new ComEnumerator(enumVariant, ownsEnumerator);
    }

    // The exception of an object that is no enumerator. The type test of a COM object does not tell why the object did not
    // answer, so it is asked again, only on this path: another error than E_NOINTERFACE, e.g. of a disconnected proxy,
    // throws the exception of its HRESULT, as in ComObjects.GetDispatch. An object that answers, whose strategy does not
    // know IEnumVARIANT, cannot be called through it either.
    private static Exception NoEnumerator(object value, object source)
    {
        if (value is ComObject)
        {
            int hr = ComMarshalling.QueryInterface(value, OleNativeMethods.IID_IEnumVARIANT, out nint pointer);
            if (hr >= 0)
            {
                Marshal.Release(pointer);
            }
            else if (hr != OleNativeMethods.E_NOINTERFACE)
            {
                return Marshal.GetExceptionForHR(hr)!;
            }
        }

        return new InvalidCastException($"The enumerator of type {source.GetType()} does not implement IEnumVARIANT.");
    }

    // The object that a value stands for by the rule of IDispatchProvider, like the source of events, and whether it is
    // owned, like by ComMarshalling.Release. A late-bound wrapper is no enumerator: its COM object is, which a pointer that
    // a collection returns, e.g. the one of a unique _NewEnum, is converted to for the wrapper that the marshaller creates.
    // An object of the application that is exposed to COM is the one that a pointer can be converted back to: it is never
    // owned, neither is the object that it provides, also when a late-bound wrapper wraps it, e.g. the one that a
    // dispinterface wrapper makes of a decorator that a call returns, as ComMarshalling.Release leaves it alone.
    private static object? Resolve(object? value, ref bool owned)
    {
        // A COM object is no wrapper, and is not tested for the interfaces of wrappers, which its strategy would look up on
        // every call.
        if (value is null or ComObject)
        {
            return value;
        }

        // The Dispatch of a wrapper follows the rule of what it is, but is not unwrapped again.
        if (ComMarshalling.IsLateBoundWrapper(value, out IDispatch? dispatch))
        {
            value = dispatch;
            if (value is null or ComObject)
            {
                return value;
            }
        }

        if (ComMarshalling.IsExposedToCom(value))
        {
            owned = false;
            if (value is not IEnumVARIANT && ComMarshalling.TryGetProvidedDispatch(value, out IDispatch? provided))
            {
                return provided;
            }
        }

        return value;
    }

    /// <inheritdoc/>
    public object? Current => _current;

    /// <inheritdoc/>
    public bool MoveNext()
    {
        ComVariant item = default;
        uint fetched = 0;
        int hr = _enumerator.Next(1, &item, &fetched);
        try
        {
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (hr != OleNativeMethods.S_OK || fetched == 0)
            {
                _current = null;
                return false;
            }

            _current = VariantMarshaller.ConvertToManaged(item);
            return true;
        }
        finally
        {
            item.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Marshal.ThrowExceptionForHR(_enumerator.Reset());
        _current = null;
    }

    /// <summary>
    /// Releases the COM enumerator at once if this enumerator owns it and it is a unique object, as the <c>unique</c>
    /// interface marshalling returns it from <c>_NewEnum</c>. A shared enumerator is released when it is garbage collected.
    /// Calling it again does nothing, also on another thread at the same time.
    /// </summary>
    public void Dispose()
    {
        _current = null;
        if (_ownsEnumerator)
        {
            // ComObject.FinalRelease releases the COM enumerator once, also when it is called again or on several threads
            // at the same time.
            ComMarshalling.ReleaseObject(_enumerator);
        }
    }

    object? IEnumerator.Current => _current;
}
