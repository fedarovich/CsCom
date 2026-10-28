using System.Runtime.InteropServices;

namespace CsCom.Runtime;

/// <summary>
/// The OLE Automation <c>EXCEPINFO</c> structure.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ExcepInfo
{
    /// <summary>The error code.</summary>
    public ushort wCode;

    /// <summary>Reserved.</summary>
    public ushort wReserved;

    /// <summary>The <c>BSTR</c> with the source of the exception.</summary>
    public nint bstrSource;

    /// <summary>The <c>BSTR</c> with the description of the error.</summary>
    public nint bstrDescription;

    /// <summary>The <c>BSTR</c> with the path of the help file.</summary>
    public nint bstrHelpFile;

    /// <summary>The help context identifier.</summary>
    public uint dwHelpContext;

    /// <summary>Reserved.</summary>
    public nint pvReserved;

    /// <summary>A function that fills in the structure lazily.</summary>
    public delegate* unmanaged[Stdcall]<ExcepInfo*, int> pfnDeferredFillIn;

    /// <summary>The error code (<c>SCODE</c>).</summary>
    public int scode;
}
