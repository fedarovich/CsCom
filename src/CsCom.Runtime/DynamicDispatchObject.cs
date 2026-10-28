using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace CsCom.Runtime;

/// <summary>
/// Gives late-bound access to an <see cref="IDispatch"/> interface through <c>dynamic</c>: members are resolved by name with
/// <c>IDispatch::GetIDsOfNames</c> and invoked with <c>IDispatch::Invoke</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>d.Name</c> and <c>d.Name = value</c> get and set properties.</description></item>
/// <item><description><c>d.Name(args)</c> invokes methods and parameterized properties, e.g. <c>d.Item(1)</c>; named arguments are supported.</description></item>
/// <item><description><c>d.Name[args]</c> and <c>d.Name[args] = value</c> get and set parameterized properties, e.g. <c>sheet.Range["A1"]</c>.</description></item>
/// <item><description><c>d[args]</c>, <c>d[args] = value</c> and <c>d(args)</c> use the default member (<c>DISPID_VALUE</c>).</description></item>
/// <item><description><c>ref</c> and <c>out</c> arguments are passed by reference and receive the new values.</description></item>
/// <item><description><c>foreach</c> enumerates collections through <c>_NewEnum</c>.</description></item>
/// <item><description>The object converts to <see cref="IDispatch"/>, to the COM interfaces it implements, to late-bound wrappers and to the value of its default member.</description></item>
/// <item><description><c>d.Event += handler</c> and <c>d.Event -= handler</c> add and remove handlers of the events of the source dispinterfaces of its coclass.</description></item>
/// </list>
/// COM objects returned by the members are wrapped as well. There is one wrapper per COM object, so wrappers of the same object
/// are equal, like the runtime callable wrappers of the built-in COM interop. The debugger shows the properties described by
/// the type information of the object.
/// </remarks>
[RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public sealed class DynamicDispatchObject : DynamicObject, IDispatchProvider, IEnumerable
{
    // The keys are the managed objects of the COM objects, which StrategyBasedComWrappers creates once per COM identity. The table
    // holds neither the keys nor the values: an entry lives as long as its key, so the wrappers do not extend the lifetime of
    // the COM objects.
    private static readonly ConditionalWeakTable<IDispatch, DynamicDispatchObject> Wrappers = new();

    private static readonly Lock EventSinksLock = new();

    private readonly ConcurrentDictionary<string, Member> _members = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _propertyNames;
    private EventInterface[]? _eventInterfaces;
    private Dictionary<Guid, DynamicEventSink>? _eventSinks;

    private DynamicDispatchObject(IDispatch dispatch)
    {
        Dispatch = dispatch;
    }

    // The public members of the class take precedence over the members of the COM object, so the wrapped interface is only
    // exposed explicitly and through the conversion to IDispatch.
    private IDispatch Dispatch { get; }

    IDispatch IDispatchProvider.Dispatch => Dispatch;

    private string DebuggerDisplay => ComDynamic.GetTypeName(this) ?? "COM object";

    /// <inheritdoc/>
    public override DynamicMetaObject GetMetaObject(Expression parameter)
    {
        return new DispatchMetaObject(parameter, this, base.GetMetaObject(parameter));
    }

    /// <summary>
    /// Returns the names of the properties that can be read without arguments, as described by the type information of the
    /// object, for the dynamic view of debuggers. Methods are not included, since debuggers read each member.
    /// </summary>
    public override IEnumerable<string> GetDynamicMemberNames()
    {
        return _propertyNames ??= ComTypeInfo.GetTypeInfo(Dispatch) is { } typeInfo ? ComTypeInfo.GetPropertyNames(typeInfo) : [];
    }

    /// <inheritdoc/>
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = null;
        if (!TryGetMember(binder.Name, out Member member))
        {
            // d.Event += handler reads the event first, then adds the handler to the result and assigns it back.
            if (TryGetEvent(binder.Name, out _, out _))
            {
                result = new EventAccessor(this, binder.Name);
                return true;
            }

            return false;
        }

        if (!member.IsIndexed)
        {
            int hr = DispatchInvoker.TryInvoke(Dispatch, member.DispId, DispatchFlags.PropertyGet | DispatchFlags.Method, [], [], default, default, out object? value, out int argument);
            if (hr >= 0)
            {
                result = Wrap(value);
                return true;
            }

            // A property that requires arguments, e.g. Range in sheet.Range["A1"], is indexed by the caller, and so is a property
            // that can only be set, e.g. Key in dictionary.Key["a"] = "b". Using the property as a value repeats the error.
            if (hr is not (OleNativeMethods.DISP_E_BADPARAMCOUNT or OleNativeMethods.DISP_E_PARAMNOTFOUND or OleNativeMethods.DISP_E_MEMBERNOTFOUND))
            {
                DispatchInvoker.ThrowInvocationException(hr, member.DispId, binder.Name, DispatchFlags.PropertyGet | DispatchFlags.Method, 0, argument);
            }

            _members[binder.Name] = member with { IsIndexed = true };
        }

        result = new IndexedProperty(this, member.DispId, binder.Name);
        return true;
    }

    /// <inheritdoc/>
    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        // The last step of d.Event += handler assigns the event back.
        if (value is EventAccessor accessor && accessor.Owner == this && string.Equals(accessor.Name, binder.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryGetMember(binder.Name, out Member member) && SetProperty(member.DispId, [value], binder.Name);
    }

    /// <inheritdoc/>
    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        result = null;
        if (!TryResolve(binder.Name, binder.CallInfo, out int dispId, out int[] namedDispIds))
        {
            return false;
        }

        result = Invoke(dispId, DispatchFlags.Method | DispatchFlags.PropertyGet, args ?? [], namedDispIds, (binder as ByRefInvokeMemberBinder)?.Types, binder.Name);
        return true;
    }

    /// <inheritdoc/>
    public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
    {
        result = Invoke(DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, indexes, [], null, null);
        return true;
    }

    /// <inheritdoc/>
    public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object? value)
    {
        return SetProperty(DispIds.Value, [.. indexes, value], null);
    }

    /// <inheritdoc/>
    public override bool TryInvoke(InvokeBinder binder, object?[]? args, out object? result)
    {
        result = Invoke(DispIds.Value, DispatchFlags.Method | DispatchFlags.PropertyGet, args ?? [], [], (binder as ByRefInvokeBinder)?.Types, null);
        return true;
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

        if (typeof(DispatchObject).IsAssignableFrom(type))
        {
            return TryCreateWrapper(type, out result);
        }

        // Like in VB, an object converts to a value through its default member.
        Type valueType = Nullable.GetUnderlyingType(type) ?? type;
        if (valueType == typeof(string) || valueType.IsPrimitive || valueType == typeof(decimal) || valueType == typeof(DateTime))
        {
            object? value = Invoke(DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, [], [], null, null);
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
        object? enumerator = DispatchInvoker.Invoke(Dispatch, DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet, []);
        return Enumerate(ComEnumerator.FromObject(enumerator));
    }

    /// <summary>
    /// Gets the wrapper of a COM object, creating it on first use.
    /// </summary>
    internal static DynamicDispatchObject GetOrCreate(IDispatch dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return Wrappers.GetValue(dispatch, static d => new DynamicDispatchObject(d));
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
            case object?[,] array2D when array2D.GetType() == typeof(object[,]):
                var result2D = new object?[array2D.GetLength(0), array2D.GetLength(1)];
                for (int i = 0; i < array2D.GetLength(0); i++)
                {
                    for (int j = 0; j < array2D.GetLength(1); j++)
                    {
                        result2D[i, j] = Wrap(array2D[i, j]);
                    }
                }

                return result2D;
            case object?[,,] array3D when array3D.GetType() == typeof(object[,,]):
                var result3D = new object?[array3D.GetLength(0), array3D.GetLength(1), array3D.GetLength(2)];
                for (int i = 0; i < array3D.GetLength(0); i++)
                {
                    for (int j = 0; j < array3D.GetLength(1); j++)
                    {
                        for (int k = 0; k < array3D.GetLength(2); k++)
                        {
                            result3D[i, j, k] = Wrap(array3D[i, j, k]);
                        }
                    }
                }

                return result3D;
            case IDispatch dispatch:
                return GetOrCreate(dispatch);
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

    private static T CreateWrapper<T>(IDispatch dispatch)
        where T : class, IDispatchWrapper<T>
    {
        return T.Create(dispatch);
    }

    /// <summary>
    /// Adds a handler of an event, connecting to the source dispinterface of the event if needed.
    /// </summary>
    /// <exception cref="ArgumentException">The object has no such event, or the handler has more parameters than the event.</exception>
    internal void AddEventHandler(string name, Delegate handler)
    {
        EventInterface eventInterface = GetEvent(name, out EventMember member);
        DynamicEventSink? sink;
        lock (EventSinksLock)
        {
            _eventSinks ??= [];
            if (!_eventSinks.TryGetValue(eventInterface.Iid, out sink))
            {
                _eventSinks.Add(eventInterface.Iid, sink = new DynamicEventSink(Dispatch, eventInterface));
            }
        }

        sink.Add(member, handler);
    }

    /// <summary>
    /// Removes a handler of an event, disconnecting from the source dispinterface when it has no handlers left.
    /// </summary>
    /// <exception cref="ArgumentException">The object has no such event.</exception>
    internal void RemoveEventHandler(string name, Delegate handler)
    {
        EventInterface eventInterface = GetEvent(name, out EventMember member);
        DynamicEventSink? sink;
        lock (EventSinksLock)
        {
            sink = _eventSinks?.GetValueOrDefault(eventInterface.Iid);
        }

        sink?.Remove(member, handler);
    }

    /// <summary>
    /// Removes the handlers of all events and disconnects from the source dispinterfaces.
    /// </summary>
    internal void RemoveEventHandlers()
    {
        DynamicEventSink[] sinks;
        lock (EventSinksLock)
        {
            sinks = _eventSinks is null ? [] : [.. _eventSinks.Values];
        }

        foreach (DynamicEventSink sink in sinks)
        {
            sink.RemoveAll();
        }
    }

    /// <summary>
    /// Converts a value to the type of a variable or a parameter, which receives it unboxed: COM objects are wrapped for
    /// <see cref="object"/> and <c>dynamic</c>, and numbers are converted to other numeric types and enums.
    /// </summary>
    internal static object? ConvertTo(object? value, Type type)
    {
        if (type == typeof(object))
        {
            return Wrap(value);
        }

        Type target = Nullable.GetUnderlyingType(type) ?? type;
        if (value is null)
        {
            return type.IsValueType && target == type ? Array.CreateInstance(type, 1).GetValue(0) : null;
        }

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        if (target.IsEnum)
        {
            return Enum.ToObject(target, value);
        }

        return value is IConvertible && (target.IsPrimitive || target == typeof(string) || target == typeof(decimal) || target == typeof(DateTime))
            ? Convert.ChangeType(value, target, CultureInfo.InvariantCulture)
            : value;
    }

    private bool TryCreateWrapper(Type type, out object? result)
    {
        MethodInfo method;
        try
        {
            method = typeof(DynamicDispatchObject).GetMethod(nameof(CreateWrapper), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(type);
        }
        catch (ArgumentException)
        {
            // The type is not a late-bound wrapper (IDispatchWrapper<T>).
            result = null;
            return false;
        }

        result = method.Invoke(null, [Dispatch]);
        return true;
    }

    private bool TryGetEvent(string name, [NotNullWhen(true)] out EventInterface? eventInterface, out EventMember member)
    {
        _eventInterfaces ??= ComTypeInfo.GetEventInterfaces(Dispatch);
        foreach (EventInterface candidate in _eventInterfaces)
        {
            if (candidate.Events.TryGetValue(name, out member))
            {
                eventInterface = candidate;
                return true;
            }
        }

        eventInterface = null;
        member = default;
        return false;
    }

    private EventInterface GetEvent(string name, out EventMember member)
    {
        return TryGetEvent(name, out EventInterface? eventInterface, out member)
            ? eventInterface
            : throw new ArgumentException($"The COM object has no event named '{name}'.", "eventName");
    }

    private bool TryGetMember(string name, out Member member)
    {
        if (_members.TryGetValue(name, out member))
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
        member = new Member(ids[0], IsIndexed: false);
        _members.TryAdd(name, member);
        return true;
    }

    // Resolves a member and the names of the named arguments of a call.
    private bool TryResolve(string name, CallInfo callInfo, out int dispId, out int[] namedDispIds)
    {
        var argumentNames = callInfo.ArgumentNames;
        if (argumentNames.Count == 0)
        {
            namedDispIds = [];
            bool found = TryGetMember(name, out Member member);
            dispId = member.DispId;
            return found;
        }

        string[] names = [name, .. argumentNames];
        var ids = new int[names.Length];
        int hr = DispatchInvoker.GetDispIds(Dispatch, names, ids);
        if (hr == OleNativeMethods.DISP_E_UNKNOWNNAME)
        {
            if (ids[0] == DispIds.Unknown)
            {
                dispId = DispIds.Unknown;
                namedDispIds = [];
                return false;
            }

            int unknown = Array.IndexOf(ids, DispIds.Unknown, 1);
            throw new ArgumentException($"The member '{name}' has no parameter named '{names[unknown]}'.");
        }

        Marshal.ThrowExceptionForHR(hr);
        dispId = ids[0];
        namedDispIds = ids[1..];
        return true;
    }

    // The value is the last argument. Objects are assigned by reference (VB's "Set") when the property supports it;
    // otherwise, a property put ("Let") assigns the value of their default member.
    private bool SetProperty(int dispId, object?[] args, string? name)
    {
        if (IsComObject(args[^1])
            && DispatchInvoker.TryInvoke(Dispatch, dispId, DispatchFlags.PropertyPutRef, args, [], default, default, out _, out _) >= 0)
        {
            return true;
        }

        DispatchInvoker.Invoke(Dispatch, dispId, DispatchFlags.PropertyPut, args, memberName: name);
        return true;
    }

    // Invokes a member. The by-reference arguments, whose variables have the specified types, receive the new values.
    private object? Invoke(int dispId, DispatchFlags flags, object?[] args, ReadOnlySpan<int> namedDispIds, Type?[]? byRefTypes, string? name)
    {
        if (byRefTypes is null)
        {
            return Wrap(DispatchInvoker.Invoke(Dispatch, dispId, flags, args, namedDispIds, memberName: name));
        }

        // Variables of the COM types are passed as references to their types, which typed by-reference parameters require,
        // and object and dynamic variables as references to VARIANTs. A null string is passed as an empty BSTR.
        var kinds = new ByRefKind[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (byRefTypes[i] is { } type)
            {
                kinds[i] = type == typeof(object) ? ByRefKind.Variant : ByRefKind.Typed;
                if (type == typeof(string))
                {
                    args[i] ??= string.Empty;
                }
            }
        }

        object? result = DispatchInvoker.Invoke(Dispatch, dispId, flags, args, namedDispIds, args, kinds, name);
        for (int i = 0; i < args.Length; i++)
        {
            if (byRefTypes[i] is { } type)
            {
                args[i] = ConvertTo(args[i], type);
            }
        }

        return Wrap(result);
    }

    private readonly record struct Member(int DispId, bool IsIndexed);

    /// <summary>
    /// An event of the COM object, read by <c>d.Event += handler</c> and <c>d.Event -= handler</c>, which add and remove the
    /// handler through it and assign it back.
    /// </summary>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    [DebuggerDisplay("Event {Name,nq}")]
    private sealed class EventAccessor(DynamicDispatchObject owner, string name) : DynamicObject
    {
        public DynamicDispatchObject Owner => owner;

        public string Name => name;

        public override bool TryBinaryOperation(BinaryOperationBinder binder, object arg, out object? result)
        {
            result = this;
            if (arg is not Delegate handler)
            {
                return false;
            }

            switch (binder.Operation)
            {
                case ExpressionType.AddAssign or ExpressionType.Add:
                    owner.AddEventHandler(name, handler);
                    return true;
                case ExpressionType.SubtractAssign or ExpressionType.Subtract:
                    owner.RemoveEventHandler(name, handler);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// A property that requires arguments, e.g. <c>Range</c> in <c>sheet.Range["A1"]</c>, which is indexed with them.
    /// </summary>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    [DebuggerDisplay("Indexed property {_name,nq}")]
    private sealed class IndexedProperty(DynamicDispatchObject owner, int dispId, string name) : DynamicObject
    {
        private readonly string _name = name;

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object? result)
        {
            result = owner.Invoke(dispId, DispatchFlags.PropertyGet | DispatchFlags.Method, indexes, [], null, _name);
            return true;
        }

        public override bool TrySetIndex(SetIndexBinder binder, object[] indexes, object? value)
        {
            return owner.SetProperty(dispId, [.. indexes, value], _name);
        }

        public override bool TryInvoke(InvokeBinder binder, object?[]? args, out object? result)
        {
            owner.TryResolve(_name, binder.CallInfo, out _, out int[] namedDispIds);
            result = owner.Invoke(dispId, DispatchFlags.Method | DispatchFlags.PropertyGet, args ?? [], namedDispIds, null, _name);
            return true;
        }

        // The property is used as a value: reading it without arguments throws the error of the COM object.
        public override bool TryConvert(ConvertBinder binder, out object? result)
        {
            result = ConvertTo(owner.Invoke(dispId, DispatchFlags.PropertyGet | DispatchFlags.Method, [], [], null, _name), binder.Type);
            return true;
        }
    }

    /// <summary>
    /// Binds the operations of <see cref="DynamicDispatchObject"/> like <see cref="DynamicObject"/> does, and passes the types of
    /// the <c>ref</c> and <c>out</c> arguments of invocations to <see cref="TryInvokeMember"/> and <see cref="TryInvoke"/>.
    /// </summary>
    /// <remarks>
    /// The variables of <c>ref</c> and <c>out</c> arguments are by-reference parameters of the call site, which
    /// <see cref="DynamicObject"/> assigns from the argument array after a successful invocation.
    /// </remarks>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    private sealed class DispatchMetaObject(Expression expression, DynamicDispatchObject value, DynamicMetaObject inner)
        : DynamicMetaObject(expression, BindingRestrictions.Empty, value)
    {
        public override DynamicMetaObject BindInvokeMember(InvokeMemberBinder binder, DynamicMetaObject[] args) =>
            GetByRefTypes(args) is { } types
                ? inner.BindInvokeMember(new ByRefInvokeMemberBinder(binder, types), args)
                : inner.BindInvokeMember(binder, args);

        public override DynamicMetaObject BindInvoke(InvokeBinder binder, DynamicMetaObject[] args) =>
            GetByRefTypes(args) is { } types
                ? inner.BindInvoke(new ByRefInvokeBinder(binder, types), args)
                : inner.BindInvoke(binder, args);

        public override DynamicMetaObject BindGetMember(GetMemberBinder binder) => inner.BindGetMember(binder);

        public override DynamicMetaObject BindSetMember(SetMemberBinder binder, DynamicMetaObject value) => inner.BindSetMember(binder, value);

        public override DynamicMetaObject BindDeleteMember(DeleteMemberBinder binder) => inner.BindDeleteMember(binder);

        public override DynamicMetaObject BindGetIndex(GetIndexBinder binder, DynamicMetaObject[] indexes) => inner.BindGetIndex(binder, indexes);

        public override DynamicMetaObject BindSetIndex(SetIndexBinder binder, DynamicMetaObject[] indexes, DynamicMetaObject value) =>
            inner.BindSetIndex(binder, indexes, value);

        public override DynamicMetaObject BindDeleteIndex(DeleteIndexBinder binder, DynamicMetaObject[] indexes) => inner.BindDeleteIndex(binder, indexes);

        public override DynamicMetaObject BindCreateInstance(CreateInstanceBinder binder, DynamicMetaObject[] args) => inner.BindCreateInstance(binder, args);

        public override DynamicMetaObject BindUnaryOperation(UnaryOperationBinder binder) => inner.BindUnaryOperation(binder);

        public override DynamicMetaObject BindBinaryOperation(BinaryOperationBinder binder, DynamicMetaObject arg) => inner.BindBinaryOperation(binder, arg);

        public override DynamicMetaObject BindConvert(ConvertBinder binder) => inner.BindConvert(binder);

        public override IEnumerable<string> GetDynamicMemberNames() => inner.GetDynamicMemberNames();

        private static Type?[]? GetByRefTypes(DynamicMetaObject[] args)
        {
            Type?[]? types = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Expression is ParameterExpression { IsByRef: true } variable)
                {
                    types ??= new Type?[args.Length];
                    types[i] = variable.Type;
                }
            }

            return types;
        }
    }

    /// <summary>An <see cref="InvokeMemberBinder"/> that carries the types of the by-reference arguments.</summary>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    private sealed class ByRefInvokeMemberBinder(InvokeMemberBinder binder, Type?[] types)
        : InvokeMemberBinder(binder.Name, binder.IgnoreCase, binder.CallInfo)
    {
        public Type?[] Types => types;

        public override DynamicMetaObject FallbackInvokeMember(DynamicMetaObject target, DynamicMetaObject[] args, DynamicMetaObject? errorSuggestion) =>
            binder.FallbackInvokeMember(target, args, errorSuggestion);

        public override DynamicMetaObject FallbackInvoke(DynamicMetaObject target, DynamicMetaObject[] args, DynamicMetaObject? errorSuggestion) =>
            binder.FallbackInvoke(target, args, errorSuggestion);
    }

    /// <summary>An <see cref="InvokeBinder"/> that carries the types of the by-reference arguments.</summary>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    private sealed class ByRefInvokeBinder(InvokeBinder binder, Type?[] types) : InvokeBinder(binder.CallInfo)
    {
        public Type?[] Types => types;

        public override DynamicMetaObject FallbackInvoke(DynamicMetaObject target, DynamicMetaObject[] args, DynamicMetaObject? errorSuggestion) =>
            binder.FallbackInvoke(target, args, errorSuggestion);
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
        return DynamicDispatchObject.GetOrCreate(dispatch);
    }

    /// <summary>
    /// Returns the COM object wrapped by a late-bound dispinterface wrapper for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="wrapper">The late-bound wrapper.</param>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static dynamic AsDynamic(this DispatchObject wrapper)
    {
        ArgumentNullException.ThrowIfNull(wrapper);
        return DynamicDispatchObject.GetOrCreate(wrapper.Dispatch);
    }

    /// <summary>
    /// Returns a COM object, e.g. one returned in a <c>VARIANT</c>, for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="comObject">A COM object that implements <c>IDispatch</c>, a late-bound wrapper, or <see langword="null"/>.</param>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static dynamic? ToDynamic(object? comObject)
    {
        return comObject is null ? null : GetWrapper(comObject);
    }

    /// <summary>
    /// Gets the name of the type of a COM object from its type information, like VB's <c>TypeName</c>, e.g. the name of the
    /// default interface of its class.
    /// </summary>
    /// <param name="comObject">A COM object that implements <c>IDispatch</c>, a late-bound wrapper or a <c>dynamic</c> object.</param>
    /// <returns>The name, or <see langword="null"/> if the object provides no type information.</returns>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    public static string? GetTypeName(object comObject)
    {
        ArgumentNullException.ThrowIfNull(comObject);
        IDispatch dispatch = comObject switch
        {
            IDispatchProvider provider => provider.Dispatch,
            IDispatch direct => direct,
            _ => throw new InvalidCastException($"The object of type {comObject.GetType()} does not implement IDispatch."),
        };

        return ComTypeInfo.GetTypeInfo(dispatch) is { } typeInfo ? ComTypeInfo.GetTypeName(typeInfo) : null;
    }

    /// <summary>
    /// Adds a handler of an event of a COM object, like <c>d.Event += handler</c>, also for events whose names are those of
    /// methods or properties of the object.
    /// </summary>
    /// <param name="comObject">A COM object that implements <c>IDispatch</c>, a late-bound wrapper or a <c>dynamic</c> object.</param>
    /// <param name="eventName">The name of a method of a source dispinterface of the coclass of the object.</param>
    /// <param name="handler">
    /// The handler: a delegate with at most the parameters of the event, which receives the arguments converted to the types of
    /// its parameters, and returns the values of <c>ref</c> and <c>out</c> parameters and the result of the event.
    /// </param>
    /// <exception cref="ArgumentException">The object has no such event, or the handler has more parameters than the event.</exception>
    /// <exception cref="InvalidOperationException">The object does not raise the events of the source dispinterface.</exception>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static void AddEventHandler(object comObject, string eventName, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(comObject);
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        GetWrapper(comObject).AddEventHandler(eventName, handler);
    }

    /// <summary>
    /// Removes a handler of an event of a COM object, like <c>d.Event -= handler</c>. The connection to the source
    /// dispinterface is closed when it has no handlers left.
    /// </summary>
    /// <param name="comObject">A COM object that implements <c>IDispatch</c>, a late-bound wrapper or a <c>dynamic</c> object.</param>
    /// <param name="eventName">The name of the event.</param>
    /// <param name="handler">The handler to remove.</param>
    /// <exception cref="ArgumentException">The object has no such event.</exception>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static void RemoveEventHandler(object comObject, string eventName, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(comObject);
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(handler);
        GetWrapper(comObject).RemoveEventHandler(eventName, handler);
    }

    /// <summary>
    /// Removes the handlers of all events of a COM object added through <c>dynamic</c> and closes the connections to its
    /// source dispinterfaces.
    /// </summary>
    /// <param name="comObject">A COM object that implements <c>IDispatch</c>, a late-bound wrapper or a <c>dynamic</c> object.</param>
    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    public static void RemoveEventHandlers(object comObject)
    {
        ArgumentNullException.ThrowIfNull(comObject);
        GetWrapper(comObject).RemoveEventHandlers();
    }

    [RequiresDynamicCode(RequiresDynamicCodeMessage)]
    private static DynamicDispatchObject GetWrapper(object comObject) => comObject switch
    {
        DynamicDispatchObject dynamicObject => dynamicObject,
        IDispatchProvider provider => DynamicDispatchObject.GetOrCreate(provider.Dispatch),
        IDispatch dispatch => DynamicDispatchObject.GetOrCreate(dispatch),
        _ => throw new InvalidCastException($"The object of type {comObject.GetType()} does not implement IDispatch."),
    };
}
