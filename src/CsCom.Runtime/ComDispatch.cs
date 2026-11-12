using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CsCom.Runtime;

/// <summary>
/// How an argument of <see cref="ComDispatch.Invoke"/> is passed.
/// </summary>
public enum DispatchArgumentKind : byte
{
    /// <summary>By value.</summary>
    Value,

    /// <summary>By reference, as a <c>VT_BYREF | VT_VARIANT</c>.</summary>
    Variant,

    /// <summary>
    /// By reference to a value of its own type, e.g. <c>VT_BYREF | VT_I4</c> for an <see cref="int"/>, which typed by-reference
    /// parameters (<c>long*</c>) require; as a <c>VT_BYREF | VT_VARIANT</c> when the value has no such type, e.g. <see langword="null"/>.
    /// </summary>
    Typed,
}

/// <summary>
/// How the type information of an object describes a member, see <see cref="ComDispatch.GetMemberKinds"/>.
/// </summary>
[Flags]
public enum DispatchMemberKinds
{
    /// <summary>The member is not described.</summary>
    None = 0,

    /// <summary>The member is invoked as a method.</summary>
    Method = 1,

    /// <summary>The member is a property that can be read.</summary>
    Property = 2,

    /// <summary>The getter of the property takes arguments, required or optional.</summary>
    IndexedProperty = 4,
}

