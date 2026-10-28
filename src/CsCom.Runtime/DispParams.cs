using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// The OLE Automation <c>DISPPARAMS</c> structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DispParams
{
    /// <summary>The arguments, in reverse order.</summary>
    public ComVariant* rgvarg;

    /// <summary>The dispatch identifiers of the named arguments.</summary>
    public int* rgdispidNamedArgs;

    /// <summary>The number of arguments.</summary>
    public uint cArgs;

    /// <summary>The number of named arguments.</summary>
    public uint cNamedArgs;
}
