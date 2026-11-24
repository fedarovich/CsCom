using System.Runtime.InteropServices;

namespace CsCom.TypeLibraries.Native;

internal static partial class OleAut32
{
    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int LoadTypeLibEx(string szFile, REGKIND regkind, out ITypeLib? pptlib);

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int CreateTypeLib2(System.Runtime.InteropServices.ComTypes.SYSKIND syskind, string szFile, out ICreateTypeLib? ppctlib);

    // The path is a BSTR, which some versions of Windows return with the terminating null character included in its length.
    [LibraryImport("oleaut32.dll")]
    public static partial int QueryPathOfRegTypeLib(in Guid guid, ushort wMaj, ushort wMin, int lcid, out nint lpbstrPathName);
}
