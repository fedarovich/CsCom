using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals <see cref="decimal"/> values as OLE Automation currency values (<c>CURRENCY</c>).
/// </summary>
[CustomMarshaller(typeof(decimal), MarshalMode.Default, typeof(CurrencyMarshaller))]
public static class CurrencyMarshaller
{
    /// <summary>Converts a <see cref="decimal"/> to a currency value, a 64-bit integer scaled by 10,000.</summary>
    public static long ConvertToUnmanaged(decimal managed) => decimal.ToOACurrency(managed);

    /// <summary>Converts a currency value to a <see cref="decimal"/>.</summary>
    public static decimal ConvertToManaged(long unmanaged) => decimal.FromOACurrency(unmanaged);
}
