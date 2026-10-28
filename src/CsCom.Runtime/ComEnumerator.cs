using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Adapts an <see cref="IEnumVARIANT"/> to <see cref="IEnumerator{T}"/>, so that COM collections can be used in <c>foreach</c> loops.
/// </summary>
public sealed unsafe class ComEnumerator : IEnumerator<object?>
{
    private readonly IEnumVARIANT _enumerator;
    private object? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComEnumerator"/> class.
    /// </summary>
    /// <param name="enumerator">The COM enumerator.</param>
    public ComEnumerator(IEnumVARIANT enumerator)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        _enumerator = enumerator;
    }

    /// <summary>
    /// Creates an enumerator from the object returned by a <c>_NewEnum</c> member.
    /// </summary>
    /// <param name="enumerator">A COM object that implements <c>IEnumVARIANT</c>.</param>
    public static ComEnumerator FromObject(object? enumerator)
    {
        return enumerator switch
        {
            null => throw new InvalidOperationException("The collection did not return an enumerator."),
            IEnumVARIANT enumVariant => new ComEnumerator(enumVariant),
            _ => throw new InvalidCastException($"The enumerator of type {enumerator.GetType()} does not implement IEnumVARIANT."),
        };
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
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
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
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(_enumerator.Reset());
        _current = null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    object? IEnumerator.Current => _current;
}
