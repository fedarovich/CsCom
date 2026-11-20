using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals <see cref="object"/> values as OLE Automation <c>VARIANT</c>s.
/// </summary>
/// <remarks>
/// Extends <see cref="ComVariantMarshaller"/> with support for enums, <see cref="ComDate"/>, <see cref="ComCurrency"/> and <see cref="ComBool"/>, COM objects (passed as <c>VT_DISPATCH</c> when
/// they implement <c>IDispatch</c>), late-bound dispinterface wrappers and <c>dynamic</c> objects, <see cref="Missing"/> (passed as an omitted
/// optional argument), arrays of up to three dimensions and <see cref="ComSafeArray{T}"/> (passed as <c>SAFEARRAY</c>s), and
/// <see cref="ComBstr"/> (passed as a copy of the <c>BSTR</c>).
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
            case ComDate date:
                return ComVariant.CreateRaw(VarEnum.VT_DATE, date.Value);
            case ComCurrency currency:
                return ComVariant.CreateRaw(VarEnum.VT_CY, currency.Value);
            case ComBool boolean:
                return ComVariant.CreateRaw(VarEnum.VT_BOOL, boolean.Value);
            case Enum value:
                return ConvertEnum(value);
            case Array array:
                return SafeArrayConverter.ToVariant(array);

            // A COM object, or a late-bound or dynamic wrapper of one, is converted to its pointer (see IDispatchProvider). A
            // COM object is tested first and not for the interfaces below, which its strategy would look up on every call;
            // one that has been released is rejected. It passes the IDispatch that it keeps once the type test has asked
            // it for it, while QueryInterface would ask it on every call. The type test decides, so that an object without
            // IDispatch is asked once per conversion, which is a call to another apartment for a proxy: the strategy of a
            // custom ComWrappers must have the details of IDispatch, which the late-bound wrappers need too, and an object
            // whose strategy has none is passed as VT_UNKNOWN.
            case ComObject:
                return managed is IDispatch dispatch
                    ? ComVariant.CreateRaw(VarEnum.VT_DISPATCH, (nint)ComMarshalling.ToUnmanaged(dispatch))
                    : ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, (nint)ComMarshalling.ToUnmanaged(managed));

            // A late-bound wrapper without a COM object, whose Dispatch breaks its contract, stands for no object, like in
            // a SAFEARRAY of IDispatch: a null IDispatch, as its object would be one. The Dispatch is converted itself, not
            // unwrapped again. A managed object that is exposed to COM is passed itself, also when it implements
            // IDispatchProvider.
            case IDispatchProvider when ComMarshalling.IsLateBoundWrapper(managed, out IDispatch? wrapped):
                return ComVariant.CreateRaw(VarEnum.VT_DISPATCH, (nint)ComMarshalling.ToUnmanaged(wrapped, unwrap: false));
            case IDispatchProvider:
                return CreateObjectVariant((nint)ComMarshalling.ToUnmanaged(managed));
            case ISafeArrayProvider provider:
                return CreateSafeArrayVariant(provider.Pointer);
            case ComBstr bstr:
                return ComVariant.CreateRaw(VarEnum.VT_BSTR, ComBstr.Copy(bstr.Pointer));

            // ComVariantMarshaller would expose the wrapped object with the default instance of .NET, and passes null as an
            // empty VARIANT: here it is a null object. A DispatchWrapper has no case: without the built-in COM interop it
            // only wraps null, which an UnknownWrapper passes, so ComVariantMarshaller rejects it with an ArgumentException,
            // like ComMarshalling.ToUnmanaged where an object goes to COM otherwise.
            case UnknownWrapper wrapper:
                return ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, (nint)ComMarshalling.ToUnmanaged(wrapper.WrappedObject));
        }

        return CreateObjectVariant(managed);
    }

    /// <summary>Converts a <see cref="ComVariant"/> to a managed object.</summary>
    public static object? ConvertToManaged(ComVariant unmanaged)
    {
        VarEnum vt = unmanaged.VarType;
        if ((vt & VarEnum.VT_ARRAY) != 0)
        {
            // An array passed by reference can have no reference at all, like one that is a null pointer.
            nint psa = unmanaged.GetRawDataRef<nint>();
            if ((vt & VarEnum.VT_BYREF) != 0 && psa != 0)
            {
                psa = *(nint*)psa;
            }

            return SafeArrayConverter.ToManaged(psa);
        }

        if (vt == (VarEnum.VT_BYREF | VarEnum.VT_VARIANT))
        {
            nint reference = unmanaged.GetRawDataRef<nint>();
            return reference == 0 ? null : ConvertToManaged(*(ComVariant*)reference);
        }

        // ComVariantMarshaller would create the object with the default instance of .NET, and does not accept a null
        // pointer, like Nothing of Visual Basic.
        if (TryGetObjectPointer(&unmanaged, out nint pointer))
        {
            return ComMarshalling.ToManaged<object>((void*)pointer);
        }

        return ComVariantMarshaller.ConvertToManaged(unmanaged);
    }

    /// <summary>
    /// Gets the interface pointer of a <c>VARIANT</c> that holds an object, also one that is passed by reference or in a
    /// <c>VARIANT</c> that is passed by reference, without adding a reference; 0 for a null object.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the <c>VARIANT</c> does not hold an object, also a <c>VARIANT</c> passed by reference
    /// without a reference.
    /// </returns>
    internal static bool TryGetObjectPointer(ComVariant* variant, out nint pointer)
    {
        // ConvertToManaged follows such a reference itself, for every type, before it gets here.
        if (variant->VarType == (VarEnum.VT_BYREF | VarEnum.VT_VARIANT))
        {
            variant = (ComVariant*)variant->GetRawDataRef<nint>();
            if (variant is null)
            {
                pointer = 0;
                return false;
            }
        }

        VarEnum vt = variant->VarType;
        if ((vt & ~VarEnum.VT_BYREF) is not (VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH))
        {
            pointer = 0;
            return false;
        }

        pointer = variant->GetRawDataRef<nint>();
        if ((vt & VarEnum.VT_BYREF) != 0 && pointer != 0)
        {
            pointer = *(nint*)pointer;
        }

        return true;
    }

    /// <summary>Releases the resources owned by the <see cref="ComVariant"/>.</summary>
    public static void Free(ComVariant unmanaged)
    {
        unmanaged.Dispose();
    }

    // Converts a COM object, a managed object that is exposed to COM, or a value that ComVariantMarshaller supports.
    private static ComVariant CreateObjectVariant(object value)
    {
        // The most common values first: they are no objects, and need neither the lookup of a COM object nor that of an
        // exposed class, also DBNull, the VT_NULL that e.g. clears a field. The wrappers of the built-in COM interop of
        // .NET, BStrWrapper, ErrorWrapper, VariantWrapper and the obsolete CurrencyWrapper, take both lookups, which cost
        // little next to the call that the VARIANT goes to: they are rare, the support code has ComBstr and ComCurrency,
        // and tests for them here would cost every COM object of another ComWrappers and every exposed object.
        if (value is ValueType or string or DBNull)
        {
            return ComVariantMarshaller.ConvertToUnmanaged(value);
        }

        if (ComWrappers.TryGetComInstance(value, out nint unknown))
        {
            return CreateObjectVariant(unknown);
        }

        // A managed object is exposed to COM if its class is a [GeneratedComClass], by the instance of ComMarshalling:
        // ComVariantMarshaller would expose it with the default instance of .NET.
        if (ComMarshalling.IsExposedToCom(value))
        {
            return CreateObjectVariant((nint)ComMarshalling.ToUnmanaged(value));
        }

        // The other values, and an ArgumentException for the objects that cannot be passed to COM.
        return ComVariantMarshaller.ConvertToUnmanaged(value);
    }

    // Takes ownership of the 'unknown' reference, which is never null: that of a managed object that is exposed to COM, or
    // of the COM object of another ComWrappers.
    private static ComVariant CreateObjectVariant(nint unknown)
    {
        nint pointer = ComObjects.QueryDispatch(unknown, out bool isDispatch);
        return ComVariant.CreateRaw(isDispatch ? VarEnum.VT_DISPATCH : VarEnum.VT_UNKNOWN, pointer);
    }

    // The VARIANT holds a copy, so that the wrapper keeps owning its SAFEARRAY.
    private static ComVariant CreateSafeArrayVariant(nint psa)
    {
        Marshal.ThrowExceptionForHR(OleNativeMethods.SafeArrayGetVartype(psa, out ushort vt));
        return ComVariant.CreateRaw(VarEnum.VT_ARRAY | (VarEnum)vt, SafeArrayConverter.Copy(psa));
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

