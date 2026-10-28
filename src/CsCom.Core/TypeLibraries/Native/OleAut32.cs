using System.Runtime.InteropServices;

namespace CsCom.TypeLibraries.Native;

internal static partial class OleAut32
{
    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int LoadTypeLibEx(string szFile, REGKIND regkind, out ITypeLib? pptlib);
}
