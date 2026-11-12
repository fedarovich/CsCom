using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals optional <see cref="object"/> parameters as <c>VARIANT</c>s.
/// </summary>
/// <remarks>
/// Unlike <see cref="VariantMarshaller"/>, <see langword="null"/> is passed as an omitted argument
/// (<c>VT_ERROR</c> with <c>DISP_E_PARAMNOTFOUND</c>), so that the COM object applies its default value.
/// Pass <see cref="System.DBNull.Value"/> to pass <c>VT_NULL</c>.
/// </remarks>
[CustomMarshaller(typeof(object), MarshalMode.Default, typeof(OptionalVariantMarshaller))]
public static class OptionalVariantMarshaller
{
    /// <summary>Converts a managed object to a <see cref="ComVariant"/>.</summary>
    public static ComVariant ConvertToUnmanaged(object? managed) => VariantMarshaller.ConvertToUnmanaged(managed ?? Missing.Value);

    /// <summary>
    /// Converts a <see cref="ComVariant"/> to a managed object; an omitted argument (<c>VT_ERROR</c> with
    /// <c>DISP_E_PARAMNOTFOUND</c>), e.g. of a COM caller of an interface implemented in .NET, is <see langword="null"/>.
    /// </summary>
    public static object? ConvertToManaged(ComVariant unmanaged) =>
        unmanaged.VarType == VarEnum.VT_ERROR && unmanaged.GetRawDataRef<int>() == OleNativeMethods.DISP_E_PARAMNOTFOUND
            ? null
            : VariantMarshaller.ConvertToManaged(unmanaged);

    /// <summary>Releases the resources owned by the <see cref="ComVariant"/>.</summary>
    public static void Free(ComVariant unmanaged) => VariantMarshaller.Free(unmanaged);
}
