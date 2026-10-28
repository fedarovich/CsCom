using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Converts <c>SAFEARRAY</c>s to managed arrays and back.
/// </summary>
/// <remarks>
/// <c>SAFEARRAY</c>s store their elements with the first index varying fastest, and .NET arrays with the last index varying
/// fastest, so the elements of multidimensional arrays are reordered: the element <c>(i, j)</c> of a <c>SAFEARRAY</c> is
/// <c>[i, j]</c> of the .NET array. The .NET arrays created from <c>SAFEARRAY</c>s are zero-based, because Native AOT does
/// not support arrays with other lower bounds.
/// </remarks>
internal static unsafe class SafeArrayConverter
{
    /// <summary>The highest rank of the <c>SAFEARRAY</c>s that are converted to .NET arrays.</summary>
    public const int MaxRank = 3;

    /// <summary>
    /// Converts a <c>SAFEARRAY</c> to a zero-based managed array of up to three dimensions, whose element type is determined
    /// by the variant type of the <c>SAFEARRAY</c>.
    /// </summary>
    public static Array? ToManaged(nint psa)
    {
        if (psa == 0)
        {
            return null;
        }

        int[] lengths = GetLengths(psa);
        if (lengths.Length > MaxRank)
        {
            throw new NotSupportedException(
                $"SAFEARRAYs with up to {MaxRank} dimensions can be converted to .NET arrays, but the array has {lengths.Length} dimensions. Use ComSafeArray<T> to access it.");
        }

        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetVartype(psa, out ushort vtRaw));
        int length = 1;
        foreach (int dimension in lengths)
        {
            length *= dimension;
        }

        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayAccessData(psa, out void* data));
        try
        {
            VarEnum vt = (VarEnum)vtRaw;
            return vt switch
            {
                VarEnum.VT_VARIANT => Reshape(ConvertVariants((ComVariant*)data, length), lengths),
                VarEnum.VT_BSTR => Reshape(ConvertStrings((nint*)data, length), lengths),
                VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH => Reshape(ConvertObjects((void**)data, length), lengths),
                VarEnum.VT_BOOL => Reshape(ConvertBooleans((short*)data, length), lengths),
                VarEnum.VT_DATE => Reshape(ConvertDates((double*)data, length), lengths),
                VarEnum.VT_CY => Reshape(ConvertCurrencies((long*)data, length), lengths),
                VarEnum.VT_I1 => Reshape(new ReadOnlySpan<sbyte>(data, length).ToArray(), lengths),
                VarEnum.VT_UI1 => Reshape(new ReadOnlySpan<byte>(data, length).ToArray(), lengths),
                VarEnum.VT_I2 => Reshape(new ReadOnlySpan<short>(data, length).ToArray(), lengths),
                VarEnum.VT_UI2 => Reshape(new ReadOnlySpan<ushort>(data, length).ToArray(), lengths),
                VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_ERROR => Reshape(new ReadOnlySpan<int>(data, length).ToArray(), lengths),
                VarEnum.VT_UI4 or VarEnum.VT_UINT => Reshape(new ReadOnlySpan<uint>(data, length).ToArray(), lengths),
                VarEnum.VT_I8 => Reshape(new ReadOnlySpan<long>(data, length).ToArray(), lengths),
                VarEnum.VT_UI8 => Reshape(new ReadOnlySpan<ulong>(data, length).ToArray(), lengths),
                VarEnum.VT_R4 => Reshape(new ReadOnlySpan<float>(data, length).ToArray(), lengths),
                VarEnum.VT_R8 => Reshape(new ReadOnlySpan<double>(data, length).ToArray(), lengths),
                VarEnum.VT_DECIMAL => Reshape(new ReadOnlySpan<decimal>(data, length).ToArray(), lengths),
                _ => throw new NotSupportedException($"SAFEARRAYs of {vt} are not supported."),
            };
        }
        finally
        {
            OleNativeMethods.SafeArrayUnaccessData(psa);
        }
    }

    /// <summary>
    /// Converts a one-dimensional <c>SAFEARRAY</c> to an array of <typeparamref name="T"/>, converting the elements when the
    /// variant type of the <c>SAFEARRAY</c> does not match, e.g. <c>VT_I4</c> elements to an enum or COM objects to an interface.
    /// </summary>
    /// <exception cref="NotSupportedException">The <c>SAFEARRAY</c> has more than one dimension.</exception>
    public static T[]? ToArray<T>(nint psa)
    {
        if (psa != 0 && OleNativeMethods.SafeArrayGetDim(psa) is var dims and not 1)
        {
            throw new NotSupportedException(
                $"A one-dimensional SAFEARRAY is expected, but the array has {dims} dimensions. " +
                "Generate the interop code with '--safearray-marshalling sysarray', 'wrapper' or 'none' to receive multidimensional SAFEARRAYs.");
        }

        Array? array = ToManaged(psa);
        if (array is null)
        {
            return null;
        }

        if (array.GetType() == typeof(T[]))
        {
            return (T[])array;
        }

        var result = new T[array.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = ConvertElement<T>(array.GetValue(i));
        }

        return result;
    }

    /// <summary>
    /// Gets the number of elements of each dimension of a <c>SAFEARRAY</c>, the first (fastest-varying) dimension first.
    /// </summary>
    public static int[] GetLengths(nint psa)
    {
        int[] lengths = new int[OleNativeMethods.SafeArrayGetDim(psa)];
        for (int i = 0; i < lengths.Length; i++)
        {
            Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetLBound(psa, (uint)i + 1, out int lower));
            Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetUBound(psa, (uint)i + 1, out int upper));
            lengths[i] = upper - lower + 1;
        }

        return lengths;
    }

    /// <summary>
    /// Creates a <c>SAFEARRAY</c> from an array of up to three dimensions. Multidimensional arrays and arrays with other
    /// lower bounds than zero keep their bounds.
    /// </summary>
    /// <param name="array">The array.</param>
    /// <param name="elementType">
    /// The variant type of the elements; derived from the element type of the array by default.
    /// <c>VT_UNKNOWN</c> and <c>VT_DISPATCH</c> can be specified for arrays of objects.
    /// </param>
    /// <returns>The variant type of the elements and the <c>SAFEARRAY</c>, which is owned by the caller.</returns>
    public static (VarEnum ElementType, nint Psa) Create(Array array, VarEnum? elementType = null)
    {
        if (array.Rank > MaxRank)
        {
            throw new NotSupportedException($"Arrays with up to {MaxRank} dimensions can be marshalled as SAFEARRAYs, but the array has {array.Rank} dimensions.");
        }

        if (array.Rank != 1 || array.GetLowerBound(0) != 0)
        {
            VarEnum vt = elementType ?? GetVarType(array.GetType().GetElementType()!);
            return (vt, CreateArray(array, vt));
        }

        if (elementType is VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH)
        {
            return (elementType.Value, CreateObjects(array, elementType.Value));
        }

        switch (array)
        {
            case object?[] objects when array.GetType() == typeof(object[]):
                return (VarEnum.VT_VARIANT, CreateVariants(objects));
            case string?[] strings:
                return (VarEnum.VT_BSTR, CreateStrings(strings));
            case bool[] booleans:
                return (VarEnum.VT_BOOL, CreateBooleans(booleans));
            case DateTime[] dates:
                return (VarEnum.VT_DATE, CreateDates(dates));
            case sbyte[] values:
                return (VarEnum.VT_I1, Create(VarEnum.VT_I1, values));
            case byte[] values:
                return (VarEnum.VT_UI1, Create(VarEnum.VT_UI1, values));
            case short[] values:
                return (VarEnum.VT_I2, Create(VarEnum.VT_I2, values));
            case ushort[] values:
                return (VarEnum.VT_UI2, Create(VarEnum.VT_UI2, values));
            case int[] values:
                return (VarEnum.VT_I4, Create(VarEnum.VT_I4, values));
            case uint[] values:
                return (VarEnum.VT_UI4, Create(VarEnum.VT_UI4, values));
            case long[] values:
                return (VarEnum.VT_I8, Create(VarEnum.VT_I8, values));
            case ulong[] values:
                return (VarEnum.VT_UI8, Create(VarEnum.VT_UI8, values));
            case float[] values:
                return (VarEnum.VT_R4, Create(VarEnum.VT_R4, values));
            case double[] values:
                return (VarEnum.VT_R8, Create(VarEnum.VT_R8, values));
            case decimal[] values:
                return (VarEnum.VT_DECIMAL, Create(VarEnum.VT_DECIMAL, values));
        }

        Type type = array.GetType().GetElementType()!;
        if (type.IsEnum)
        {
            return CreateEnums(array, Type.GetTypeCode(type));
        }

        bool isWrapper = typeof(IDispatchProvider).IsAssignableFrom(type);
        if (type.IsInterface || isWrapper)
        {
            VarEnum vt = isWrapper || typeof(IDispatch).IsAssignableFrom(type) ? VarEnum.VT_DISPATCH : VarEnum.VT_UNKNOWN;
            return (vt, CreateObjects(array, vt));
        }

        // Arrays of the value wrappers, e.g. ComDate[].
        VarEnum elementVt = GetVarType(type);
        return (elementVt, CreateArray(array, elementVt));
    }

    /// <summary>
    /// Creates a <c>SAFEARRAY</c> with the specified element type and bounds, whose elements are zero, empty or <see langword="null"/>.
    /// </summary>
    /// <param name="elementType">The variant type of the elements.</param>
    /// <param name="lengths">The number of elements of each dimension, the first (fastest-varying) dimension first.</param>
    /// <param name="lowerBounds">The lower bounds of the dimensions; zero when empty.</param>
    /// <returns>The <c>SAFEARRAY</c>, which is owned by the caller.</returns>
    public static nint Create(VarEnum elementType, ReadOnlySpan<int> lengths, ReadOnlySpan<int> lowerBounds = default)
    {
        if (lengths.IsEmpty)
        {
            throw new ArgumentException("A SAFEARRAY has at least one dimension.", nameof(lengths));
        }

        if (!lowerBounds.IsEmpty && lowerBounds.Length != lengths.Length)
        {
            throw new ArgumentException("The number of lower bounds must match the number of dimensions.", nameof(lowerBounds));
        }

        var bounds = new SafeArrayBound[lengths.Length];
        for (int i = 0; i < bounds.Length; i++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(lengths[i], nameof(lengths));
            bounds[i] = new SafeArrayBound { cElements = (uint)lengths[i], lLbound = lowerBounds.IsEmpty ? 0 : lowerBounds[i] };
        }

        fixed (SafeArrayBound* pBounds = bounds)
        {
            nint psa = OleNativeMethods.SafeArrayCreate((ushort)elementType, (uint)bounds.Length, pBounds);
            return psa != 0 ? psa : throw new NotSupportedException($"A SAFEARRAY of {elementType} cannot be created.");
        }
    }

    /// <summary>
    /// Gets the variant type of the elements of a <c>SAFEARRAY</c> created from an array with the specified element type.
    /// </summary>
    /// <exception cref="NotSupportedException">The type cannot be stored in a <c>SAFEARRAY</c>.</exception>
    public static VarEnum GetVarType(Type type)
    {
        if (type == typeof(object))
        {
            return VarEnum.VT_VARIANT;
        }

        if (type == typeof(string))
        {
            return VarEnum.VT_BSTR;
        }

        if (type == typeof(bool) || type == typeof(ComBool))
        {
            return VarEnum.VT_BOOL;
        }

        if (type == typeof(DateTime) || type == typeof(ComDate))
        {
            return VarEnum.VT_DATE;
        }

        if (type == typeof(ComCurrency))
        {
            return VarEnum.VT_CY;
        }

        if (type == typeof(decimal))
        {
            return VarEnum.VT_DECIMAL;
        }

        if (type.IsEnum || type.IsPrimitive)
        {
            // Enums are stored as their underlying integer type.
            switch (Type.GetTypeCode(type))
            {
                case TypeCode.SByte:
                    return VarEnum.VT_I1;
                case TypeCode.Byte:
                    return VarEnum.VT_UI1;
                case TypeCode.Int16:
                    return VarEnum.VT_I2;
                case TypeCode.UInt16:
                    return VarEnum.VT_UI2;
                case TypeCode.Int32:
                    return VarEnum.VT_I4;
                case TypeCode.UInt32:
                    return VarEnum.VT_UI4;
                case TypeCode.Int64:
                    return VarEnum.VT_I8;
                case TypeCode.UInt64:
                    return VarEnum.VT_UI8;
                case TypeCode.Single:
                    return VarEnum.VT_R4;
                case TypeCode.Double:
                    return VarEnum.VT_R8;
            }
        }

        bool isWrapper = typeof(IDispatchProvider).IsAssignableFrom(type);
        if (type.IsInterface || isWrapper)
        {
            return isWrapper || typeof(IDispatch).IsAssignableFrom(type) ? VarEnum.VT_DISPATCH : VarEnum.VT_UNKNOWN;
        }

        throw new NotSupportedException($"Arrays of {type} cannot be marshalled as SAFEARRAYs.");
    }

    /// <summary>
    /// Reads an element of a <c>SAFEARRAY</c> as a managed object, as <see cref="ToManaged"/> converts it.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="vt">The variant type of the elements.</param>
    public static object? ReadElement(void* element, VarEnum vt) => vt switch
    {
        VarEnum.VT_VARIANT => VariantMarshaller.ConvertToManaged(*(ComVariant*)element),
        VarEnum.VT_BSTR => *(nint*)element == 0 ? null : Marshal.PtrToStringBSTR(*(nint*)element),
        VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH => ComInterfaceMarshaller<object>.ConvertToManaged(*(void**)element),
        VarEnum.VT_BOOL => *(short*)element != 0,
        VarEnum.VT_DATE => DateTime.FromOADate(*(double*)element),
        VarEnum.VT_CY => decimal.FromOACurrency(*(long*)element),
        VarEnum.VT_I1 => *(sbyte*)element,
        VarEnum.VT_UI1 => *(byte*)element,
        VarEnum.VT_I2 => *(short*)element,
        VarEnum.VT_UI2 => *(ushort*)element,
        VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_ERROR => *(int*)element,
        VarEnum.VT_UI4 or VarEnum.VT_UINT => *(uint*)element,
        VarEnum.VT_I8 => *(long*)element,
        VarEnum.VT_UI8 => *(ulong*)element,
        VarEnum.VT_R4 => *(float*)element,
        VarEnum.VT_R8 => *(double*)element,
        VarEnum.VT_DECIMAL => *(decimal*)element,
        _ => throw new NotSupportedException($"SAFEARRAYs of {vt} are not supported."),
    };

    /// <summary>
    /// Writes an element of a <c>SAFEARRAY</c>, converting the value to the variant type of the elements, and releases the
    /// previous string, <c>VARIANT</c> or interface.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <param name="vt">The variant type of the elements.</param>
    /// <param name="value">The new value.</param>
    public static void WriteElement(void* element, VarEnum vt, object? value)
    {
        switch (vt)
        {
            case VarEnum.VT_VARIANT:
            {
                ComVariant converted = VariantMarshaller.ConvertToUnmanaged(value);
                Marshal.ThrowExceptionForHR(OleNativeMethods.VariantClear((ComVariant*)element));
                *(ComVariant*)element = converted;
                return;
            }

            case VarEnum.VT_BSTR:
            {
                string? text = value as string ?? (value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
                nint bstr = text is null ? 0 : Marshal.StringToBSTR(text);
                Marshal.FreeBSTR(*(nint*)element);
                *(nint*)element = bstr;
                return;
            }

            case VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH:
            {
                nint pointer = vt == VarEnum.VT_DISPATCH ? ComObjects.GetDispatch(value) : ComObjects.GetUnknown(value);
                if (*(nint*)element != 0)
                {
                    Marshal.Release(*(nint*)element);
                }

                *(nint*)element = pointer;
                return;
            }

            case VarEnum.VT_BOOL:
                *(short*)element = value is ComBool comBool ? comBool.Value : ComConvert.To<bool>(value) ? (short)-1 : (short)0;
                return;
            case VarEnum.VT_DATE:
                *(double*)element = value is ComDate date ? date.Value : ComConvert.To<DateTime>(value).ToOADate();
                return;
            case VarEnum.VT_CY:
                *(long*)element = value is ComCurrency currency ? currency.Value : decimal.ToOACurrency(ComConvert.To<decimal>(value));
                return;
            case VarEnum.VT_I1:
                *(sbyte*)element = ComConvert.To<sbyte>(value);
                return;
            case VarEnum.VT_UI1:
                *(byte*)element = ComConvert.To<byte>(value);
                return;
            case VarEnum.VT_I2:
                *(short*)element = ComConvert.To<short>(value);
                return;
            case VarEnum.VT_UI2:
                *(ushort*)element = ComConvert.To<ushort>(value);
                return;
            case VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_ERROR:
                *(int*)element = ComConvert.To<int>(value);
                return;
            case VarEnum.VT_UI4 or VarEnum.VT_UINT:
                *(uint*)element = ComConvert.To<uint>(value);
                return;
            case VarEnum.VT_I8:
                *(long*)element = ComConvert.To<long>(value);
                return;
            case VarEnum.VT_UI8:
                *(ulong*)element = ComConvert.To<ulong>(value);
                return;
            case VarEnum.VT_R4:
                *(float*)element = ComConvert.To<float>(value);
                return;
            case VarEnum.VT_R8:
                *(double*)element = ComConvert.To<double>(value);
                return;
            case VarEnum.VT_DECIMAL:
                *(decimal*)element = ComConvert.To<decimal>(value);
                return;
            default:
                throw new NotSupportedException($"SAFEARRAYs of {vt} are not supported.");
        }
    }

    /// <summary>
    /// Creates a <c>VARIANT</c> that holds a <c>SAFEARRAY</c> created from a one-dimensional array.
    /// </summary>
    public static ComVariant ToVariant(Array array)
    {
        (VarEnum vt, nint psa) = Create(array);
        return ComVariant.CreateRaw(VarEnum.VT_ARRAY | vt, psa);
    }

    /// <summary>
    /// Copies a <c>SAFEARRAY</c>, including the strings, variants and interfaces it holds.
    /// </summary>
    /// <returns>The copy, which is owned by the caller.</returns>
    public static nint Copy(nint psa)
    {
        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayCopy(psa, out nint copy));
        return copy;
    }

    /// <summary>
    /// Destroys a <c>SAFEARRAY</c> and releases the strings, variants and interfaces it holds.
    /// </summary>
    public static void Destroy(nint psa)
    {
        if (psa != 0)
        {
            Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayDestroy(psa));
        }
    }

    private static T ConvertElement<T>(object? value)
    {
        switch (value)
        {
            case null:
                return default!;
            case T typed:
                return typed;
        }

        if (typeof(T).IsEnum)
        {
            return (T)Enum.ToObject(typeof(T), value);
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    // Arranges the elements of a SAFEARRAY, whose first index varies fastest, into a zero-based .NET array of the same shape.
    private static Array Reshape<T>(T[] elements, int[] lengths)
    {
        switch (lengths.Length)
        {
            case 1:
                return elements;
            case 2:
            {
                var result = new T[lengths[0], lengths[1]];
                int index = 0;
                for (int j = 0; j < lengths[1]; j++)
                {
                    for (int i = 0; i < lengths[0]; i++)
                    {
                        result[i, j] = elements[index++];
                    }
                }

                return result;
            }

            default:
            {
                var result = new T[lengths[0], lengths[1], lengths[2]];
                int index = 0;
                for (int k = 0; k < lengths[2]; k++)
                {
                    for (int j = 0; j < lengths[1]; j++)
                    {
                        for (int i = 0; i < lengths[0]; i++)
                        {
                            result[i, j, k] = elements[index++];
                        }
                    }
                }

                return result;
            }
        }
    }

    // Creates a SAFEARRAY with the bounds of the array and writes its elements one by one, with the first index varying fastest.
    private static nint CreateArray(Array array, VarEnum vt)
    {
        int rank = array.Rank;
        Span<int> lengths = stackalloc int[rank];
        Span<int> lowerBounds = stackalloc int[rank];
        for (int i = 0; i < rank; i++)
        {
            lengths[i] = array.GetLength(i);
            lowerBounds[i] = array.GetLowerBound(i);
        }

        nint psa = Create(vt, lengths, lowerBounds);
        int hr = OleNativeMethods.SafeArrayAccessData(psa, out void* data);
        if (hr < 0)
        {
            OleNativeMethods.SafeArrayDestroy(psa);
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            int size = (int)OleNativeMethods.SafeArrayGetElemsize(psa);
            Span<int> index = stackalloc int[rank];
            for (int position = 0; position < array.Length; position++)
            {
                object? value = rank switch
                {
                    1 => array.GetValue(lowerBounds[0] + index[0]),
                    2 => array.GetValue(lowerBounds[0] + index[0], lowerBounds[1] + index[1]),
                    _ => array.GetValue(lowerBounds[0] + index[0], lowerBounds[1] + index[1], lowerBounds[2] + index[2]),
                };
                WriteElement((byte*)data + ((nint)position * size), vt, value);

                for (int dimension = 0; dimension < rank && ++index[dimension] == lengths[dimension]; dimension++)
                {
                    index[dimension] = 0;
                }
            }
        }
        catch
        {
            OleNativeMethods.SafeArrayUnaccessData(psa);
            OleNativeMethods.SafeArrayDestroy(psa);
            throw;
        }

        OleNativeMethods.SafeArrayUnaccessData(psa);
        return psa;
    }

    private static object?[] ConvertVariants(ComVariant* data, int length)
    {
        var result = new object?[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = VariantMarshaller.ConvertToManaged(data[i]);
        }

        return result;
    }

    private static string?[] ConvertStrings(nint* data, int length)
    {
        var result = new string?[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = data[i] == 0 ? null : Marshal.PtrToStringBSTR(data[i]);
        }

        return result;
    }

    private static object?[] ConvertObjects(void** data, int length)
    {
        var result = new object?[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = ComInterfaceMarshaller<object>.ConvertToManaged(data[i]);
        }

        return result;
    }

    private static bool[] ConvertBooleans(short* data, int length)
    {
        var result = new bool[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = data[i] != 0;
        }

        return result;
    }

    private static DateTime[] ConvertDates(double* data, int length)
    {
        var result = new DateTime[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = DateTime.FromOADate(data[i]);
        }

        return result;
    }

    private static decimal[] ConvertCurrencies(long* data, int length)
    {
        var result = new decimal[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = decimal.FromOACurrency(data[i]);
        }

        return result;
    }

    private static nint Create<T>(VarEnum vt, T[] values) where T : unmanaged =>
        CreateVector(vt, values.Length, values, static (data, values) => values.AsSpan().CopyTo(new Span<T>(data, values.Length)));

    private static nint CreateVariants(object?[] values) =>
        CreateVector(VarEnum.VT_VARIANT, values.Length, values, static (data, values) =>
        {
            var items = (ComVariant*)data;
            for (int i = 0; i < values.Length; i++)
            {
                items[i] = VariantMarshaller.ConvertToUnmanaged(values[i]);
            }
        });

    private static nint CreateStrings(string?[] values) =>
        CreateVector(VarEnum.VT_BSTR, values.Length, values, static (data, values) =>
        {
            var items = (nint*)data;
            for (int i = 0; i < values.Length; i++)
            {
                items[i] = values[i] is { } value ? Marshal.StringToBSTR(value) : 0;
            }
        });

    private static nint CreateBooleans(bool[] values) =>
        CreateVector(VarEnum.VT_BOOL, values.Length, values, static (data, values) =>
        {
            var items = (short*)data;
            for (int i = 0; i < values.Length; i++)
            {
                items[i] = values[i] ? (short)-1 : (short)0;
            }
        });

    private static nint CreateDates(DateTime[] values) =>
        CreateVector(VarEnum.VT_DATE, values.Length, values, static (data, values) =>
        {
            var items = (double*)data;
            for (int i = 0; i < values.Length; i++)
            {
                items[i] = values[i].ToOADate();
            }
        });

    private static nint CreateObjects(Array values, VarEnum vt) =>
        CreateVector(vt, values.Length, values, vt == VarEnum.VT_DISPATCH
            ? static (data, values) =>
            {
                var items = (nint*)data;
                for (int i = 0; i < values.Length; i++)
                {
                    items[i] = ComObjects.GetDispatch(values.GetValue(i));
                }
            }
            : static (data, values) =>
            {
                var items = (nint*)data;
                for (int i = 0; i < values.Length; i++)
                {
                    items[i] = ComObjects.GetUnknown(values.GetValue(i));
                }
            });

    // Enum arrays are passed as arrays of their underlying integer type.
    private static (VarEnum, nint) CreateEnums(Array values, TypeCode typeCode)
    {
        VarEnum vt = typeCode switch
        {
            TypeCode.SByte => VarEnum.VT_I1,
            TypeCode.Byte => VarEnum.VT_UI1,
            TypeCode.Int16 => VarEnum.VT_I2,
            TypeCode.UInt16 => VarEnum.VT_UI2,
            TypeCode.UInt32 => VarEnum.VT_UI4,
            TypeCode.Int64 => VarEnum.VT_I8,
            TypeCode.UInt64 => VarEnum.VT_UI8,
            _ => VarEnum.VT_I4,
        };

        nint psa = CreateVector(vt, values.Length, values, (data, values) =>
        {
            for (int i = 0; i < values.Length; i++)
            {
                var value = (IConvertible)values.GetValue(i)!;
                switch (vt)
                {
                    case VarEnum.VT_I1:
                        ((sbyte*)data)[i] = value.ToSByte(CultureInfo.InvariantCulture);
                        break;
                    case VarEnum.VT_UI1:
                        ((byte*)data)[i] = value.ToByte(CultureInfo.InvariantCulture);
                        break;
                    case VarEnum.VT_I2:
                        ((short*)data)[i] = value.ToInt16(CultureInfo.InvariantCulture);
                        break;
                    case VarEnum.VT_UI2:
                        ((ushort*)data)[i] = value.ToUInt16(CultureInfo.InvariantCulture);
                        break;
                    case VarEnum.VT_UI4:
                        ((uint*)data)[i] = value.ToUInt32(CultureInfo.InvariantCulture);
                        break;
                    case VarEnum.VT_I8:
                        ((long*)data)[i] = value.ToInt64(CultureInfo.InvariantCulture);
                        break;
                    case VarEnum.VT_UI8:
                        ((ulong*)data)[i] = value.ToUInt64(CultureInfo.InvariantCulture);
                        break;
                    default:
                        ((int*)data)[i] = value.ToInt32(CultureInfo.InvariantCulture);
                        break;
                }
            }
        });

        return (vt, psa);
    }

    /// <summary>
    /// Creates a <c>SAFEARRAY</c> and fills its elements. The array is destroyed if filling it fails.
    /// </summary>
    private static nint CreateVector<TState>(VarEnum vt, int length, TState state, Fill<TState> fill)
    {
        nint psa = OleNativeMethods.SafeArrayCreateVector((ushort)vt, 0, (uint)length);
        if (psa == 0)
        {
            throw new OutOfMemoryException("SafeArrayCreateVector failed.");
        }

        int hr = OleNativeMethods.SafeArrayAccessData(psa, out void* data);
        if (hr < 0)
        {
            OleNativeMethods.SafeArrayDestroy(psa);
            Marshal.ThrowExceptionForHR(hr);
        }

        try
        {
            fill(data, state);
        }
        catch
        {
            OleNativeMethods.SafeArrayUnaccessData(psa);
            OleNativeMethods.SafeArrayDestroy(psa);
            throw;
        }

        OleNativeMethods.SafeArrayUnaccessData(psa);
        return psa;
    }

    private delegate void Fill<in TState>(void* data, TState state);
}
