using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Converts one-dimensional <c>SAFEARRAY</c>s to managed arrays and back.
/// </summary>
internal static unsafe class SafeArrayConverter
{
    /// <summary>
    /// Converts a <c>SAFEARRAY</c> to a managed array whose element type is determined by the variant type of the <c>SAFEARRAY</c>.
    /// </summary>
    public static Array? ToManaged(nint psa)
    {
        if (psa == 0)
        {
            return null;
        }

        uint dims = OleNativeMethods.SafeArrayGetDim(psa);
        if (dims != 1)
        {
            throw new NotSupportedException($"Only one-dimensional SAFEARRAYs are supported, but the array has {dims} dimensions.");
        }

        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetVartype(psa, out ushort vtRaw));
        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetLBound(psa, 1, out int lower));
        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetUBound(psa, 1, out int upper));
        int length = upper - lower + 1;

        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayAccessData(psa, out void* data));
        try
        {
            VarEnum vt = (VarEnum)vtRaw;
            return vt switch
            {
                VarEnum.VT_VARIANT => ConvertVariants((ComVariant*)data, length),
                VarEnum.VT_BSTR => ConvertStrings((nint*)data, length),
                VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH => ConvertObjects((void**)data, length),
                VarEnum.VT_BOOL => ConvertBooleans((short*)data, length),
                VarEnum.VT_DATE => ConvertDates((double*)data, length),
                VarEnum.VT_CY => ConvertCurrencies((long*)data, length),
                VarEnum.VT_I1 => new ReadOnlySpan<sbyte>(data, length).ToArray(),
                VarEnum.VT_UI1 => new ReadOnlySpan<byte>(data, length).ToArray(),
                VarEnum.VT_I2 => new ReadOnlySpan<short>(data, length).ToArray(),
                VarEnum.VT_UI2 => new ReadOnlySpan<ushort>(data, length).ToArray(),
                VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_ERROR => new ReadOnlySpan<int>(data, length).ToArray(),
                VarEnum.VT_UI4 or VarEnum.VT_UINT => new ReadOnlySpan<uint>(data, length).ToArray(),
                VarEnum.VT_I8 => new ReadOnlySpan<long>(data, length).ToArray(),
                VarEnum.VT_UI8 => new ReadOnlySpan<ulong>(data, length).ToArray(),
                VarEnum.VT_R4 => new ReadOnlySpan<float>(data, length).ToArray(),
                VarEnum.VT_R8 => new ReadOnlySpan<double>(data, length).ToArray(),
                VarEnum.VT_DECIMAL => new ReadOnlySpan<decimal>(data, length).ToArray(),
                _ => throw new NotSupportedException($"SAFEARRAYs of {vt} are not supported."),
            };
        }
        finally
        {
            OleNativeMethods.SafeArrayUnaccessData(psa);
        }
    }

    /// <summary>
    /// Converts a <c>SAFEARRAY</c> to an array of <typeparamref name="T"/>, converting the elements when the variant type of
    /// the <c>SAFEARRAY</c> does not match, e.g. <c>VT_I4</c> elements to an enum or COM objects to an interface.
    /// </summary>
    public static T[]? ToArray<T>(nint psa)
    {
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
    /// Creates a <c>SAFEARRAY</c> from a one-dimensional array.
    /// </summary>
    /// <param name="array">The array.</param>
    /// <param name="elementType">
    /// The variant type of the elements; derived from the element type of the array by default.
    /// <c>VT_UNKNOWN</c> and <c>VT_DISPATCH</c> can be specified for arrays of objects.
    /// </param>
    /// <returns>The variant type of the elements and the <c>SAFEARRAY</c>, which is owned by the caller.</returns>
    public static (VarEnum ElementType, nint Psa) Create(Array array, VarEnum? elementType = null)
    {
        if (array.Rank != 1)
        {
            throw new NotSupportedException("Only one-dimensional arrays can be marshalled as SAFEARRAYs.");
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

        throw new NotSupportedException($"Arrays of {type} cannot be marshalled as SAFEARRAYs.");
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
