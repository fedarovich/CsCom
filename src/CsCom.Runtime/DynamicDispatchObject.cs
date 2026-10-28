using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CsCom.Runtime;

/// <summary>
/// Gives late-bound access to an <see cref="IDispatch"/> interface through <c>dynamic</c>: members are resolved by name with
/// <c>IDispatch::GetIDsOfNames</c> and invoked with <c>IDispatch::Invoke</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>d.Name</c> and <c>d.Name = value</c> get and set properties.</description></item>
/// <item><description><c>d.Name(args)</c> invokes methods and parameterized properties, e.g. <c>d.Item(1)</c>; named arguments are supported.</description></item>
/// <item><description><c>d[args]</c>, <c>d[args] = value</c> and <c>d(args)</c> use the default member (<c>DISPID_VALUE</c>).</description></item>
/// <item><description><c>foreach</c> enumerates collections through <c>_NewEnum</c>.</description></item>
/// <item><description>The object converts to <see cref="IDispatch"/>, to the COM interfaces it implements, and to the value of its default member.</description></item>
/// </list>
/// COM objects returned by the members are wrapped as well. Arguments are passed by value: <c>ref</c> and <c>out</c> arguments are not updated.
/// </remarks>
[RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
public sealed class DynamicDispatchObject : DynamicObject, IDispatchProvider, IEnumerable
{
    private readonly ConcurrentDictionary<string, int> _dispIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamicDispatchObject"/> class.
    /// </summary>
    /// <param name="dispatch">The <see cref="IDispatch"/> interface of the COM object.</param>
    public DynamicDispatchObject(IDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        Dispatch = dispatch;
    }

    // The public members of the class take precedence over the members of the COM object, so the wrapped interface is only
    // exposed explicitly and through the conversion to IDispatch.
    private IDispatch Dispatch { get; }

    IDispatch IDispatchProvider.Dispatch => Dispatch;

    /// <inheritdoc/>
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = null;
        return TryGetDispId(binder.Name, out int dispId)
            && Invoke(dispId, DispatchFlags.PropertyGet | DispatchFlags.Method, [], [], out result);
    }

    /// <inheritdoc/>
    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        return TryGetDispId(binder.Name, out int dispId) && SetProperty(dispId, [value]);
    }

    /// <inheritdoc/>
    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        result = null;
        var argumentNames = binder.CallInfo.ArgumentNames;
        int dispId;
        int[] namedDispIds;
        if (argumentNames.Count == 0)
        {
            if (!TryGetDispId(binder.Name, out dispId))
            {
                return false;
            }

            namedDispIds = [];
        }
        else
        {
            string[] names = [binder.Name, .. argumentNames];
            var ids = new int[names.Length];
            int hr = DispatchInvoker.GetDispIds(Dispatch, names, ids);
            if (hr == OleNativeMethods.DISP_E_UNKNOWNNAME)
            {
                if (ids[0] == DispIds.Unknown)
                {
                    return false;
                }

                int unknown = Array.IndexOf(ids, DispIds.Unknown, 1);
                throw new ArgumentException($"The member '{binder.Name}' has no parameter named '{names[unknown]}'.");
            }

            Marshal.ThrowExceptionForHR(hr);
            dispId = ids[0];
            namedDispIds = ids[1..];
        }

        return Invoke(dispId, DispatchFlags.Method | DispatchFlags.PropertyGet, args ?? [], namedDispIds, out result);
    }

    /// <inheritdoc/>
    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        return Invoke(DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, indexes, [], out result);
    }

    /// <inheritdoc/>
    public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object? value)
    {
        return SetProperty(DispIds.Value, [.. indexes, value]);
    }

    /// <inheritdoc/>
    public override bool TryInvoke(InvokeBinder binder, object?[]? args, out object? result)
    {
        return Invoke(DispIds.Value, DispatchFlags.Method, args ?? [], [], out result);
    }

    /// <inheritdoc/>
    public override bool TryConvert(ConvertBinder binder, out object? result)
    {
        Type type = binder.Type;
        if (type.IsInstanceOfType(Dispatch)
            || (type.IsInterface && Dispatch is IDynamicInterfaceCastable castable && castable.IsInterfaceImplemented(type.TypeHandle, throwIfNotImplemented: false)))
        {
            result = Dispatch;
            return true;
        }

        // Like in VB, an object converts to a value through its default member.
        Type valueType = Nullable.GetUnderlyingType(type) ?? type;
        if (valueType == typeof(string) || valueType.IsPrimitive || valueType == typeof(decimal) || valueType == typeof(DateTime))
        {
            Invoke(DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, [], [], out object? value);
            if (value is null)
            {
                result = null;
                return !type.IsValueType || type != valueType;
            }

            result = Convert.ChangeType(value, valueType, CultureInfo.InvariantCulture);
            return true;
        }

        result = null;
        return false;
    }

    /// <summary>
    /// Returns an enumerator over the items of a collection that exposes a <c>_NewEnum</c> member.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator()
    {
        Invoke(DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet, [], [], out object? enumerator);
        return Enumerate(ComEnumerator.FromObject(enumerator is IDispatchProvider provider ? provider.Dispatch : enumerator));
    }

    /// <summary>
    /// Wraps the COM objects that implement <see cref="IDispatch"/>, including the elements of object arrays, for late binding.
    /// </summary>
    internal static object? Wrap(object? value)
    {
        switch (value)
        {
            case null:
            case IDispatchProvider:
                return value;
            case object?[] array when array.GetType() == typeof(object[]):
                var result = new object?[array.Length];
                for (int i = 0; i < array.Length; i++)
                {
                    result[i] = Wrap(array[i]);
                }

                return result;
            case IDispatch dispatch:
                return new DynamicDispatchObject(dispatch);
            default:
                return value;
        }
    }

    private static IEnumerator Enumerate(ComEnumerator enumerator)
    {
        using (enumerator)
        {
            while (enumerator.MoveNext())
            {
                yield return Wrap(enumerator.Current);
            }
        }
    }

    private static bool IsComObject(object? value) =>
        value is IDispatchProvider || (value is not null && ComWrappers.TryGetComInstance(value, out nint unknown) && Release(unknown));

    private static bool Release(nint unknown)
    {
        Marshal.Release(unknown);
        return true;
    }

    private bool TryGetDispId(string name, out int dispId)
    {
        if (_dispIds.TryGetValue(name, out dispId))
        {
            return true;
        }

        Span<int> ids = [0];
        int hr = DispatchInvoker.GetDispIds(Dispatch, [name], ids);
        if (hr == OleNativeMethods.DISP_E_UNKNOWNNAME)
        {
            return false;
        }

        Marshal.ThrowExceptionForHR(hr);
        dispId = ids[0];
        _dispIds.TryAdd(name, dispId);
        return true;
    }

    // The value is the last argument. Objects are assigned by reference (VB's "Set") when the property supports it;
    // otherwise, a property put ("Let") assigns the value of their default member.
    private bool SetProperty(int dispId, ReadOnlySpan<object?> args)
    {
        if (IsComObject(args[^1])
            && DispatchInvoker.TryInvoke(Dispatch, dispId, DispatchFlags.PropertyPutRef, args, [], default, default, out _) >= 0)
        {
            return true;
        }

        return Invoke(dispId, DispatchFlags.PropertyPut, args, [], out _);
    }

    private bool Invoke(int dispId, DispatchFlags flags, ReadOnlySpan<object?> args, ReadOnlySpan<int> namedDispIds, out object? result)
    {
        result = Wrap(DispatchInvoker.Invoke(Dispatch, dispId, flags, args, namedDispIds));
        return true;
    }
}

