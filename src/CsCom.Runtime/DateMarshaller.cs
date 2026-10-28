using System;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals <see cref="DateTime"/> values as OLE Automation dates (<c>DATE</c>).
/// </summary>
[CustomMarshaller(typeof(DateTime), MarshalMode.Default, typeof(DateMarshaller))]
public static class DateMarshaller
{
    /// <summary>Converts a <see cref="DateTime"/> to an OLE Automation date.</summary>
    public static double ConvertToUnmanaged(DateTime managed) => managed.ToOADate();

    /// <summary>Converts an OLE Automation date to a <see cref="DateTime"/>.</summary>
    public static DateTime ConvertToManaged(double unmanaged) => DateTime.FromOADate(unmanaged);
}
