using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// The COM <c>IConnectionPointContainer</c> interface, implemented by the objects that raise events.
/// </summary>
[GeneratedComInterface]
[Guid("B196B284-BAB4-101A-B69C-00AA00341D07")]
public partial interface IConnectionPointContainer
{
    /// <summary>Creates an enumerator of the connection points (<c>IEnumConnectionPoints</c>), which must be released by the caller.</summary>
    [PreserveSig]
    int EnumConnectionPoints(out nint ppEnum);

    /// <summary>Finds the connection point of the specified event interface.</summary>
    [PreserveSig]
    int FindConnectionPoint(in Guid riid, out IConnectionPoint? ppCP);
}

/// <summary>
/// The COM <c>IConnectionPoint</c> interface, through which the sinks of an event interface are connected.
/// </summary>
[GeneratedComInterface]
[Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
public partial interface IConnectionPoint
{
    /// <summary>Gets the identifier of the event interface.</summary>
    [PreserveSig]
    int GetConnectionInterface(out Guid pIID);

    /// <summary>Gets the connection point container (<c>IConnectionPointContainer</c>), which must be released by the caller.</summary>
    [PreserveSig]
    int GetConnectionPointContainer(out nint ppCPC);

    /// <summary>Connects a sink, passed as an <c>IUnknown</c> pointer, and returns the cookie that identifies the connection.</summary>
    [PreserveSig]
    int Advise(nint pUnkSink, out uint pdwCookie);

    /// <summary>Disconnects the sink identified by the cookie.</summary>
    [PreserveSig]
    int Unadvise(uint dwCookie);

    /// <summary>Creates an enumerator of the connections (<c>IEnumConnections</c>), which must be released by the caller.</summary>
    [PreserveSig]
    int EnumConnections(out nint ppEnum);
}
