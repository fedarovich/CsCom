using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// An event (a method of a source dispinterface) of a COM object.
/// </summary>
/// <param name="Name">The name of the event.</param>
/// <param name="DispId">The dispatch identifier of the event.</param>
/// <param name="ParameterCount">The number of parameters.</param>
internal readonly record struct EventMember(string Name, int DispId, int ParameterCount);

/// <summary>
/// A source dispinterface of a coclass, through which its objects raise events.
/// </summary>
/// <param name="Iid">The interface identifier.</param>
/// <param name="Events">The events by name, ignoring case.</param>
internal sealed record EventInterface(Guid Iid, IReadOnlyDictionary<string, EventMember> Events)
{
    /// <summary>Gets the dispatch identifiers of the events.</summary>
    public HashSet<int> DispIds { get; } = [.. Events.Values.Select(static e => e.DispId)];
}

/// <summary>
/// Reads the type information (<c>ITypeInfo</c>) of COM objects.
/// </summary>
internal static unsafe class ComTypeInfo
{
    private const int MemberIdNil = -1;
    private const int TKIND_INTERFACE = 3;
    private const int TKIND_DISPATCH = 4;
    private const int TKIND_COCLASS = 5;
    private const int INVOKE_FUNC = 1;
    private const int INVOKE_PROPERTYGET = 2;
    private const int VAR_DISPATCH = 3;
    private const ushort FUNCFLAG_FRESTRICTED = 0x1;
    private const ushort FUNCFLAG_FHIDDEN = 0x40;
    private const ushort VARFLAG_FHIDDEN = 0x40;
    private const ushort VARFLAG_FRESTRICTED = 0x80;
    private const ushort TYPEFLAG_FDUAL = 0x40;
    private const int IMPLTYPEFLAG_FDEFAULT = 0x1;
    private const int IMPLTYPEFLAG_FSOURCE = 0x2;
    private const ushort PARAMFLAG_FLCID = 0x4;
    private const ushort PARAMFLAG_FRETVAL = 0x8;
    private const ushort PARAMFLAG_FOPT = 0x10;
    private const ushort PARAMFLAG_FHASDEFAULT = 0x20;

    // The event interfaces of the coclasses, by class identifier.
    private static readonly ConcurrentDictionary<Guid, EventInterface[]> EventInterfaces = new();

