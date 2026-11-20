using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// An event sink for dispinterfaces: a COM object that implements <c>IDispatch</c>, also under the identifier of the
/// dispinterface, and passes the invocations to an <see cref="IDispatchEventHandler"/> until the gate of its connection is
/// closed.
/// </summary>
[GeneratedComClass]
public sealed unsafe partial class DispatchEventSink : IDispatch, ICustomQueryInterface
{
    private const int DispIdUnknown = -1;

    private readonly Guid _iid;
    private readonly IDispatchEventHandler _handler;
    private readonly EventGate _gate;

    internal DispatchEventSink(Guid iid, IDispatchEventHandler handler, EventGate gate)
    {
        _iid = iid;
        _handler = handler;
        _gate = gate;
    }

    /// <summary>
    /// Passes an invocation of <c>IDispatch::Invoke</c> to a handler. An exception thrown by the handler is returned as
    /// <c>DISP_E_EXCEPTION</c> with the error information, or as its <see cref="Exception.HResult"/> when the caller
    /// expects none; as <c>E_FAIL</c> when that is no error code.
    /// </summary>
    /// <param name="handler">The handler.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="parameters">The arguments.</param>
    /// <param name="result">The variable that receives the result, if any.</param>
    /// <param name="excepInfo">The structure that receives the error information, if any.</param>
    /// <returns>The <c>HRESULT</c> of the invocation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
    public static int InvokeHandler(IDispatchEventHandler handler, int dispId, DispParams* parameters, ComVariant* result, ExcepInfo* excepInfo)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // The result is an [out] parameter, so the variable of the caller may not be initialized: it is not read or cleared.
        if (result is not null)
        {
            *result = default;
        }

        try
        {
            return handler.Invoke(dispId, new EventArguments(parameters, result)) ? OleNativeMethods.S_OK : OleNativeMethods.DISP_E_MEMBERNOTFOUND;
        }
        catch (Exception e)
        {
            return Fail(e, result, excepInfo);
        }
    }

    /// <summary>
    /// Passes an invocation of <c>IDispatch::Invoke</c> to a handler while the gate of the connection is open, as a sink
    /// does that is created with <see cref="EventConnection.Advise(object, Guid, Func{EventGate, object})"/> and implements
    /// <c>IDispatch</c>, e.g. for a dual event interface. A closed gate accepts the invocation without passing it on,
    /// whatever its dispatch identifier, since the handler is not asked which events it knows any more, like a vtable
    /// sink accepts every call: the result, if the caller expects one, is empty.
    /// </summary>
    /// <remarks>
    /// The last invocation that exits a closed gate runs the completions of
    /// <see cref="EventConnection.DisposeWithCompletion"/>: their exception is returned like an exception of the handler,
    /// instead of the result of the handler, also when the handler failed, as a <c>finally</c> block would throw it.
    /// </remarks>
    /// <param name="gate">The gate of the connection of the sink.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="parameters">The arguments.</param>
    /// <param name="result">The variable that receives the result, if any.</param>
    /// <param name="excepInfo">The structure that receives the error information, if any.</param>
    /// <returns>The <c>HRESULT</c> of the invocation; <c>S_OK</c> when the gate is closed, also for an unknown member.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="gate"/> or <paramref name="handler"/> is <see langword="null"/>, also when the gate is closed.
    /// </exception>
    public static int InvokeHandler(EventGate gate, IDispatchEventHandler handler, int dispId, DispParams* parameters, ComVariant* result, ExcepInfo* excepInfo)
    {
        // Before the gate is entered: the call of the handler below would throw before it initializes the result, which
        // Fail disposes when a completion that Exit runs throws, and the result may be uninitialized memory of the caller.
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(handler);
        if (!gate.TryEnter())
        {
            return Ignore(result);
        }

        // InvokeHandler returns every exception of the handler, also one whose error information cannot be filled in, so
        // the call returns, and the exception of a completion below replaces its result. The value is only assigned for
        // the finally block.
        int hr = OleNativeMethods.E_FAIL;
        try
        {
            hr = InvokeHandler(handler, dispId, parameters, result, excepInfo);
        }
        finally
        {
            try
            {
                gate.Exit();
            }
            catch (Exception e)
            {
                // A completion of the connection failed after the handler: its exception replaces the result of the handler,
                // also an error, as a finally block would throw it, and as the vtable members of the generated sinks do.
                // The error information of the handler, which only Fail fills in with DISP_E_EXCEPTION, is freed first.
                if (hr == OleNativeMethods.DISP_E_EXCEPTION && excepInfo is not null)
                {
                    Marshal.FreeBSTR(excepInfo->bstrSource);
                    Marshal.FreeBSTR(excepInfo->bstrDescription);
                    Marshal.FreeBSTR(excepInfo->bstrHelpFile);
                    *excepInfo = default;
                }

                hr = Fail(e, result, excepInfo);
            }
        }

        return hr;
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
    int IDispatch.Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        InvokeHandler(_gate, _handler, dispIdMember, pDispParams, pVarResult, pExcepInfo);

    // Returns an exception as the failure of an invocation, with the error information when the caller expects it. It does
    // not throw, so that the exception of the handler is always reported, and InvokeHandler returns.
    private static int Fail(Exception exception, ComVariant* result, ExcepInfo* excepInfo)
    {
        // A failed invocation has no result, and the caller does not release one. The handler set it through
        // EventArguments, and ComVariant.Dispose ignores the result of PropVariantClear.
        if (result is not null)
        {
            result->Dispose();
            *result = default;
        }

        // An exception is a failure, also one whose HResult is no error code, which would read as a success.
        int hr = OleNativeMethods.GetFailure(exception);
        if (excepInfo is null)
        {
            return hr;
        }

        // The caller frees the strings with DISP_E_EXCEPTION, and only then: they are set once both are allocated, or not
        // at all, e.g. when the Message of the exception throws, and the error is reported without them then.
        *excepInfo = default;
        excepInfo->scode = hr;
        nint source = 0;
        try
        {
            source = Marshal.StringToBSTR(exception.Source ?? string.Empty);
            excepInfo->bstrDescription = Marshal.StringToBSTR(exception.Message);
            excepInfo->bstrSource = source;
        }
        catch (Exception)
        {
            Marshal.FreeBSTR(source);
        }

        return OleNativeMethods.DISP_E_EXCEPTION;
    }

    // Accepts an invocation of a closed sink without passing it on: the result, if the caller expects one, is empty.
    private static int Ignore(ComVariant* result)
    {
        // The result is an [out] parameter, so the variable of the caller may not be initialized.
        if (result is not null)
        {
            *result = default;
        }

        return OleNativeMethods.S_OK;
    }

    /// <inheritdoc/>
    CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out nint ppv)
    {
        // The connection point asks for the dispinterface, which is implemented by IDispatch. The interfaces of the class are
        // left to its table, also when they are the event interface: IDispatch, for which ToUnmanaged would ask this method
        // again, without end, and IUnknown, the identity of the object.
        if (iid == _iid && iid != OleNativeMethods.IID_IDispatch && iid != OleNativeMethods.IID_IUnknown)
        {
            ppv = (nint)ComMarshalling.ToUnmanaged<IDispatch>(this);
            return CustomQueryInterfaceResult.Handled;
        }

        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }
}
