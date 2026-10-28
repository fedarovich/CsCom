using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Resolves and invokes the members of <see cref="IDispatch"/> interfaces.
/// </summary>
internal static unsafe class DispatchInvoker
{
    /// <summary>
    /// Invokes a member and throws an exception if the invocation fails.
    /// </summary>
    /// <inheritdoc cref="TryInvoke"/>
    public static object? Invoke(
        IDispatch dispatch,
        int dispId,
        DispatchFlags flags,
        ReadOnlySpan<object?> args,
        ReadOnlySpan<int> namedDispIds = default,
        Span<object?> output = default,
        ReadOnlySpan<bool> byRef = default)
    {
        Marshal.ThrowExceptionForHR(TryInvoke(dispatch, dispId, flags, args, namedDispIds, output, byRef, out object? result));
        return result;
    }

    /// <summary>
    /// Invokes a member and returns the <c>HRESULT</c>. Exceptions raised by the COM object (<c>DISP_E_EXCEPTION</c>) are still thrown.
    /// </summary>
    /// <param name="dispatch">The interface to invoke the member on.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="args">The arguments, in the declaration order; the named arguments follow the positional ones.</param>
    /// <param name="namedDispIds">
    /// The dispatch identifiers of the trailing named arguments. For property puts without named arguments,
    /// the last argument is passed as the <c>DISPID_PROPERTYPUT</c> named argument.
    /// </param>
    /// <param name="output">Receives the new values of the by-reference arguments; usually the same span as <paramref name="args"/>.</param>
    /// <param name="byRef">For each argument, whether it is passed by reference. Empty when none of them is.</param>
    /// <param name="result">The result of the invocation.</param>
    /// <returns>The <c>HRESULT</c> returned by <c>IDispatch::Invoke</c>.</returns>
    /// <exception cref="COMException">The COM object raised an exception.</exception>
    public static int TryInvoke(
        IDispatch dispatch,
        int dispId,
        DispatchFlags flags,
        ReadOnlySpan<object?> args,
        ReadOnlySpan<int> namedDispIds,
        Span<object?> output,
        ReadOnlySpan<bool> byRef,
        out object? result)
    {
        int count = args.Length;
        if (!byRef.IsEmpty && byRef.Length != count)
        {
            throw new ArgumentException("The number of by-reference flags must match the number of arguments.", nameof(byRef));
        }

        bool isSetter = (flags & (DispatchFlags.PropertyPut | DispatchFlags.PropertyPutRef)) != 0;
        int putDispId = DispIds.PropertyPut;
        if (isSetter && namedDispIds.IsEmpty)
        {
            namedDispIds = new ReadOnlySpan<int>(&putDispId, 1);
        }

        if (namedDispIds.Length > count)
        {
            throw new ArgumentException("There are more named arguments than arguments.", nameof(namedDispIds));
        }

        // The named arguments come first, in the order of their identifiers; the positional arguments follow in reverse order.
        // By-reference arguments are passed as VT_BYREF | VT_VARIANT pointing to a separate storage that receives the new values.
        int named = namedDispIds.Length;
        int positional = count - named;
        var variants = new ComVariant[count];
        ComVariant[] storage = byRef.IsEmpty ? Array.Empty<ComVariant>() : new ComVariant[count];
        ComVariant nativeResult = default;
        ExcepInfo excepInfo = default;
        try
        {
            fixed (ComVariant* pVariants = variants)
            fixed (ComVariant* pStorage = storage)
            fixed (int* pNamed = namedDispIds)
            {
                for (int i = 0; i < count; i++)
                {
                    int nativeIndex = i < positional ? named + (positional - 1 - i) : i - positional;
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

                var dispParams = new DispParams
                {
                    rgvarg = pVariants,
                    cArgs = (uint)count,
                    rgdispidNamedArgs = named > 0 ? pNamed : null,
                    cNamedArgs = (uint)named,
                };

                uint argError = 0;
                Guid iidNull = Guid.Empty;
                int hr = dispatch.Invoke(dispId, iidNull, OleNativeMethods.LOCALE_USER_DEFAULT, (ushort)flags, &dispParams, isSetter ? null : &nativeResult, &excepInfo, &argError);
                if (hr == OleNativeMethods.DISP_E_EXCEPTION)
                {
                    throw CreateException(&excepInfo);
                }

                if (hr < 0)
                {
                    result = null;
                    return hr;
                }

                for (int i = 0; i < storage.Length; i++)
                {
                    if (byRef[i])
                    {
                        output[i] = VariantMarshaller.ConvertToManaged(pStorage[i]);
                    }
                }

                result = VariantMarshaller.ConvertToManaged(nativeResult);
                return hr;
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

            nativeResult.Dispose();
        }
    }

    /// <summary>
    /// Maps a member name and, optionally, the names of its parameters to dispatch identifiers (<c>IDispatch::GetIDsOfNames</c>).
    /// </summary>
    /// <returns>The <c>HRESULT</c>; <c>DISP_E_UNKNOWNNAME</c> when one of the names is unknown.</returns>
    public static int GetDispIds(IDispatch dispatch, ReadOnlySpan<string> names, Span<int> ids)
    {
        if (ids.Length < names.Length)
        {
            throw new ArgumentException("The span of identifiers is too short.", nameof(ids));
        }

        var pointers = new nint[names.Length];
        try
        {
            for (int i = 0; i < names.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUni(names[i]);
            }

            Guid iidNull = Guid.Empty;
            fixed (nint* pNames = pointers)
            fixed (int* pIds = ids)
            {
                return dispatch.GetIDsOfNames(iidNull, (char**)pNames, (uint)names.Length, OleNativeMethods.LOCALE_USER_DEFAULT, pIds);
            }
        }
        finally
        {
            foreach (nint pointer in pointers)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
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