    /// <summary>
    /// Gets the type information of an object through <c>IDispatch::GetTypeInfo</c>, or <see langword="null"/> if it has none.
    /// </summary>
    public static ITypeInfo? GetTypeInfo(IDispatch dispatch)
    {
        if (dispatch.GetTypeInfoCount(out uint count) < 0 || count == 0
            || dispatch.GetTypeInfo(0, OleNativeMethods.LOCALE_USER_DEFAULT, out nint pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return ComInterfaceMarshaller<ITypeInfo>.ConvertToManaged((void*)pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>
    /// Gets the name of a type, or <see langword="null"/> if it is unknown.
    /// </summary>
    public static string? GetTypeName(ITypeInfo typeInfo)
    {
        nint name = 0;
        return typeInfo.GetDocumentation(MemberIdNil, &name, null, null, null) >= 0 ? TakeString(name) : null;
    }

    /// <summary>
    /// Gets the names of the properties that can be read without arguments, including those of the base interfaces, except
    /// for restricted and hidden ones.
    /// </summary>
    public static List<string> GetPropertyNames(ITypeInfo typeInfo)
    {
        var names = new List<string>();
        AddPropertyNames(typeInfo, names, new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);
        return names;
    }

    /// <summary>
    /// Gets the source dispinterfaces of the coclass of an object, the default one first: the coclass is given by
    /// <c>IProvideClassInfo</c>, or is the first coclass of the type library whose default interface is the interface of
    /// <c>IDispatch::GetTypeInfo</c>. Dual and vtable source interfaces are not included.
    /// </summary>
    public static EventInterface[] GetEventInterfaces(IDispatch dispatch)
    {
        ITypeInfo? coClass = GetClassInfo(dispatch) ?? FindClass(GetTypeInfo(dispatch));
        if (coClass is null)
        {
            return [];
        }

        return EventInterfaces.GetOrAdd(GetTypeAttr(coClass).guid, static (_, coClass) => ReadEventInterfaces(coClass), coClass);
    }

    private static ITypeInfo? GetClassInfo(IDispatch dispatch)
    {
        if (dispatch is not IProvideClassInfo provider || provider.GetClassInfo(out nint pointer) < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return ComInterfaceMarshaller<ITypeInfo>.ConvertToManaged((void*)pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static ITypeInfo? FindClass(ITypeInfo? typeInfo)
    {
        if (typeInfo is null || typeInfo.GetContainingTypeLib(out ITypeLib? typeLib, out _) < 0 || typeLib is null)
        {
            return null;
        }

        Guid iid = GetTypeAttr(typeInfo).guid;
        uint count = typeLib.GetTypeInfoCount();
        for (uint i = 0; i < count; i++)
        {
            if (typeLib.GetTypeInfoType(i, out int kind) >= 0 && kind == TKIND_COCLASS
                && typeLib.GetTypeInfo(i, out ITypeInfo? coClass) >= 0 && coClass is not null
                && GetDefaultInterface(coClass) == iid)
            {
                return coClass;
            }
        }

        return null;
    }

    private static Guid? GetDefaultInterface(ITypeInfo coClass)
    {
        Guid? first = null;
        int count = GetTypeAttr(coClass).cImplTypes;
        for (uint i = 0; i < count; i++)
        {
            if (coClass.GetImplTypeFlags(i, out int flags) < 0 || (flags & IMPLTYPEFLAG_FSOURCE) != 0
                || GetImplementedType(coClass, i) is not { } implemented)
            {
                continue;
            }

            Guid guid = GetTypeAttr(implemented).guid;
            if ((flags & IMPLTYPEFLAG_FDEFAULT) != 0)
            {
                return guid;
            }

            first ??= guid;
        }

        return first;
    }

    private static EventInterface[] ReadEventInterfaces(ITypeInfo coClass)
    {
        var result = new List<EventInterface>();
        int count = GetTypeAttr(coClass).cImplTypes;
        for (uint i = 0; i < count; i++)
        {
            if (coClass.GetImplTypeFlags(i, out int flags) < 0 || (flags & IMPLTYPEFLAG_FSOURCE) == 0
                || GetImplementedType(coClass, i) is not { } source)
            {
                continue;
            }

            // DispatchEventSink implements only IDispatch, so the sources that call the vtables of dual interfaces are skipped.
            TypeAttr attr = GetTypeAttr(source);
            if (attr.typekind != TKIND_DISPATCH || (attr.wTypeFlags & TYPEFLAG_FDUAL) != 0)
            {
                continue;
            }

            var events = new Dictionary<string, EventMember>(StringComparer.OrdinalIgnoreCase);
            for (uint j = 0; j < attr.cFuncs; j++)
            {
                if (source.GetFuncDesc(j, out FuncDesc* func) < 0)
                {
                    continue;
                }

                try
                {
                    if (func->invkind == INVOKE_FUNC && (func->wFuncFlags & FUNCFLAG_FRESTRICTED) == 0
                        && GetMemberName(source, func->memid) is { } name)
                    {
                        events.TryAdd(name, new EventMember(name, func->memid, func->cParams));
                    }
                }
                finally
                {
                    source.ReleaseFuncDesc(func);
                }
            }

            var eventInterface = new EventInterface(attr.guid, events);
            if ((flags & IMPLTYPEFLAG_FDEFAULT) != 0)
            {
                result.Insert(0, eventInterface);
            }
            else
            {
                result.Add(eventInterface);
            }
        }

        return [.. result];
    }

    private static void AddPropertyNames(ITypeInfo typeInfo, List<string> names, HashSet<string> seen, HashSet<Guid> visited)
    {
        TypeAttr attr = GetTypeAttr(typeInfo);
        if (attr.typekind is not (TKIND_INTERFACE or TKIND_DISPATCH) || !visited.Add(attr.guid))
        {
            return;
        }

        for (uint i = 0; i < attr.cFuncs; i++)
        {
            if (typeInfo.GetFuncDesc(i, out FuncDesc* func) < 0)
            {
                continue;
            }

            try
            {
                if ((func->invkind & INVOKE_PROPERTYGET) != 0 && (func->wFuncFlags & (FUNCFLAG_FRESTRICTED | FUNCFLAG_FHIDDEN)) == 0
                    && !HasRequiredParameters(func) && GetMemberName(typeInfo, func->memid) is { } name && seen.Add(name))
                {
                    names.Add(name);
                }
            }
            finally
            {
                typeInfo.ReleaseFuncDesc(func);
            }
        }

        for (uint i = 0; i < attr.cVars; i++)
        {
            if (typeInfo.GetVarDesc(i, out VarDesc* var) < 0)
            {
                continue;
            }

            try
            {
                if (var->varkind == VAR_DISPATCH && (var->wVarFlags & (VARFLAG_FRESTRICTED | VARFLAG_FHIDDEN)) == 0
                    && GetMemberName(typeInfo, var->memid) is { } name && seen.Add(name))
                {
                    names.Add(name);
                }
            }
            finally
            {
                typeInfo.ReleaseVarDesc(var);
            }
        }

        for (uint i = 0; i < attr.cImplTypes; i++)
        {
            if (GetImplementedType(typeInfo, i) is { } baseType)
            {
                AddPropertyNames(baseType, names, seen, visited);
            }
        }
    }

    // The [retval] and [lcid] parameters are not passed by callers of IDispatch::Invoke.
    private static bool HasRequiredParameters(FuncDesc* func)
    {
        int required = func->cParams - Math.Max((int)func->cParamsOpt, 0);
        for (int i = 0; i < required; i++)
        {
            if ((func->lprgelemdescParam[i].wParamFlags & (PARAMFLAG_FRETVAL | PARAMFLAG_FLCID | PARAMFLAG_FOPT | PARAMFLAG_FHASDEFAULT)) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static ITypeInfo? GetImplementedType(ITypeInfo typeInfo, uint index)
    {
        return typeInfo.GetRefTypeOfImplType(index, out uint reference) >= 0 && typeInfo.GetRefTypeInfo(reference, out ITypeInfo? implemented) >= 0
            ? implemented
            : null;
    }

    private static TypeAttr GetTypeAttr(ITypeInfo typeInfo)
    {
        Marshal.ThrowExceptionForHR(typeInfo.GetTypeAttr(out TypeAttr* attr));
        try
        {
            return *attr;
        }
        finally
        {
            typeInfo.ReleaseTypeAttr(attr);
        }
    }

    private static string? GetMemberName(ITypeInfo typeInfo, int memberId)
    {
        nint name = 0;
        return typeInfo.GetNames(memberId, &name, 1, out uint count) >= 0 && count > 0 ? TakeString(name) : null;
    }

    private static string? TakeString(nint bstr)
    {
        if (bstr == 0)
        {
            return null;
        }

        string? value = Marshal.PtrToStringBSTR(bstr);
        Marshal.FreeBSTR(bstr);
        return value;
    }
}