/// <summary>
/// Gives late-bound access to COM objects through <c>dynamic</c>.
/// </summary>
public static class ComDynamic
{
    internal const string RequiresDynamicCodeMessage = "Late binding with dynamic requires dynamic code generation and is not compatible with Native AOT.";

    /// <summary>
    /// Returns the COM object for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="dispatch">The <see cref="IDispatch"/> interface of the object, or a dual interface derived from it.</param>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static dynamic AsDynamic(this IDispatch dispatch)
    {
        return new DynamicDispatchObject(dispatch);
    }

    /// <summary>
    /// Returns the COM object wrapped by a late-bound dispinterface wrapper for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="wrapper">The late-bound wrapper.</param>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static dynamic AsDynamic(this DispatchObject wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return new DynamicDispatchObject(wrapper.Dispatch);
    }

    /// <summary>
    /// Returns a COM object, e.g. one returned in a <c>VARIANT</c>, for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="comObject">A COM object that implements <c>IDispatch</c>, a late-bound wrapper, or <see langword="null"/>.</param>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static dynamic? ToDynamic(object? comObject)
    {
        return comObject switch
        {
            null => null,
            DynamicDispatchObject dynamicObject => dynamicObject,
            IDispatchProvider provider => new DynamicDispatchObject(provider.Dispatch),
            IDispatch dispatch => new DynamicDispatchObject(dispatch),
            _ => throw new InvalidCastException($"The object of type {comObject.GetType()} does not implement IDispatch."),
        };
    }
}
