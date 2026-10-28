using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace CsCom.Runtime;

/// <summary>
/// A connection of an event sink to a COM object that raises events through a connection point. Disposing the connection
/// disconnects the sink.
/// </summary>
/// <remarks>
/// Dispose the connection on the thread that created it: <c>IConnectionPoint::Unadvise</c> must be called from the apartment
/// the connection was made in. There is no finalizer, so a connection that is not disposed stays connected until the COM
/// object releases the sink.
/// </remarks>
public sealed unsafe class EventConnection : IDisposable
{
    private IConnectionPoint? _connectionPoint;

    private EventConnection(IConnectionPoint connectionPoint, uint cookie)
    {
        _connectionPoint = connectionPoint;
        Cookie = cookie;
    }

    /// <summary>Gets the cookie that identifies the connection.</summary>
    public uint Cookie { get; }

    /// <summary>
    /// Connects a sink that implements the event interface to the events of a COM object.
    /// </summary>
    /// <param name="source">
    /// The COM object: an object returned by a generated interface, a late-bound wrapper or a <c>dynamic</c> object.
    /// </param>
    /// <param name="iid">The interface identifier of the event interface.</param>
    /// <param name="sink">The sink, a <see cref="GeneratedComClassAttribute"/>-attributed class that implements the event interface.</param>
    /// <returns>The connection, which must be disposed to disconnect the sink.</returns>
    /// <exception cref="InvalidOperationException">The object does not raise the events of the interface.</exception>
    public static EventConnection Advise(object source, Guid iid, object sink)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sink);

        IConnectionPointContainer container;
        nint unknown = ComObjects.GetUnknown(source);
        try
        {
            int hr = Marshal.QueryInterface(unknown, OleNativeMethods.IID_IConnectionPointContainer, out nint pointer);
            if (hr < 0)
            {
                throw new InvalidOperationException(
                    $"The object of type {source.GetType()} does not raise events: it does not implement IConnectionPointContainer.",
                    Marshal.GetExceptionForHR(hr));
            }

            container = ComInterfaceMarshaller<IConnectionPointContainer>.ConvertToManaged((void*)pointer)!;
            Marshal.Release(pointer);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        int result = container.FindConnectionPoint(iid, out IConnectionPoint? connectionPoint);
        if (result < 0 || connectionPoint is null)
        {
            throw new InvalidOperationException(
                $"The object of type {source.GetType()} does not raise the events of the interface {iid:B}.",
                result < 0 ? Marshal.GetExceptionForHR(result) : null);
        }

        nint sinkUnknown = (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(sink);
        try
        {
            Marshal.ThrowExceptionForHR(connectionPoint.Advise(sinkUnknown, out uint cookie));
            return new EventConnection(connectionPoint, cookie);
        }
        finally
        {
            Marshal.Release(sinkUnknown);
        }
    }

    /// <summary>
    /// Connects a handler to the events of a dispinterface, which the COM object raises through <c>IDispatch::Invoke</c>.
    /// </summary>
    /// <param name="source">
    /// The COM object: an object returned by a generated interface, a late-bound wrapper or a <c>dynamic</c> object.
    /// </param>
    /// <param name="iid">The interface identifier of the dispinterface.</param>
    /// <param name="handler">The handler of the invocations.</param>
    /// <returns>The connection, which must be disposed to disconnect the handler.</returns>
    /// <exception cref="InvalidOperationException">The object does not raise the events of the interface.</exception>
    public static EventConnection AdviseDispatch(object source, Guid iid, IDispatchEventHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Advise(source, iid, new DispatchEventSink(iid, handler));
    }

    /// <summary>
    /// Disconnects the sink. Errors are ignored, e.g. when the COM object has already disconnected the sink or has been destroyed.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _connectionPoint, null) is { } connectionPoint)
        {
            connectionPoint.Unadvise(Cookie);
        }
    }
}
