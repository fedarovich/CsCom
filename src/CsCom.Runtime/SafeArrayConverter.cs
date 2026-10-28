using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Converts one-dimensional <c>SAFEARRAY</c>s to managed arrays and back.
/// </summary>
internal static unsafe class SafeArrayConverter
{
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

    public static ComVariant ToVariant(Array array)
    {
        if (array.Rank != 1)
        {
            throw new NotSupportedException("Only one-dimensional arrays can be marshalled as SAFEARRAYs.");
        }

        (VarEnum vt, nint psa) = array switch
        {
            object?[] objects when array.GetType() == typeof(object[]) => (VarEnum.VT_VARIANT, CreateVariants(objects)),
            string?[] strings => (VarEnum.VT_BSTR, CreateStrings(strings)),
            bool[] booleans => (VarEnum.VT_BOOL, CreateBooleans(booleans)),
            DateTime[] dates => (VarEnum.VT_DATE, CreateDates(dates)),
            sbyte[] values => (VarEnum.VT_I1, Create(VarEnum.VT_I1, values)),
            byte[] values => (VarEnum.VT_UI1, Create(VarEnum.VT_UI1, values)),
            short[] values => (VarEnum.VT_I2, Create(VarEnum.VT_I2, values)),
            ushort[] values => (VarEnum.VT_UI2, Create(VarEnum.VT_UI2, values)),
            int[] values => (VarEnum.VT_I4, Create(VarEnum.VT_I4, values)),
            uint[] values => (VarEnum.VT_UI4, Create(VarEnum.VT_UI4, values)),
            long[] values => (VarEnum.VT_I8, Create(VarEnum.VT_I8, values)),
            ulong[] values => (VarEnum.VT_UI8, Create(VarEnum.VT_UI8, values)),
            float[] values => (VarEnum.VT_R4, Create(VarEnum.VT_R4, values)),
            double[] values => (VarEnum.VT_R8, Create(VarEnum.VT_R8, values)),
            decimal[] values => (VarEnum.VT_DECIMAL, Create(VarEnum.VT_DECIMAL, values)),
            _ => throw new NotSupportedException($"Arrays of {array.GetType().GetElementType()} cannot be marshalled as SAFEARRAYs."),
        };

        return ComVariant.CreateRaw(VarEnum.VT_ARRAY | vt, psa);
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

    private static nint Create<T>(VarEnum vt, T[] values) where T : unmanaged
    {
        nint psa = CreateVector(vt, values.Length, out void* data);
        values.AsSpan().CopyTo(new Span<T>(data, values.Length));
        OleNativeMethods.SafeArrayUnaccessData(psa);
        return psa;
    }

    private static nint CreateVariants(object?[] values)
    {
        nint psa = CreateVector(VarEnum.VT_VARIANT, values.Length, out void* data);
        try
        {
            var items = (ComVariant*)data;
            for (int i = 0; i < values.Length; i++)
            {
                items[i] = VariantMarshaller.ConvertToUnmanaged(values[i]);
            }
        }
        finally
        {
            OleNativeMethods.SafeArrayUnaccessData(psa);
        }

        return psa;
    }

    private static nint CreateStrings(string?[] values)
    {
        nint psa = CreateVector(VarEnum.VT_BSTR, values.Length, out void* data);
        var items = (nint*)data;
        for (int i = 0; i < values.Length; i++)
        {
            items[i] = values[i] is { } value ? Marshal.StringToBSTR(value) : 0;
        }

        OleNativeMethods.SafeArrayUnaccessData(psa);
        return psa;
    }

    private static nint CreateBooleans(bool[] values)
    {
        nint psa = CreateVector(VarEnum.VT_BOOL, values.Length, out void* data);
        var items = (short*)data;
        for (int i = 0; i < values.Length; i++)
        {
            items[i] = values[i] ? (short)-1 : (short)0;
        }

        OleNativeMethods.SafeArrayUnaccessData(psa);
        return psa;
    }

    private static nint CreateDates(DateTime[] values)
    {
        nint psa = CreateVector(VarEnum.VT_DATE, values.Length, out void* data);
        var items = (double*)data;
        for (int i = 0; i < values.Length; i++)
        {
            items[i] = values[i].ToOADate();
        }

        OleNativeMethods.SafeArrayUnaccessData(psa);
        return psa;
    }

    private static nint CreateVector(VarEnum vt, int length, out void* data)
    {
        nint psa = OleNativeMethods.SafeArrayCreateVector((ushort)vt, 0, (uint)length);
        if (psa == 0)
        {
            throw new OutOfMemoryException("SafeArrayCreateVector failed.");
        }

        int hr = OleNativeMethods.SafeArrayAccessData(psa, out data);
        if (hr < 0)
        {
            OleNativeMethods.SafeArrayDestroy(psa);
            Marshal.ThrowExceptionForHR(hr);
        }

        return psa;
    }
}
