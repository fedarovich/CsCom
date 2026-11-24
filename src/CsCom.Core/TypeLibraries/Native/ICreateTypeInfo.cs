using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.TypeLibraries.Native;

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("00020405-0000-0000-C000-000000000046")]
internal unsafe partial interface ICreateTypeInfo
{
    void SetGuid(in Guid guid);

    void SetTypeFlags(uint uTypeFlags);

    void SetDocString(string pStrDoc);

    void SetHelpContext(uint dwHelpContext);

    void SetVersion(ushort wMajorVerNum, ushort wMinorVerNum);

    void AddRefTypeInfo(ITypeInfo pTInfo, out uint phRefType);

    void AddFuncDesc(uint index, FUNCDESC* pFuncDesc);

    void AddImplType(uint index, uint hRefType);

    void SetImplTypeFlags(uint index, int implTypeFlags);

    void SetAlignment(ushort cbAlignment);

    void SetSchema(string pStrSchema);

    void AddVarDesc(uint index, VARDESC* pVarDesc);

    void SetFuncAndParamNames(uint index, char** rgszNames, uint cNames);

    void SetVarName(uint index, string szName);

    void SetTypeDescAlias(TYPEDESC* pTDescAlias);

    // The name of the entry point is an ordinal in the low word of the pointer for a function exported by ordinal.
    void DefineFuncAsDllEntry(uint index, char* szDllName, char* szProcName);

    void SetFuncDocString(uint index, string szDocString);

    void SetVarDocString(uint index, string szDocString);

    void SetFuncHelpContext(uint index, uint dwHelpContext);

    void SetVarHelpContext(uint index, uint dwHelpContext);

    void SetMops(uint index, nint bstrMops);

    void SetTypeIdldesc(IDLDESC* pIdlDesc);

    void LayOut();
}
