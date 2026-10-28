using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals one-dimensional arrays as <c>SAFEARRAY</c>s.
/// </summary>
/// <remarks>
/// The variant type of the elements is derived from <typeparamref name="T"/>: <see cref="object"/> is passed as <c>VT_VARIANT</c>,
/// <see cref="string"/> as <c>VT_BSTR</c>, <see cref="bool"/> as <c>VT_BOOL</c>, <see cref="System.DateTime"/> as <c>VT_DATE</c>,
/// enums as their underlying integer type, interfaces derived from <see cref="IDispatch"/> as <c>VT_DISPATCH</c> and other
/// interfaces as <c>VT_UNKNOWN</c>. Use <see cref="UnknownSafeArrayMarshaller"/> for arrays of <c>IUnknown</c> pointers.
/// Returned arrays are converted to zero-based arrays of <typeparamref name="T"/>.
/// </remarks>
/// <typeparam name="T">The type of the elements.</typeparam>
[CustomMarshaller(typeof(CustomMarshallerAttribute.GenericPlaceholder[]), MarshalMode.Default, typeof(SafeArrayMarshaller<>))]
public static class SafeArrayMarshaller<T>
{
    /// <summary>Creates a <c>SAFEARRAY</c> from an array.</summary>
    public static nint ConvertToUnmanaged(T[]? managed) => managed is null ? 0 : SafeArrayConverter.Create(managed).Psa;

    /// <summary>Converts a <c>SAFEARRAY</c> to an array.</summary>
    public static T[]? ConvertToManaged(nint unmanaged) => SafeArrayConverter.ToArray<T>(unmanaged);

    /// <summary>Destroys the <c>SAFEARRAY</c>.</summary>
    public static void Free(nint unmanaged) => SafeArrayConverter.Destroy(unmanaged);
}

/// <summary>
/// Marshals arrays of objects as <c>SAFEARRAY</c>s of <c>IUnknown</c> pointers.
/// </summary>
[CustomMarshaller(typeof(object[]), MarshalMode.Default, typeof(UnknownSafeArrayMarshaller))]
public static class UnknownSafeArrayMarshaller
{
    /// <summary>Creates a <c>SAFEARRAY</c> of <c>IUnknown</c> pointers from an array.</summary>
    public static nint ConvertToUnmanaged(object?[]? managed) => managed is null ? 0 : SafeArrayConverter.Create(managed, VarEnum.VT_UNKNOWN).Psa;

    /// <summary>Converts a <c>SAFEARRAY</c> to an array.</summary>
    public static object?[]? ConvertToManaged(nint unmanaged) => SafeArrayConverter.ToArray<object?>(unmanaged);

    /// <summary>Destroys the <c>SAFEARRAY</c>.</summary>
    public static void Free(nint unmanaged) => SafeArrayConverter.Destroy(unmanaged);
}
