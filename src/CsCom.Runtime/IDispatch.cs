using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// The OLE Automation <c>IDispatch</c> interface. Dual interfaces derive from it so that their vtable slots line up.
/// </summary>
[GeneratedComInterface]
[Guid("00020400-0000-0000-C000-000000000046")]
public unsafe partial interface IDispatch
{
    /// <summary>Retrieves the number of type information interfaces that an object provides (either 0 or 1).</summary>
    [PreserveSig]
    int GetTypeInfoCount(out uint pctinfo);

    /// <summary>Retrieves the type information for an object. The returned <c>ITypeInfo</c> pointer must be released by the caller.</summary>
    [PreserveSig]
    int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo);

    /// <summary>Maps a set of names to a corresponding set of dispatch identifiers.</summary>
    [PreserveSig]
    int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId);

    /// <summary>Provides access to properties and methods exposed by an object.</summary>
    [PreserveSig]
    int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr);
}
