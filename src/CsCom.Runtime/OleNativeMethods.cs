using System;
using System.Runtime.InteropServices;

namespace CsCom.Runtime;

internal static unsafe partial class OleNativeMethods
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);
    public const int DISP_E_EXCEPTION = unchecked((int)0x80020009);
    public const int DISP_E_PARAMNOTFOUND = unchecked((int)0x80020004);
    public const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    public const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    public const int CO_E_CLASSSTRING = unchecked((int)0x800401F3);
    public const uint COINIT_MULTITHREADED = 0x0;
    public const uint LOCALE_USER_DEFAULT = 0x0400;

    public static readonly Guid IID_IUnknown = new(0x00000000, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);
    public static readonly Guid IID_IDispatch = new(0x00020400, 0x0000, 0x0000, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46);

    [LibraryImport("ole32.dll")]
    public static partial int CoCreateInstance(in Guid rclsid, void* pUnkOuter, uint dwClsContext, in Guid riid, out void* ppv);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(void* pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CLSIDFromProgID(string lpszProgID, out Guid lpclsid);

    [LibraryImport("oleaut32.dll")]
    public static partial uint SafeArrayGetDim(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetLBound(nint psa, uint nDim, out int plLbound);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetUBound(nint psa, uint nDim, out int plUbound);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayGetVartype(nint psa, out ushort pvt);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayAccessData(nint psa, out void* ppvData);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayUnaccessData(nint psa);

    [LibraryImport("oleaut32.dll")]
    public static partial nint SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);

    [LibraryImport("oleaut32.dll")]
    public static partial int SafeArrayDestroy(nint psa);
}
