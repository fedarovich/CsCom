using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// How an argument of <c>IDispatch::Invoke</c> is passed.
/// </summary>
internal enum ByRefKind : byte
{
    /// <summary>By value.</summary>
    None,

    /// <summary>By reference, as a <c>VT_BYREF | VT_VARIANT</c>.</summary>
    Variant,

    /// <summary>
    /// By reference to a value of its own type, e.g. <c>VT_BYREF | VT_I4</c> for an <see cref="int"/>, which typed by-reference
    /// parameters (<c>long*</c>) require; as a <c>VT_BYREF | VT_VARIANT</c> when the value has no such type, e.g. <see langword="null"/>.
    /// </summary>
    Typed,
}

/// <summary>
/// Resolves and invokes the members of <see cref="IDispatch"/> interfaces.
/// </summary>
internal static unsafe class DispatchInvoker
{
    /// <summary>
    /// Invokes a member and throws an exception if the invocation fails.
    /// </summary>
    /// <param name="dispatch">The interface to invoke the member on.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="args">The arguments, in the declaration order; the named arguments follow the positional ones.</param>
    /// <param name="namedDispIds">The dispatch identifiers of the trailing named arguments.</param>
    /// <param name="output">Receives the new values of the by-reference arguments; usually the same span as <paramref name="args"/>.</param>
    /// <param name="byRef">For each argument, how it is passed. Empty when all of them are passed by value.</param>
    /// <param name="memberName">The name of the member for error messages, if known.</param>
    /// <returns>The result of the invocation.</returns>
    /// <exception cref="COMException">The invocation failed or the COM object raised an exception.</exception>
    public static object? Invoke(
        IDispatch dispatch,
        int dispId,
        DispatchFlags flags,
        ReadOnlySpan<object?> args,
        ReadOnlySpan<int> namedDispIds = default,
        Span<object?> output = default,
        ReadOnlySpan<ByRefKind> byRef = default,
        string? memberName = null)
    {
        int hr = TryInvoke(dispatch, dispId, flags, args, namedDispIds, output, byRef, out object? result, out int argument);
        if (hr < 0)
        {
            ThrowInvocationException(hr, dispId, memberName, flags, args.Length, argument);
        }

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
    /// <param name="byRef">For each argument, how it is passed. Empty when all of them are passed by value.</param>
    /// <param name="result">The result of the invocation.</param>
    /// <param name="argument">
    /// The index in <paramref name="args"/> of the argument that caused a <c>DISP_E_TYPEMISMATCH</c> or <c>DISP_E_PARAMNOTFOUND</c>
    /// error, or -1.
    /// </param>
    /// <returns>The <c>HRESULT</c> returned by <c>IDispatch::Invoke</c>.</returns>
    /// <exception cref="COMException">The COM object raised an exception.</exception>
    public static int TryInvoke(
        IDispatch dispatch,
        int dispId,
        DispatchFlags flags,
        ReadOnlySpan<object?> args,
        ReadOnlySpan<int> namedDispIds,
        Span<object?> output,
        ReadOnlySpan<ByRefKind> byRef,
        out object? result,
        out int argument)
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
        // By-reference arguments point to a separate storage that receives the new values: VT_BYREF | VT_VARIANT points to
        // the storage variant, and a typed reference such as VT_BYREF | VT_I4 points to its data.
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
                    ByRefKind kind = byRef.IsEmpty ? ByRefKind.None : byRef[i];
                    if (kind == ByRefKind.None)
                    {
                        pVariants[nativeIndex] = VariantMarshaller.ConvertToUnmanaged(args[i]);
                        continue;
                    }

                    pStorage[i] = VariantMarshaller.ConvertToUnmanaged(args[i]);
                    VarEnum type = pStorage[i].VarType;
                    pVariants[nativeIndex] = kind == ByRefKind.Typed && HasTypedReference(type)
                        ? ComVariant.CreateRaw(VarEnum.VT_BYREF | type, (nint)Unsafe.AsPointer(ref pStorage[i].GetRawDataRef<byte>()))
                        : ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_VARIANT, (nint)(pStorage + i));
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
                    argument = (hr is OleNativeMethods.DISP_E_TYPEMISMATCH or OleNativeMethods.DISP_E_PARAMNOTFOUND) && argError < (uint)count
                        ? (argError < (uint)named ? positional + (int)argError : positional - 1 - ((int)argError - named))
                        : -1;
                    return hr;
                }

                for (int i = 0; i < storage.Length; i++)
                {
                    if (byRef[i] != ByRefKind.None)
                    {
                        output[i] = VariantMarshaller.ConvertToManaged(pStorage[i]);
                    }
                }

                result = VariantMarshaller.ConvertToManaged(nativeResult);
                argument = -1;
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
    /// Converts by-reference flags to <see cref="ByRefKind.Variant"/> references.
    /// </summary>
    public static ByRefKind[] ToVariantReferences(ReadOnlySpan<bool> byRef)
    {
        var kinds = new ByRefKind[byRef.Length];
        for (int i = 0; i < byRef.Length; i++)
        {
            kinds[i] = byRef[i] ? ByRefKind.Variant : ByRefKind.None;
        }

        return kinds;
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

    /// <summary>
    /// Throws the exception for a failed invocation: a <see cref="COMException"/> that names the member and the argument for
    /// the common errors of <c>IDispatch::Invoke</c>, otherwise the exception of the <c>HRESULT</c>.
    /// </summary>
    /// <param name="hr">The <c>HRESULT</c>.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="memberName">The name of the member, if known.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="count">The number of arguments.</param>
    /// <param name="argument">The index of the argument that caused the error, or -1.</param>
    [DoesNotReturn]
    public static void ThrowInvocationException(int hr, int dispId, string? memberName, DispatchFlags flags, int count, int argument)
    {
        string member = memberName is not null
            ? $"'{memberName}'"
            : dispId switch
            {
                DispIds.Value => "the default member",
                DispIds.NewEnum => "'_NewEnum'",
                _ => $"the member with DISPID {dispId}",
            };

        string? message = hr switch
        {
            OleNativeMethods.DISP_E_TYPEMISMATCH when argument >= 0 => $"Type mismatch in argument {argument + 1} of {member}.",
            OleNativeMethods.DISP_E_PARAMNOTFOUND when argument >= 0 => $"Parameter not found for argument {argument + 1} of {member}.",
            OleNativeMethods.DISP_E_BADPARAMCOUNT => $"{Capitalize(member)} does not take {count} argument{(count == 1 ? string.Empty : "s")}.",
            OleNativeMethods.DISP_E_MEMBERNOTFOUND => $"{Capitalize(member)} cannot be {DescribeAccess(flags)}.",
            _ => null,
        };

        if (message is null)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        throw new COMException(message, hr);
    }

    private static string Capitalize(string text) => string.Concat(char.ToUpperInvariant(text[0]).ToString(), text.AsSpan(1));

    private static string DescribeAccess(DispatchFlags flags) =>
        (flags & (DispatchFlags.PropertyPut | DispatchFlags.PropertyPutRef)) != 0 ? "assigned"
        : (flags & (DispatchFlags.Method | DispatchFlags.PropertyGet)) == (DispatchFlags.Method | DispatchFlags.PropertyGet) ? "read or called"
        : (flags & DispatchFlags.PropertyGet) != 0 ? "read"
        : "called";

    // The types that have typed references (VT_BYREF | type) pointing to the data of a VARIANT. VT_DECIMAL overlays the
    // whole VARIANT, and empty and null values have no type, so they are passed as VT_BYREF | VT_VARIANT.
    private static bool HasTypedReference(VarEnum type) =>
        (type & VarEnum.VT_ARRAY) != 0
        || type is VarEnum.VT_I1 or VarEnum.VT_UI1 or VarEnum.VT_I2 or VarEnum.VT_UI2 or VarEnum.VT_I4 or VarEnum.VT_UI4
            or VarEnum.VT_I8 or VarEnum.VT_UI8 or VarEnum.VT_INT or VarEnum.VT_UINT or VarEnum.VT_R4 or VarEnum.VT_R8
            or VarEnum.VT_CY or VarEnum.VT_DATE or VarEnum.VT_BSTR or VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN
            or VarEnum.VT_BOOL or VarEnum.VT_ERROR;

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
