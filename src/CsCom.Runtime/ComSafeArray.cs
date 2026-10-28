using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace CsCom.Runtime;

/// <summary>
/// Implemented by the wrappers of <c>SAFEARRAY</c> pointers, so that they can be passed in <c>VARIANT</c>s.
/// </summary>
internal interface ISafeArrayProvider
{
    /// <summary>Gets the <c>SAFEARRAY</c> pointer.</summary>
    nint Pointer { get; }
}

/// <summary>
/// A <c>SAFEARRAY</c> of any rank whose elements are accessed as <typeparamref name="T"/>. Unlike the .NET arrays converted
/// from <c>SAFEARRAY</c>s, it keeps the bounds of the native array, e.g. 1-based arrays created by Visual Basic or Excel.
/// </summary>
/// <remarks>
/// <para>
/// An instance that owns its <c>SAFEARRAY</c> destroys it when it is disposed or finalized; a view of a <c>SAFEARRAY</c>
/// owned by someone else does not. When it is passed to COM methods, the ownership follows the COM rules: <c>[in]</c>
/// arrays stay owned by the caller, returned arrays are owned by the receiver, and an array replaced by the callee of an
/// <c>[in, out]</c> parameter is detached. The arrays received by COM objects implemented in .NET are views that are valid
/// during the call; an owned array returned by them is detached and its ownership is transferred to the caller.
/// </para>
/// <para>
/// The elements are converted to and from <typeparamref name="T"/> on each access. The indices follow the order of
/// the <c>SAFEARRAY</c> indices, e.g. <c>(row, column)</c> for Excel ranges, and the dimensions are numbered from 0.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of the elements.</typeparam>
[NativeMarshalling(typeof(ComSafeArrayMarshaller<>))]
public sealed unsafe class ComSafeArray<T> : IDisposable, ISafeArrayProvider
{
    private nint _pointer;

    private ComSafeArray(nint pointer, bool ownsArray)
    {
        _pointer = pointer;
        OwnsArray = ownsArray;
        if (!ownsArray)
        {
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Destroys the <c>SAFEARRAY</c> if it is owned and was not disposed.</summary>
    ~ComSafeArray()
    {
        Release();
    }

    /// <summary>Gets the <c>SAFEARRAY</c> pointer.</summary>
    /// <exception cref="ObjectDisposedException">The instance was disposed or detached.</exception>
    public nint Pointer
    {
        get
        {
            nint pointer = Volatile.Read(ref _pointer);
            ObjectDisposedException.ThrowIf(pointer == 0, this);
            return pointer;
        }
    }

    /// <summary>Gets a value indicating whether the instance destroys the <c>SAFEARRAY</c> when it is disposed.</summary>
    public bool OwnsArray { get; private set; }

    /// <summary>Gets the variant type of the elements.</summary>
    public VarEnum ElementType
    {
        get
        {
            Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetVartype(Pointer, out ushort vt));
            return (VarEnum)vt;
        }
    }

    /// <summary>Gets the number of dimensions.</summary>
    public int Rank => (int)OleNativeMethods.SafeArrayGetDim(Pointer);

    /// <summary>Gets the total number of elements.</summary>
    public int Length
    {
        get
        {
            int length = 1;
            foreach (int dimension in SafeArrayConverter.GetLengths(Pointer))
            {
                length *= dimension;
            }

            return length;
        }
    }

    /// <summary>Gets or sets an element of a one-dimensional array.</summary>
    /// <param name="index">The index, within the bounds of the array.</param>
    public T this[int index]
    {
        get => GetValue(index);
        set => SetValue(value, index);
    }

    /// <summary>Gets or sets an element of a two-dimensional array.</summary>
    /// <param name="index0">The index of the first dimension, within its bounds.</param>
    /// <param name="index1">The index of the second dimension, within its bounds.</param>
    public T this[int index0, int index1]
    {
        get => GetValue(index0, index1);
        set => SetValue(value, index0, index1);
    }

    /// <summary>Gets or sets an element of a three-dimensional array.</summary>
    /// <param name="index0">The index of the first dimension, within its bounds.</param>
    /// <param name="index1">The index of the second dimension, within its bounds.</param>
    /// <param name="index2">The index of the third dimension, within its bounds.</param>
    public T this[int index0, int index1, int index2]
    {
        get => GetValue(index0, index1, index2);
        set => SetValue(value, index0, index1, index2);
    }

    /// <summary>Gets or sets an element of a multidimensional array.</summary>
    /// <param name="indices">The indices of the dimensions, within their bounds.</param>
    public T this[params ReadOnlySpan<int> indices]
    {
        get => GetValue(indices);
        set => SetValue(value, indices);
    }

    /// <summary>Creates a one-dimensional <c>SAFEARRAY</c> with the elements of an array; the element type is derived from <typeparamref name="T"/>.</summary>
    public static ComSafeArray<T> Create(T[] values) => FromArray(values);

    /// <summary>Creates a two-dimensional <c>SAFEARRAY</c> with the bounds and the elements of an array; the element type is derived from <typeparamref name="T"/>.</summary>
    public static ComSafeArray<T> Create(T[,] values) => FromArray(values);

    /// <summary>Creates a three-dimensional <c>SAFEARRAY</c> with the bounds and the elements of an array; the element type is derived from <typeparamref name="T"/>.</summary>
    public static ComSafeArray<T> Create(T[,,] values) => FromArray(values);

    /// <summary>
    /// Creates a <c>SAFEARRAY</c> whose elements are zero, empty or <see langword="null"/>.
    /// </summary>
    /// <param name="elementType">The variant type of the elements, e.g. <c>VT_UNKNOWN</c> for <see cref="object"/> elements that are interface pointers.</param>
    /// <param name="lengths">The number of elements of each dimension.</param>
    /// <param name="lowerBounds">The lower bounds of the dimensions; zero when empty.</param>
    public static ComSafeArray<T> Create(VarEnum elementType, ReadOnlySpan<int> lengths, ReadOnlySpan<int> lowerBounds = default) =>
        new(SafeArrayConverter.Create(elementType, lengths, lowerBounds), ownsArray: true);

    /// <summary>
    /// Wraps a <c>SAFEARRAY</c> pointer, e.g. a <c>SAFEARRAY</c> field of a structure.
    /// </summary>
    /// <param name="pointer">The <c>SAFEARRAY</c> pointer.</param>
    /// <param name="ownsArray">Whether the instance destroys the <c>SAFEARRAY</c> when it is disposed.</param>
    /// <returns>The wrapper, or <see langword="null"/> if <paramref name="pointer"/> is 0.</returns>
    public static ComSafeArray<T>? FromPointer(nint pointer, bool ownsArray) => pointer == 0 ? null : new(pointer, ownsArray);

    /// <summary>Gets the number of elements of a dimension.</summary>
    /// <param name="dimension">The zero-based dimension.</param>
    public int GetLength(int dimension) => GetUpperBound(dimension) - GetLowerBound(dimension) + 1;

    /// <summary>Gets the lower bound of a dimension.</summary>
    /// <param name="dimension">The zero-based dimension.</param>
    public int GetLowerBound(int dimension)
    {
        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetLBound(Pointer, CheckDimension(dimension), out int bound));
        return bound;
    }

