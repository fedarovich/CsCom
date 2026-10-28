using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// The OLE Automation <c>IEnumVARIANT</c> interface returned by <c>_NewEnum</c> members of collections.
/// </summary>
[GeneratedComInterface]
[Guid("00020404-0000-0000-C000-000000000046")]
public unsafe partial interface IEnumVARIANT
{
    /// <summary>Retrieves the specified number of items in the enumeration sequence.</summary>
    [PreserveSig]
    int Next(uint celt, ComVariant* rgVar, uint* pCeltFetched);

    /// <summary>Attempts to skip over the next <paramref name="celt"/> elements in the enumeration sequence.</summary>
    [PreserveSig]
    int Skip(uint celt);

    /// <summary>Resets the enumeration sequence to the beginning.</summary>
    [PreserveSig]
    int Reset();

    /// <summary>Creates a copy of the current state of enumeration.</summary>
    [PreserveSig]
    int Clone(out IEnumVARIANT? ppEnum);
}
