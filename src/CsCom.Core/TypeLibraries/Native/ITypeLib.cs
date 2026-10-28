using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.TypeLibraries.Native;

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("00020402-0000-0000-C000-000000000046")]
internal unsafe partial interface ITypeLib
{
    [PreserveSig]
    uint GetTypeInfoCount();

    void GetTypeInfo(uint index, out ITypeInfo ppTInfo);

    void GetTypeInfoType(uint index, out TYPEKIND pTKind);

    [PreserveSig]
    int GetTypeInfoOfGuid(in Guid guid, out ITypeInfo? ppTinfo);

    void GetLibAttr(out TLIBATTR* ppTLibAttr);

    [PreserveSig]
    int GetTypeComp(out nint ppTComp);

    void GetDocumentation(int index, out string? pBstrName, out string? pBstrDocString, out uint pdwHelpContext, out string? pBstrHelpFile);

    [PreserveSig]
    int IsName(char* szNameBuf, uint lHashVal, int* pfName);

    [PreserveSig]
    int FindName(char* szNameBuf, uint lHashVal, nint* ppTInfo, int* rgMemId, ushort* pcFound);

    [PreserveSig]
    void ReleaseTLibAttr(TLIBATTR* pTLibAttr);
}