    /// <summary>Gets the upper bound of a dimension.</summary>
    /// <param name="dimension">The zero-based dimension.</param>
    public int GetUpperBound(int dimension)
    {
        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetUBound(Pointer, CheckDimension(dimension), out int bound));
        return bound;
    }

    /// <summary>Gets an element.</summary>
    /// <param name="indices">The index of each dimension, within its bounds.</param>
    /// <exception cref="IndexOutOfRangeException">An index is outside of the bounds.</exception>
    public T GetValue(params ReadOnlySpan<int> indices)
    {
        nint pointer = Pointer;
        VarEnum vt = ElementType;
        return ComConvert.To<T>(Access(pointer, indices, vt, null, static (element, vt, _) => SafeArrayConverter.ReadElement((void*)element, vt)));
    }

    /// <summary>Sets an element, converting the value to the element type of the <c>SAFEARRAY</c>.</summary>
    /// <param name="value">The new value.</param>
    /// <param name="indices">The index of each dimension, within its bounds.</param>
    /// <exception cref="IndexOutOfRangeException">An index is outside of the bounds.</exception>
    public void SetValue(T value, params ReadOnlySpan<int> indices)
    {
        nint pointer = Pointer;
        VarEnum vt = ElementType;
        Access(pointer, indices, vt, value, static (element, vt, value) =>
        {
            SafeArrayConverter.WriteElement((void*)element, vt, value);
            return null;
        });
    }

    /// <summary>
    /// Gets the elements of a one-dimensional array in the memory of the <c>SAFEARRAY</c>, without converting them.
    /// </summary>
    /// <remarks>
    /// The element at index 0 of the span is at the lower bound of the array. <typeparamref name="T"/> must have the layout of
    /// the elements: the numeric type of the elements or an enum based on it, <see cref="decimal"/> for <c>VT_DECIMAL</c>,
    /// <see cref="ComBool"/> for <c>VT_BOOL</c>, <see cref="ComDate"/> for <c>VT_DATE</c> or <see cref="ComCurrency"/> for
    /// <c>VT_CY</c>. The span is valid until the instance is disposed or detached, and while the <c>SAFEARRAY</c> is not
    /// resized or destroyed by its owner.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The array is not one-dimensional, or its elements cannot be accessed as <typeparamref name="T"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The instance was disposed or detached.</exception>
    public Span<T> AsSpan()
    {
        nint pointer = Pointer;
        CheckRank(pointer, 1);
        VarEnum vt = ElementType;
        if (!HasLayoutOf(vt))
        {
            throw new InvalidOperationException($"The elements of the SAFEARRAY are {vt}, which cannot be accessed as a span of {typeof(T)}.");
        }

        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayAccessData(pointer, out void* data));
        OleNativeMethods.SafeArrayUnaccessData(pointer);
        return new Span<T>(data, GetLength(0));
    }

    /// <summary>Copies the elements of a one-dimensional array to a zero-based .NET array.</summary>
    /// <exception cref="InvalidOperationException">The array is not one-dimensional.</exception>
    public T[] ToArray() => ComConvert.ToArray<T>(ToManagedArray(1))!;

    /// <summary>Copies the elements of a two-dimensional array to a zero-based .NET array.</summary>
    /// <exception cref="InvalidOperationException">The array is not two-dimensional.</exception>
    public T[,] ToArray2D() => ComConvert.ToArray2D<T>(ToManagedArray(2))!;

    /// <summary>Copies the elements of a three-dimensional array to a zero-based .NET array.</summary>
    /// <exception cref="InvalidOperationException">The array is not three-dimensional.</exception>
    public T[,,] ToArray3D() => ComConvert.ToArray3D<T>(ToManagedArray(3))!;

    /// <summary>
    /// Gives up the <c>SAFEARRAY</c> without destroying it, e.g. to return it to a COM caller. The instance cannot be used afterwards.
    /// </summary>
    /// <returns>The <c>SAFEARRAY</c> pointer, which is owned by the caller if the instance owned it.</returns>
    public nint Detach()
    {
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        ObjectDisposedException.ThrowIf(pointer == 0, this);
        OwnsArray = false;
        GC.SuppressFinalize(this);
        return pointer;
    }

    /// <summary>Destroys the <c>SAFEARRAY</c> if it is owned. The instance cannot be used afterwards.</summary>
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    nint ISafeArrayProvider.Pointer => Pointer;

    /// <summary>Gets the <c>SAFEARRAY</c> pointer, or 0 if the instance was disposed or detached.</summary>
    internal nint PointerOrZero => Volatile.Read(ref _pointer);

    // Gives up the SAFEARRAY without destroying it, because it was destroyed by someone else or the instance is a view
    // that is no longer valid.
    internal void Forget()
    {
        Volatile.Write(ref _pointer, 0);
        OwnsArray = false;
        GC.SuppressFinalize(this);
    }

    private static object? Access(nint pointer, ReadOnlySpan<int> indices, VarEnum vt, object? value, Func<nint, VarEnum, object?, object?> action)
    {
        if (indices.Length != OleNativeMethods.SafeArrayGetDim(pointer))
        {
            throw new ArgumentException($"The array has {OleNativeMethods.SafeArrayGetDim(pointer)} dimensions, but {indices.Length} indices were specified.", nameof(indices));
        }

        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayLock(pointer));
        try
        {
            fixed (int* pIndices = indices)
            {
                int hr = OleNativeMethods.SafeArrayPtrOfIndex(pointer, pIndices, out void* element);
                if (hr == OleNativeMethods.DISP_E_BADINDEX)
                {
                    throw new IndexOutOfRangeException("An index is outside of the bounds of the SAFEARRAY.");
                }

                Marshal.ThrowExceptionForHR(hr);
                return action((nint)element, vt, value);
            }
        }
        finally
        {
            OleNativeMethods.SafeArrayUnlock(pointer);
        }
    }

    private static ComSafeArray<T> FromArray(Array values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(SafeArrayConverter.Create(values, SafeArrayConverter.GetVarType(typeof(T))).Psa, ownsArray: true);
    }

    private static void CheckRank(nint pointer, int rank)
    {
        if (OleNativeMethods.SafeArrayGetDim(pointer) != rank)
        {
            throw new InvalidOperationException($"The array has {OleNativeMethods.SafeArrayGetDim(pointer)} dimensions, not {rank}.");
        }
    }

    // Whether T has the layout of the elements, so that they can be accessed without converting them.
    private static bool HasLayoutOf(VarEnum vt)
    {
        Type type = typeof(T).IsEnum ? Enum.GetUnderlyingType(typeof(T)) : typeof(T);
        return vt switch
        {
            VarEnum.VT_I1 => type == typeof(sbyte),
            VarEnum.VT_UI1 => type == typeof(byte),
            VarEnum.VT_I2 => type == typeof(short),
            VarEnum.VT_UI2 => type == typeof(ushort),
            VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_ERROR => type == typeof(int),
            VarEnum.VT_UI4 or VarEnum.VT_UINT => type == typeof(uint),
            VarEnum.VT_I8 => type == typeof(long),
            VarEnum.VT_UI8 => type == typeof(ulong),
            VarEnum.VT_R4 => type == typeof(float),
            VarEnum.VT_R8 => type == typeof(double),
            VarEnum.VT_DECIMAL => type == typeof(decimal),
            VarEnum.VT_BOOL => type == typeof(ComBool),
            VarEnum.VT_DATE => type == typeof(ComDate),
            VarEnum.VT_CY => type == typeof(ComCurrency),
            _ => false,
        };
    }

    private Array ToManagedArray(int rank)
    {
        nint pointer = Pointer;
        CheckRank(pointer, rank);
        return SafeArrayConverter.ToManaged(pointer)!;
    }

    private uint CheckDimension(int dimension)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(dimension, Rank);
        return (uint)dimension + 1;
    }

    private void Release()
    {
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0 && OwnsArray)
        {
            OleNativeMethods.SafeArrayDestroy(pointer);
        }
    }
}