/// <summary>
/// Resolves and invokes the members of <see cref="IDispatch"/> interfaces by name and dispatch identifier, and reads their type
/// information: the late binding that the generated dispinterface wrappers and <c>dynamic</c> objects build on.
/// </summary>
public static class ComDispatch
{
    /// <summary>
    /// Maps a member name and, optionally, the names of its parameters to dispatch identifiers (<c>IDispatch::GetIDsOfNames</c>).
    /// </summary>
    /// <param name="dispatch">The interface.</param>
    /// <param name="names">The name of the member, followed by the names of parameters.</param>
    /// <param name="ids">Receives the dispatch identifiers; <see cref="DispIds.Unknown"/> for the unknown names.</param>
    /// <returns>The <c>HRESULT</c>; <c>DISP_E_UNKNOWNNAME</c> when one of the names is unknown.</returns>
    public static int GetDispIds(IDispatch dispatch, ReadOnlySpan<string> names, Span<int> ids)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return DispatchInvoker.GetDispIds(dispatch, names, ids);
    }

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
        ReadOnlySpan<DispatchArgumentKind> byRef = default,
        string? memberName = null)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return DispatchInvoker.Invoke(dispatch, dispId, flags, args, namedDispIds, output, ToByRefKinds(byRef), memberName);
    }

    /// <summary>
    /// Invokes a member and returns the <c>HRESULT</c>. Exceptions raised by the COM object (<c>DISP_E_EXCEPTION</c>) are still thrown.
    /// </summary>
    /// <param name="dispatch">The interface to invoke the member on.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="args">The arguments, in the declaration order; the named arguments follow the positional ones.</param>
    /// <param name="namedDispIds">The dispatch identifiers of the trailing named arguments.</param>
    /// <param name="output">Receives the new values of the by-reference arguments; usually the same span as <paramref name="args"/>.</param>
    /// <param name="byRef">For each argument, how it is passed. Empty when all of them are passed by value.</param>
    /// <param name="result">The result of the invocation.</param>
    /// <param name="argumentError">
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
        ReadOnlySpan<DispatchArgumentKind> byRef,
        out object? result,
        out int argumentError)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return DispatchInvoker.TryInvoke(dispatch, dispId, flags, args, namedDispIds, output, ToByRefKinds(byRef), out result, out argumentError);
    }

    /// <summary>
    /// Creates the exception of a failed invocation, as <see cref="Invoke"/> throws it: a <see cref="COMException"/> that names
    /// the member and the argument for the common errors of <c>IDispatch::Invoke</c>, otherwise the exception of the <c>HRESULT</c>.
    /// </summary>
    /// <param name="hr">The <c>HRESULT</c> returned by <see cref="TryInvoke"/>.</param>
    /// <param name="dispId">The dispatch identifier of the member.</param>
    /// <param name="memberName">The name of the member, if known.</param>
    /// <param name="flags">The kind of the invocation.</param>
    /// <param name="argumentCount">The number of arguments.</param>
    /// <param name="argumentError">The index of the argument that caused the error, or -1.</param>
    public static Exception CreateInvocationException(int hr, int dispId, string? memberName, DispatchFlags flags, int argumentCount, int argumentError)
    {
        try
        {
            DispatchInvoker.ThrowInvocationException(hr, dispId, memberName, flags, argumentCount, argumentError);
        }
        catch (Exception e)
        {
            return e;
        }

        return new COMException(null, hr);
    }

    /// <summary>
    /// Gets the name of the type of an object from its type information (<c>IDispatch::GetTypeInfo</c>), like VB's
    /// <c>TypeName</c>, e.g. the name of the default interface of its class.
    /// </summary>
    /// <returns>The name, or <see langword="null"/> if the object provides no type information.</returns>
    public static string? GetTypeName(IDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return ComTypeInfo.GetTypeInfo(dispatch) is { } typeInfo ? ComTypeInfo.GetTypeName(typeInfo) : null;
    }

    /// <summary>
    /// Gets the name of a member of an object from its type information, e.g. the name of the default member
    /// (<see cref="DispIds.Value"/>), which <see cref="GetDispIds"/> needs to resolve the names of its parameters.
    /// </summary>
    /// <returns>The name, or <see langword="null"/> if the object provides no type information or the type has no such member.</returns>
    public static string? GetMemberName(IDispatch dispatch, int dispId)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return ComTypeInfo.GetTypeInfo(dispatch) is { } typeInfo ? ComTypeInfo.GetMemberName(typeInfo, dispId) : null;
    }

    /// <summary>
    /// Gets how the type information of an object describes a member: as a method, as a property that can be read, and
    /// whether the getter of the property takes arguments. Late-bound callers that read a member can then return something
    /// to call or to index rather than invoke it.
    /// </summary>
    /// <returns>
    /// The kinds, or <see cref="DispatchMemberKinds.None"/> for an unknown member and if the object provides no type information.
    /// </returns>
    public static DispatchMemberKinds GetMemberKinds(IDispatch dispatch, int dispId)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return ComTypeInfo.GetTypeInfo(dispatch) is { } typeInfo ? ComTypeInfo.GetMemberKinds(typeInfo, dispId) : DispatchMemberKinds.None;
    }

    /// <summary>
    /// Gets the names of the properties of an object that can be read without arguments, including those of the base
    /// interfaces, except for restricted and hidden ones.
    /// </summary>
    /// <returns>The names, or none if the object provides no type information.</returns>
    public static IReadOnlyList<string> GetPropertyNames(IDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return ComTypeInfo.GetTypeInfo(dispatch) is { } typeInfo ? ComTypeInfo.GetPropertyNames(typeInfo) : [];
    }

    /// <summary>
    /// Gets the source dispinterfaces of the coclass of an object, the default one first: the coclass is given by
    /// <c>IProvideClassInfo</c>, or is the first coclass of the type library whose default interface is the interface of
    /// <c>IDispatch::GetTypeInfo</c>. Dual and vtable source interfaces are not included.
    /// </summary>
    public static IReadOnlyList<EventInterface> GetEventInterfaces(IDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return ComTypeInfo.GetEventInterfaces(dispatch);
    }

    // DispatchArgumentKind has the values of the internal ByRefKind.
    private static ReadOnlySpan<ByRefKind> ToByRefKinds(ReadOnlySpan<DispatchArgumentKind> kinds) =>
        MemoryMarshal.Cast<DispatchArgumentKind, ByRefKind>(kinds);
}
