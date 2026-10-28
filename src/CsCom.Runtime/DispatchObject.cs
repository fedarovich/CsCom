using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// The base class of the late-bound wrappers generated for dispinterfaces.
/// </summary>
public abstract unsafe class DispatchObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchObject"/> class.
    /// </summary>
    /// <param name="dispatch">The wrapped <see cref="IDispatch"/> interface.</param>
    protected DispatchObject(IDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        Dispatch = dispatch;
    }

    /// <summary>
    /// Gets the wrapped <see cref="IDispatch"/> interface.
    /// </summary>
    public IDispatch Dispatch { get; }

    /// <summary>Invokes a method.</summary>
    protected object? InvokeMethod(int dispId, params ReadOnlySpan<object?> args)
    {
        return InvokeCore(Dispatch, dispId, DispatchFlags.Method, args, default, default);
    }

    /// <summary>Invokes a method that has by-reference parameters. The values of by-reference arguments are updated in place.</summary>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="byRef">For each argument, whether it is passed by reference.</param>
    protected object? InvokeMethodByRef(int dispId, Span<object?> args, ReadOnlySpan<bool> byRef)
    {
        return InvokeCore(Dispatch, dispId, DispatchFlags.Method, args, args, byRef);
    }

    /// <summary>Gets the value of a property.</summary>
    protected object? InvokeGet(int dispId, params ReadOnlySpan<object?> args)
    {
        return InvokeCore(Dispatch, dispId, DispatchFlags.PropertyGet | DispatchFlags.Method, args, default, default);
    }

    /// <summary>Sets the value of a property.</summary>
    protected void InvokePut(int dispId, object? value, params ReadOnlySpan<object?> args)
    {
        InvokeSetter(dispId, DispatchFlags.PropertyPut, value, args);
    }

    /// <summary>Sets the value of a property by reference.</summary>
    protected void InvokePutRef(int dispId, object? value, params ReadOnlySpan<object?> args)
    {
        InvokeSetter(dispId, DispatchFlags.PropertyPutRef, value, args);
    }

    /// <summary>Converts an invocation result to a late-bound wrapper.</summary>
    protected static T? ToWrapper<T>(object? value) where T : class, IDispatchWrapper<T>
    {
        return value switch
        {
            null => null,
            T wrapper => wrapper,
            IDispatch dispatch => T.Create(dispatch),
            _ => throw new InvalidCastException($"The value of type {value.GetType()} does not implement IDispatch."),
        };
    }

    /// <summary>Converts an invocation result to a COM interface.</summary>
    protected static T? ToInterface<T>(object? value) where T : class
    {
        return value switch
        {
            null => null,
            DispatchObject wrapper => (T)(object)wrapper.Dispatch,
            _ => (T)value,
        };
    }

    /// <summary>Gets an enumerator for a collection that exposes a <c>_NewEnum</c> member.</summary>
    protected ComEnumerator GetEnumeratorCore()
    {
        object? enumerator = InvokeCore(Dispatch, DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet, default, default, default);
        return ComEnumerator.FromObject(enumerator);
    }

    private void InvokeSetter(int dispId, DispatchFlags flags, object? value, ReadOnlySpan<object?> args)
    {
        if (args.IsEmpty)
        {
            InvokeCore(Dispatch, dispId, flags, [value], default, default);
            return;
        }

        var allArgs = new object?[args.Length + 1];
        args.CopyTo(allArgs);
        allArgs[^1] = value;
        InvokeCore(Dispatch, dispId, flags, allArgs, default, default);
    }

    /// <summary>
    /// Invokes a member of an <see cref="IDispatch"/> interface.
    /// </summary>
    /// <param name="dispatch">The interface to invoke the member on.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="args">The arguments, in the declaration order.</param>
    /// <returns>The result of the invocation.</returns>
    public static object? Invoke(IDispatch dispatch, int dispId, DispatchFlags flags, params ReadOnlySpan<object?> args)
    {
        return InvokeCore(dispatch, dispId, flags, args, default, default);
    }

    /// <summary>
    /// Invokes a member of an <see cref="IDispatch"/> interface that has by-reference parameters.
    /// </summary>
    /// <param name="dispatch">The interface to invoke the member on.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="args">The arguments, in the declaration order. By-reference arguments are updated in place.</param>
    /// <param name="byRef">For each argument, whether it is passed by reference.</param>
    /// <returns>The result of the invocation.</returns>
    public static object? InvokeByRef(IDispatch dispatch, int dispId, DispatchFlags flags, Span<object?> args, ReadOnlySpan<bool> byRef)
    {
        return InvokeCore(dispatch, dispId, flags, args, args, byRef);
    }

    private static object? InvokeCore(IDispatch dispatch, int dispId, DispatchFlags flags, ReadOnlySpan<object?> args, Span<object?> output, ReadOnlySpan<bool> byRef)
    {
        int count = args.Length;
        if (!byRef.IsEmpty && byRef.Length != count)
        {
            throw new ArgumentException("The number of by-reference flags must match the number of arguments.", nameof(byRef));
        }

        // The arguments are passed in reverse order. By-reference arguments are passed as VT_BYREF | VT_VARIANT
        // pointing to a separate storage that receives the new values.
        var variants = new ComVariant[count];
        ComVariant[] storage = byRef.IsEmpty ? Array.Empty<ComVariant>() : new ComVariant[count];
        ComVariant result = default;
        ExcepInfo excepInfo = default;
        try
        {
            fixed (ComVariant* pVariants = variants)
            fixed (ComVariant* pStorage = storage)
            {
                for (int i = 0; i < count; i++)
                {
                    int nativeIndex = count - 1 - i;
                    if (!byRef.IsEmpty && byRef[i])
                    {
                        pStorage[i] = VariantMarshaller.ConvertToUnmanaged(args[i]);
                        pVariants[nativeIndex] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_VARIANT, (nint)(pStorage + i));
                    }
                    else
                    {
                        pVariants[nativeIndex] = VariantMarshaller.ConvertToUnmanaged(args[i]);
                    }
                }

                int putDispId = DispIds.PropertyPut;
                bool isSetter = (flags & (DispatchFlags.PropertyPut | DispatchFlags.PropertyPutRef)) != 0;
                var dispParams = new DispParams
                {
                    rgvarg = pVariants,
                    cArgs = (uint)count,
                    rgdispidNamedArgs = isSetter ? &putDispId : null,
                    cNamedArgs = isSetter ? 1u : 0u,
                };

                uint argError = 0;
                Guid iidNull = Guid.Empty;
                int hr = dispatch.Invoke(dispId, iidNull, OleNativeMethods.LOCALE_USER_DEFAULT, (ushort)flags, &dispParams, isSetter ? null : &result, &excepInfo, &argError);
                if (hr == OleNativeMethods.DISP_E_EXCEPTION)
                {
                    throw CreateException(&excepInfo);
                }

                Marshal.ThrowExceptionForHR(hr);

                for (int i = 0; i < storage.Length; i++)
                {
                    if (byRef[i])
                    {
                        output[i] = VariantMarshaller.ConvertToManaged(pStorage[i]);
                    }
                }

                return VariantMarshaller.ConvertToManaged(result);
            }
        }
        finally
        {
            for (int i = 0; i < count; i++)
            {
                variants[i].Dispose();
            }

            for (int i = 0; i < storage.Length; i++)
            {
                storage[i].Dispose();
            }

            result.Dispose();
        }
    }

    private static COMException CreateException(ExcepInfo* excepInfo)
    {
        if (excepInfo->pfnDeferredFillIn != null)
        {
            excepInfo->pfnDeferredFillIn(excepInfo);
        }

        string? source = TakeString(ref excepInfo->bstrSource);
        string? description = TakeString(ref excepInfo->bstrDescription);
        string? helpFile = TakeString(ref excepInfo->bstrHelpFile);
        int errorCode = excepInfo->scode != 0 ? excepInfo->scode : unchecked((int)(0x80040000u | excepInfo->wCode));

        var exception = new COMException(description ?? $"Exception occurred (0x{errorCode:X8}).", errorCode)
        {
            Source = source,
        };

        if (helpFile is not null)
        {
            exception.HelpLink = excepInfo->dwHelpContext != 0 ? $"{helpFile}#{excepInfo->dwHelpContext}" : helpFile;
        }

        return exception;
    }

    private static string? TakeString(ref nint bstr)
    {
        if (bstr == 0)
        {
            return null;
        }

        string? value = Marshal.PtrToStringBSTR(bstr);
        Marshal.FreeBSTR(bstr);
        bstr = 0;
        return value;
    }
}
