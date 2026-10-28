using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals <see cref="object"/> values as OLE Automation <c>VARIANT</c>s.
/// </summary>
/// <remarks>
/// Extends <see cref="ComVariantMarshaller"/> with support for enums, COM objects (passed as <c>VT_DISPATCH</c> when
/// they implement <c>IDispatch</c>), late-bound dispinterface wrappers, <see cref="Missing"/> (passed as an omitted
/// optional argument) and one-dimensional arrays (passed as <c>SAFEARRAY</c>s).
/// </remarks>
[CustomMarshaller(typeof(object), MarshalMode.Default, typeof(VariantMarshaller))]
public static unsafe class VariantMarshaller
{
    /// <summary>Converts a managed object to a <see cref="ComVariant"/>.</summary>
    public static ComVariant ConvertToUnmanaged(object? managed)
    {
        switch (managed)
        {
            case null:
                return default;
            case Missing:
                return ComVariant.CreateRaw(VarEnum.VT_ERROR, OleNativeMethods.DISP_E_PARAMNOTFOUND);
            case Enum value:
                return ConvertEnum(value);
            case Array array:
                return SafeArrayConverter.ToVariant(array);
            case DispatchObject wrapper:
                return CreateObjectVariant(wrapper.Dispatch);
        }

        if (ComWrappers.TryGetComInstance(managed, out nint unknown))
        {
            return CreateObjectVariant(unknown);
        }

        return ComVariantMarshaller.ConvertToUnmanaged(managed);
    }

    /// <summary>Converts a <see cref="ComVariant"/> to a managed object.</summary>
    public static object? ConvertToManaged(ComVariant unmanaged)
    {
        VarEnum vt = unmanaged.VarType;
        if ((vt & VarEnum.VT_ARRAY) != 0)
        {
            nint psa = (vt & VarEnum.VT_BYREF) != 0 ? *(nint*)unmanaged.GetRawDataRef<nint>() : unmanaged.GetRawDataRef<nint>();
            return SafeArrayConverter.ToManaged(psa);
        }

        if (vt == (VarEnum.VT_BYREF | VarEnum.VT_VARIANT))
        {
            return ConvertToManaged(*(ComVariant*)unmanaged.GetRawDataRef<nint>());
        }

        return ComVariantMarshaller.ConvertToManaged(unmanaged);
    }

    /// <summary>Releases the resources owned by the <see cref="ComVariant"/>.</summary>
    public static void Free(ComVariant unmanaged)
    {
        unmanaged.Dispose();
    }

    private static ComVariant CreateObjectVariant(object dispatchOrUnknown)
    {
        if (ComWrappers.TryGetComInstance(dispatchOrUnknown, out nint unknown))
        {
            return CreateObjectVariant(unknown);
        }

        return ComVariantMarshaller.ConvertToUnmanaged(dispatchOrUnknown);
    }

    // Takes ownership of the 'unknown' reference.
    private static ComVariant CreateObjectVariant(nint unknown)
    {
        nint pointer = ComObjects.QueryDispatch(unknown, out bool isDispatch);
        return ComVariant.CreateRaw(isDispatch ? VarEnum.VT_DISPATCH : VarEnum.VT_UNKNOWN, pointer);
    }

    private static ComVariant ConvertEnum(Enum value)
    {
        IConvertible convertible = value;
        return convertible.GetTypeCode() switch
        {
            TypeCode.SByte => ComVariant.Create(convertible.ToSByte(null)),
            TypeCode.Byte => ComVariant.Create(convertible.ToByte(null)),
            TypeCode.Int16 => ComVariant.Create(convertible.ToInt16(null)),
            TypeCode.UInt16 => ComVariant.Create(convertible.ToUInt16(null)),
            TypeCode.UInt32 => ComVariant.Create(convertible.ToUInt32(null)),
            TypeCode.Int64 => ComVariant.Create(convertible.ToInt64(null)),
            TypeCode.UInt64 => ComVariant.Create(convertible.ToUInt64(null)),
            _ => ComVariant.Create(convertible.ToInt32(null)),
        };
    }
}

