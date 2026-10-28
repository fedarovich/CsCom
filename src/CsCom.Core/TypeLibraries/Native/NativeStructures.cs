using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CsCom.TypeLibraries.Native;

// Blittable definitions of the OLE Automation type description structures.
// The enums are reused from System.Runtime.InteropServices.ComTypes, the structures there contain managed fields.

[StructLayout(LayoutKind.Sequential)]
internal struct TLIBATTR
{
    public Guid guid;
    public int lcid;
    public SYSKIND syskind;
    public ushort wMajorVerNum;
    public ushort wMinorVerNum;
    public LIBFLAGS wLibFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TYPEDESC
{
    // Union of TYPEDESC* lptdesc, ARRAYDESC* lpadesc and HREFTYPE hreftype.
    public nint lpValue;
    public ushort vt;

    public readonly TYPEDESC* lptdesc => (TYPEDESC*)lpValue;

    public readonly ARRAYDESC* lpadesc => (ARRAYDESC*)lpValue;

    public readonly uint hreftype => (uint)lpValue;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SAFEARRAYBOUND
{
    public uint cElements;
    public int lLbound;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ARRAYDESC
{
    public TYPEDESC tdescElem;
    public ushort cDims;

    // Followed by cDims SAFEARRAYBOUND structures.
    private SAFEARRAYBOUND _rgbounds;

    public static SAFEARRAYBOUND* Bounds(ARRAYDESC* desc) => &desc->_rgbounds;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PARAMDESCEX
{
    public uint cBytes;
    public System.Runtime.InteropServices.Marshalling.ComVariant varDefaultValue;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PARAMDESC
{
    public PARAMDESCEX* pparamdescex;
    public PARAMFLAG wParamFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ELEMDESC
{
    public TYPEDESC tdesc;

    // Union of IDLDESC and PARAMDESC, which have the same layout.
    public PARAMDESC paramdesc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IDLDESC
{
    public nuint dwReserved;
    public ushort wIDLFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TYPEATTR
{
    public Guid guid;
    public int lcid;
    public uint dwReserved;
    public int memidConstructor;
    public int memidDestructor;
    public char* lpstrSchema;
    public uint cbSizeInstance;
    public TYPEKIND typekind;
    public ushort cFuncs;
    public ushort cVars;
    public ushort cImplTypes;
    public ushort cbSizeVft;
    public ushort cbAlignment;
    public TYPEFLAGS wTypeFlags;
    public ushort wMajorVerNum;
    public ushort wMinorVerNum;
    public TYPEDESC tdescAlias;
    public IDLDESC idldescType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FUNCDESC
{
    public int memid;
    public int* lprgscode;
    public ELEMDESC* lprgelemdescParam;
    public FUNCKIND funckind;
    public INVOKEKIND invkind;
    public CALLCONV callconv;
    public short cParams;
    public short cParamsOpt;
    public short oVft;
    public short cScodes;
    public ELEMDESC elemdescFunc;
    public FUNCFLAGS wFuncFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VARDESC
{
    public int memid;
    public char* lpstrSchema;

    // Union of ULONG oInst and VARIANT* lpvarValue.
    public nint lpValue;
    public ELEMDESC elemdescVar;
    public VARFLAGS wVarFlags;
    public VARKIND varkind;

    public readonly uint oInst => (uint)lpValue;

    public readonly System.Runtime.InteropServices.Marshalling.ComVariant* lpvarValue => (System.Runtime.InteropServices.Marshalling.ComVariant*)lpValue;
}

internal enum REGKIND
{
    REGKIND_DEFAULT = 0,
    REGKIND_REGISTER = 1,
    REGKIND_NONE = 2,
}
