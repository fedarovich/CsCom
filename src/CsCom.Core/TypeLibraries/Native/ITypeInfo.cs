using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.TypeLibraries.Native;

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("00020401-0000-0000-C000-000000000046")]
internal unsafe partial interface ITypeInfo
{
    void GetTypeAttr(out TYPEATTR* ppTypeAttr);

    [PreserveSig]
    int GetTypeComp(out nint ppTComp);

    void GetFuncDesc(uint index, out FUNCDESC* ppFuncDesc);

    void GetVarDesc(uint index, out VARDESC* ppVarDesc);

    void GetNames(int memid, nint* rgBstrNames, uint cMaxNames, out uint pcNames);

    [PreserveSig]
    int GetRefTypeOfImplType(uint index, out uint pRefType);

    void GetImplTypeFlags(uint index, out IMPLTYPEFLAGS pImplTypeFlags);

    [PreserveSig]
    int GetIDsOfNames(char** rgszNames, uint cNames, int* pMemId);

    [PreserveSig]
    int Invoke(void* pvInstance, int memid, ushort wFlags, void* pDispParams, void* pVarResult, void* pExcepInfo, uint* puArgErr);

    [PreserveSig]
    int GetDocumentation(int memid, out string? pBstrName, out string? pBstrDocString, out uint pdwHelpContext, out string? pBstrHelpFile);

    [PreserveSig]
    int GetDllEntry(int memid, INVOKEKIND invKind, out string? pBstrDllName, out string? pBstrName, out ushort pwOrdinal);

    void GetRefTypeInfo(uint hRefType, out ITypeInfo ppTInfo);

    [PreserveSig]
    int AddressOfMember(int memid, INVOKEKIND invKind, void** ppv);

    [PreserveSig]
    int CreateInstance(void* pUnkOuter, in Guid riid, void** ppvObj);

    [PreserveSig]
    int GetMops(int memid, out string? pBstrMops);

    void GetContainingTypeLib(out ITypeLib ppTLib, out uint pIndex);

    [PreserveSig]
    void ReleaseTypeAttr(TYPEATTR* pTypeAttr);

    [PreserveSig]
    void ReleaseFuncDesc(FUNCDESC* pFuncDesc);

    [PreserveSig]
    void ReleaseVarDesc(VARDESC* pVarDesc);
}
