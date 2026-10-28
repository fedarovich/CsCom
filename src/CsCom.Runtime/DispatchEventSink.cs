using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// An event sink for dispinterfaces: a COM object that implements <c>IDispatch</c>, also under the identifier of the
/// dispinterface, and passes the invocations to an <see cref="IDispatchEventHandler"/>.
/// </summary>
[GeneratedComClass]
public sealed unsafe partial class DispatchEventSink : IDispatch, ICustomQueryInterface
{
    private const int DispIdUnknown = -1;

    private readonly Guid _iid;
    private readonly IDispatchEventHandler _handler;

    internal DispatchEventSink(Guid iid, IDispatchEventHandler handler)
    {
        _iid = iid;
        _handler = handler;
    }

    /// <summary>
    /// Passes an invocation of <c>IDispatch::Invoke</c> to a handler. An exception thrown by the handler is returned as
    /// <c>DISP_E_EXCEPTION</c> with the error information.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="parameters">The arguments.</param>
    /// <param name="result">The variable that receives the result, if any.</param>
    /// <param name="excepInfo">The structure that receives the error information, if any.</param>
    /// <returns>The <c>HRESULT</c> of the invocation.</returns>
    public static int InvokeHandler(IDispatchEventHandler handler, int dispId, DispParams* parameters, ComVariant* result, ExcepInfo* excepInfo)
    {
        ArgumentNullException.ThrowIfNull(handler);
        try
        {
            return handler.Invoke(dispId, new EventArguments(parameters, result)) ? OleNativeMethods.S_OK : OleNativeMethods.DISP_E_MEMBERNOTFOUND;
        }
        catch (Exception e)
        {
            if (excepInfo is null)
            {
                return e.HResult;
            }

            *excepInfo = default;
            excepInfo->bstrSource = Marshal.StringToBSTR(e.Source ?? string.Empty);
            excepInfo->bstrDescription = Marshal.StringToBSTR(e.Message);
            excepInfo->scode = e.HResult;
            return OleNativeMethods.DISP_E_EXCEPTION;
        }
    }

    /// <inheritdoc/>
    int IDispatch.GetTypeInfoCount(out uint pctinfo)
    {
        pctinfo = 0;
        return OleNativeMethods.S_OK;
    }

    /// <inheritdoc/>
    int IDispatch.GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
    {
        ppTInfo = 0;
        return OleNativeMethods.E_NOTIMPL;
    }

    /// <inheritdoc/>
    int IDispatch.GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId)
    {
        for (uint i = 0; i < cNames; i++)
        {
            rgDispId[i] = DispIdUnknown;
        }

        return OleNativeMethods.DISP_E_UNKNOWNNAME;
    }

    /// <inheritdoc/>
    int IDispatch.Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr)
    {
        return InvokeHandler(_handler, dispIdMember, pDispParams, pVarResult, pExcepInfo);
    }

    /// <inheritdoc/>
    CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out nint ppv)
    {
        // The connection point asks for the dispinterface, which is implemented by IDispatch.
        if (iid == _iid)
        {
            ppv = (nint)ComInterfaceMarshaller<IDispatch>.ConvertToUnmanaged(this);
            return CustomQueryInterfaceResult.Handled;
        }

        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }
}
