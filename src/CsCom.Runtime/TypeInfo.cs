using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// The OLE Automation <c>ITypeInfo</c> interface, which describes a type of a type library.
/// </summary>
[GeneratedComInterface]
[Guid("00020401-0000-0000-C000-000000000046")]
internal unsafe partial interface ITypeInfo
{
    [PreserveSig]
    int GetTypeAttr(out TypeAttr* ppTypeAttr);

    [PreserveSig]
    int GetTypeComp(out nint ppTComp);

    [PreserveSig]
    int GetFuncDesc(uint index, out FuncDesc* ppFuncDesc);

    [PreserveSig]
    int GetVarDesc(uint index, out VarDesc* ppVarDesc);

    [PreserveSig]
    int GetNames(int memid, nint* rgBstrNames, uint cMaxNames, out uint pcNames);

    [PreserveSig]
    int GetRefTypeOfImplType(uint index, out uint pRefType);

    [PreserveSig]
    int GetImplTypeFlags(uint index, out int pImplTypeFlags);

    [PreserveSig]
    int GetIDsOfNames(char** rgszNames, uint cNames, int* pMemId);

    [PreserveSig]
    int Invoke(void* pvInstance, int memid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr);

    [PreserveSig]
    int GetDocumentation(int memid, nint* pBstrName, nint* pBstrDocString, uint* pdwHelpContext, nint* pBstrHelpFile);

    [PreserveSig]
    int GetDllEntry(int memid, int invKind, nint* pBstrDllName, nint* pBstrName, ushort* pwOrdinal);

    [PreserveSig]
    int GetRefTypeInfo(uint hRefType, out ITypeInfo? ppTInfo);

    [PreserveSig]
    int AddressOfMember(int memid, int invKind, void** ppv);

    [PreserveSig]
    int CreateInstance(nint pUnkOuter, in Guid riid, void** ppvObj);

    [PreserveSig]
    int GetMops(int memid, nint* pBstrMops);

    [PreserveSig]
    int GetContainingTypeLib(out ITypeLib? ppTLib, out uint pIndex);

    [PreserveSig]
    void ReleaseTypeAttr(TypeAttr* pTypeAttr);

    [PreserveSig]
    void ReleaseFuncDesc(FuncDesc* pFuncDesc);

    [PreserveSig]
    void ReleaseVarDesc(VarDesc* pVarDesc);
}

/// <summary>
/// The first methods of the OLE Automation <c>ITypeLib</c> interface, which describes a type library.
/// </summary>
[GeneratedComInterface]
[Guid("00020402-0000-0000-C000-000000000046")]
internal partial interface ITypeLib
{
    [PreserveSig]
    uint GetTypeInfoCount();

    [PreserveSig]
    int GetTypeInfo(uint index, out ITypeInfo? ppTInfo);

    [PreserveSig]
    int GetTypeInfoType(uint index, out int pTKind);
}

/// <summary>
/// The COM <c>IProvideClassInfo</c> interface, through which an object gives the type information of its coclass, including
/// its event interfaces.
/// </summary>
[GeneratedComInterface]
[Guid("B196B283-BAB4-101A-B69C-00AA00341D07")]
public partial interface IProvideClassInfo
{
    /// <summary>Gets the type information (<c>ITypeInfo</c>) of the coclass, which must be released by the caller.</summary>
    [PreserveSig]
    int GetClassInfo(out nint ppTI);
}

/// <summary>The <c>TYPEDESC</c> structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TypeDesc
{
    public nint lpValue;
    public ushort vt;
}

/// <summary>The <c>ELEMDESC</c> structure, whose <c>PARAMDESC</c> and <c>IDLDESC</c> union members have the same layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ElemDesc
{
    public TypeDesc tdesc;
    public nint pparamdescex;
    public ushort wParamFlags;
}

/// <summary>The <c>TYPEATTR</c> structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TypeAttr
{
    public Guid guid;
    public uint lcid;
    public uint dwReserved;
    public int memidConstructor;
    public int memidDestructor;
    public nint lpstrSchema;
    public uint cbSizeInstance;
    public int typekind;
    public ushort cFuncs;
    public ushort cVars;
    public ushort cImplTypes;
    public ushort cbSizeVft;
    public ushort cbAlignment;
    public ushort wTypeFlags;
    public ushort wMajorVerNum;
    public ushort wMinorVerNum;
    public TypeDesc tdescAlias;
    public nint idldescReserved;
    public ushort wIDLFlags;
}

/// <summary>The <c>FUNCDESC</c> structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FuncDesc
{
    public int memid;
    public int* lprgscode;
    public ElemDesc* lprgelemdescParam;
    public int funckind;
    public int invkind;
    public int callconv;
    public short cParams;
    public short cParamsOpt;
    public short oVft;
    public short cScodes;
    public ElemDesc elemdescFunc;
    public ushort wFuncFlags;
}

/// <summary>The <c>VARDESC</c> structure.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VarDesc
{
    public int memid;
    public nint lpstrSchema;
    public nint oInstOrValue;
    public ElemDesc elemdescVar;
    public ushort wVarFlags;
    public int varkind;
}
