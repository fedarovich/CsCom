using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.TypeLibraries.Native;

// The strings of the interfaces that create type libraries are LPOLESTRs, unlike the BSTRs of ITypeLib and ITypeInfo.
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("00020406-0000-0000-C000-000000000046")]
internal partial interface ICreateTypeLib
{
    void CreateTypeInfo(string szName, TYPEKIND tkind, out ICreateTypeInfo ppCTInfo);

    void SetName(string szName);

    void SetVersion(ushort wMajorVerNum, ushort wMinorVerNum);

    void SetGuid(in Guid guid);

    void SetDocString(string szDoc);

    void SetHelpFileName(string szHelpFileName);

    void SetHelpContext(uint dwHelpContext);

    void SetLcid(int lcid);

    void SetLibFlags(uint uLibFlags);

    void SaveAllChanges();
}
