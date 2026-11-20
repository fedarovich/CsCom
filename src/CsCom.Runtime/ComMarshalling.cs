using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace CsCom.Runtime;

/// <summary>
/// <c>IUnknown</c> as a source-generated COM interface, which every COM object implements: <see cref="ComMarshalling"/> asks
/// an object for it to get its <c>IUnknown</c> pointer.
/// </summary>
[GeneratedComInterface]
[Guid("00000000-0000-0000-C000-000000000046")]
internal partial interface IUnknownObject
{
}

/// <summary>
/// Configures how the support code converts COM interface pointers to managed objects and back.
/// </summary>
public static unsafe class ComMarshalling
{
    private static readonly Lock Gate = new();
    private static StrategyBasedComWrappers? _comWrappers;
    private static volatile bool _used;

    /// <summary>
    /// Gets or sets the <see cref="StrategyBasedComWrappers"/> that creates the managed objects of COM objects and the COM
    /// interfaces of managed objects; <see langword="null"/>, the default, for the instance that
    /// <see cref="ComInterfaceMarshaller{T}"/> of .NET uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instance is used by <see cref="ComActivator"/>, by the <c>VARIANT</c> and <c>SAFEARRAY</c> marshallers, by the
    /// late-bound wrappers and the event sinks, and by <see cref="ComObjectMarshaller{T}"/> and
    /// <see cref="UniqueComObjectMarshaller{T}"/>, which the generated code applies to all interface pointers. Other code
    /// that marshals interface pointers with <see cref="ComInterfaceMarshaller{T}"/>, such as hand-written COM interfaces
    /// without these marshallers, does not use it.
    /// </para>
    /// <para>
    /// The marshalled interfaces must be source-generated COM interfaces (<see cref="GeneratedComInterfaceAttribute"/>),
    /// also when the instance has a strategy of its own for the details of interfaces: their identifiers are taken from
    /// the attributes that the generator adds.
    /// </para>
    /// <para>
    /// Set it once, before the first COM object is created: two instances create two managed objects for one COM object.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The value is changed after the support code converted an interface pointer.
    /// </exception>
    public static StrategyBasedComWrappers? ComWrappers
    {
        get => Volatile.Read(ref _comWrappers);
        set
        {
            lock (Gate)
            {
                if (_used && !ReferenceEquals(value, _comWrappers))
                {
                    throw new InvalidOperationException(
                        $"{nameof(ComMarshalling)}.{nameof(ComWrappers)} cannot be changed after COM interface pointers have been converted.");
                }

                Volatile.Write(ref _comWrappers, value);
            }
        }
    }

    /// <summary>
    /// Gets the configured instance, which cannot be changed any more afterwards.
    /// </summary>
    private static StrategyBasedComWrappers? Current
    {
        get
        {
            if (!_used)
            {
                lock (Gate)
                {
                    _used = true;
                }
            }

            return _comWrappers;
        }
    }

    /// <summary>
    /// Converts an interface pointer to a managed object, without taking ownership of the reference.
    /// </summary>
    /// <param name="unmanaged">The interface pointer.</param>
    /// <param name="unique">Whether to create a managed object that is not shared with other references to the COM object.</param>
    internal static T? ToManaged<T>(void* unmanaged, bool unique = false)
    {
        // The interface is checked also for a null pointer, so that a marshaller of an interface that is not a COM
        // interface fails at its first use rather than at its first object. A null pointer is no COM object: converting
        // it does not use the instance, which can still be set afterwards, and neither does the check.
        ThrowIfNotComInterface<T>();
        if (unmanaged is null)
        {
            return default;
        }

        StrategyBasedComWrappers? wrappers = Current;

        // The pointer of a managed object that the instance exposes is converted to the object itself when it implements
        // the interface, and otherwise to a COM object of its own, which calls it through COM like the pointer of any COM
        // object, e.g. when its class implements another declaration of the interface, of another interop assembly.
        if (!unique)
        {
            // Converted untyped with the Unwrap flag, and cast here: a COM object at once, which asks it for the interface
            // once, a managed object only if it implements the interface. Otherwise it gets a COM object of its own like a
            // unique pointer, for every conversion: it is not shared, so Release releases it, since only its receiver has
            // it. A shared one cannot be created: the instance of .NET has no public method for it, and another instance
            // would keep it for the identity of the pointer, which ComWrappers looks up before it unwraps, so that it would
            // be returned instead of the managed object for the interfaces that the object implements.
            object shared = wrappers is null
                ? ComInterfaceMarshaller<object>.ConvertToManaged(unmanaged)!
                : wrappers.GetOrCreateObjectForComInstance((nint)unmanaged, CreateObjectFlags.Unwrap);
            if (shared is ComObject || shared is T)
            {
                return (T)shared;
            }
        }
        else if (TryUnwrap<T>(wrappers, unmanaged, out object? managed))
        {
            // Like the shared objects with the Unwrap flag. That flag has no effect with UniqueInstance, which ComWrappers
            // tests first, and UniqueComInterfaceMarshaller does not unwrap either, so the instance is asked first. The
            // object is no COM object of its receiver, which ReleaseObject leaves alone.
            return (T)managed;
        }

        // A unique object is created untyped, so that it can be released when the COM object does not implement the
        // interface: nobody else has a reference to it, and it would hold the COM object until it is garbage collected.
        object instance = wrappers is not null
            ? wrappers.GetOrCreateObjectForComInstance((nint)unmanaged, CreateObjectFlags.UniqueInstance)
            : UniqueComInterfaceMarshaller<object>.ConvertToManaged(unmanaged)!;
        try
        {
            return (T)instance;
        }
        catch
        {
            // Not only an InvalidCastException: a QueryInterface that fails with another error than E_NOINTERFACE, e.g.
            // of a disconnected proxy, throws the exception of its HRESULT.
            ReleaseObject(instance);
            throw;
        }
    }

    // Whether the pointer is a COM interface that the instance has created for a managed object that implements T, which it
    // converts back to that object, like Unwrap. The static ComWrappers.TryGetObject finds the objects of every instance,
    // also one with vtables of its own whose object does not implement the interface, which a shared object calls through
    // COM. So the identity of the pointer is compared with that of the instance's own interface of the object, which the
    // instance creates if only another instance has one: an object of another ComWrappers keeps it as long as it lives,
    // unused. A table of the interfaces that ToUnmanaged creates would avoid that, but miss those that the instance
    // creates elsewhere, e.g. in calls of the application or in the marshallers of .NET, whose pointers would then be
    // called through COM. Without a configured instance, it is the default instance of .NET, which ComInterfaceMarshaller
    // uses: it is not public, so its interface is created by ComInterfaceMarshaller too.
    private static bool TryUnwrap<T>(StrategyBasedComWrappers? wrappers, void* unmanaged, [NotNullWhen(true)] out object? managed)
    {
        // A COM object in the interface of a managed object is converted like a COM object, not exposed as an object of
        // the instance: ToUnmanaged passes it as its COM object. A managed object that does not implement the interface is
        // called through COM whatever instance exposes it, so the identity is not compared, which would create an
        // interface of the instance for it.
        if (!global::System.Runtime.InteropServices.ComWrappers.TryGetObject((nint)unmanaged, out managed) || managed is ComObject or not T)
        {
            managed = null;
            return false;
        }

        nint own;
        try
        {
            own = wrappers is not null
                ? wrappers.GetOrCreateComInterfaceForObject(managed, CreateComInterfaceFlags.None)
                : (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(managed);
        }
        catch (Exception)
        {
            // An instance whose strategy cannot expose the object has not created the interface either.
            managed = null;
            return false;
        }

        try
        {
            if (Marshal.QueryInterface((nint)unmanaged, OleNativeMethods.IID_IUnknown, out nint identity) >= 0)
            {
                Marshal.Release(identity);
                if (identity == own)
                {
                    return true;
                }
            }
        }
        finally
        {
            Marshal.Release(own);
        }

        managed = null;
        return false;
    }

    /// <summary>
    /// Throws when <typeparamref name="T"/> is an interface that is not a source-generated COM interface
    /// (<see cref="GeneratedComInterfaceAttribute"/>): its identifier is that of a source-generated COM interface, whatever
    /// strategy the instance has. The conversions check it also for <see langword="null"/>.
    /// </summary>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not a source-generated COM interface.</exception>
    internal static void ThrowIfNotComInterface<T>()
    {
        // Without the identifier, the IUnknown pointer would be returned as the pointer of the interface, and called through
        // the wrong vtable, and a COM object could not be converted to the interface.
        if (InterfaceId<T>.Value is null && typeof(T).IsInterface)
        {
            throw new NotSupportedException(
                $"The interface {typeof(T)} cannot be marshalled, because it is not a source-generated COM interface ([GeneratedComInterface]).");
        }
    }

    /// <summary>
    /// Releases the COM references of a unique object at once, like <see cref="ComObject.FinalRelease"/>; the object must
    /// not be used afterwards. Nothing happens for other objects: a shared object keeps its references until it is garbage
    /// collected, and a managed object has none. The COM object that a shared conversion creates for the pointer of a
    /// managed object for an interface that the object does not implement is not shared (see
    /// <see cref="ComObjectMarshaller{T}"/>), and is released.
    /// </summary>
    /// <param name="value">
    /// The object: a COM interface, or a late-bound wrapper or <c>dynamic</c> object, whose COM object is released; can be
    /// <see langword="null"/>. A managed object that is exposed to COM (<see cref="GeneratedComClassAttribute"/>) is left
    /// alone, also when it implements <see cref="IDispatchProvider"/>. An <see cref="UnknownWrapper"/> stands for its
    /// object, and a <see cref="DispatchWrapper"/> is rejected, as where an object is passed to COM.
    /// </param>
    /// <remarks>
    /// Release an object only when no other thread uses it: a call that passes it to a COM object at the same time can use
    /// its interface pointer after the release.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is, or wraps, a <see cref="DispatchWrapper"/>.</exception>
    public static void Release(object? value)
    {
        // An UnknownWrapper stands for its object, as where an object is passed to COM and in ComEnumerator.FromObject,
        // which releases it like this method. A DispatchWrapper is rejected as everywhere: ignoring it like a managed
        // object would leave the object that it wraps to the garbage collector without a word.
        value = UnwrapUnknownWrapper(value, nameof(value));

        // A managed object that is exposed to COM is not the receiver's: the marshallers never create one, they return the
        // object of the application for its own pointer, which must not lose the object that it wraps.
        if (value is ComObject comObject)
        {
            comObject.FinalRelease();
        }
        else if (value is not null && IsLateBoundWrapper(value, out IDispatch? dispatch))
        {
            ReleaseObject(dispatch);
        }
    }

    /// <summary>
    /// Releases the COM references of a unique object that the caller has created itself. Unlike <see cref="Release"/>,
    /// it leaves a late-bound wrapper alone: a pointer can be converted to a managed object of the application, whose
    /// COM object is not the caller's.
    /// </summary>
    internal static void ReleaseObject(object? value) => (value as ComObject)?.FinalRelease();

    /// <summary>
    /// Gets a value indicating whether the support code exposes an object to COM itself: its class is a source-generated
    /// COM class (<see cref="GeneratedComClassAttribute"/>). Such an object is passed as its own COM interface in a
    /// <c>VARIANT</c>, a <c>SAFEARRAY</c> and an <c>IUnknown</c> parameter alike, also when it implements
    /// <see cref="IDispatchProvider"/>, so that its pointer converts back to it, and <see cref="Release"/> leaves it alone.
    /// </summary>
    /// <param name="value">The object; can be <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> if the class of the object is a source-generated COM class; <see langword="false"/>
    /// otherwise, also for <see langword="null"/>, a COM object and a late-bound or <c>dynamic</c> wrapper.
    /// </returns>
    /// <remarks>
    /// The test is the same whatever <see cref="ComWrappers"/> is set: it reads the class with the strategy of .NET that
    /// reads the identifiers of the interfaces, so that the same values are passed with every instance, and it does not use
    /// the instance, which can still be set afterwards. Pass an object that only a custom instance exposes in an
    /// <see cref="UnknownWrapper"/>. One that implements <see cref="IDispatchProvider"/> is a late-bound wrapper all the
    /// same, which stands for the COM object of its <see cref="IDispatchProvider.Dispatch"/>, also in an
    /// <see cref="UnknownWrapper"/> and in a parameter of a COM interface that it implements.
    /// </remarks>
    public static bool IsExposedToCom([NotNullWhen(true)] object? value) =>
        value is not null && ReferenceEquals(ExposedTypes.GetValue(value.GetType(), static type => GetExposure(type)), Exposed);

    // The strategy reads an attribute of the class with reflection on every call, so the result is kept per class. The table
    // does not keep the classes of collectible assemblies alive.
    private static readonly ConditionalWeakTable<Type, object> ExposedTypes = new();
    private static readonly object Exposed = new();
    private static readonly object NotExposed = new();

    private static object GetExposure(Type type) =>
        StrategyBasedComWrappers.DefaultIUnknownInterfaceDetailsStrategy.GetComExposedTypeDetails(type.TypeHandle) is not null ? Exposed : NotExposed;

    /// <summary>
    /// Gets a value indicating whether an object is a late-bound or <c>dynamic</c> wrapper, which stands for the COM object
    /// of its <see cref="IDispatchProvider.Dispatch"/> everywhere (see <see cref="IDispatchProvider"/>): an
    /// <see cref="IDispatchProvider"/> that is not exposed to COM itself.
    /// </summary>
    /// <param name="value">The object.</param>
    /// <param name="dispatch">
    /// The <see cref="IDispatchProvider.Dispatch"/> of the wrapper; <see langword="null"/> also for a wrapper whose
    /// <see cref="IDispatchProvider.Dispatch"/> breaks its contract, which stands for no object.
    /// </param>
    internal static bool IsLateBoundWrapper(object value, out IDispatch? dispatch)
    {
        // A COM object is no wrapper, and is not tested for the interface: the type test of one that has been released
        // throws, and the strategy of the object looks the interface up on every call.
        if (value is not ComObject && value is IDispatchProvider provider && !IsExposedToCom(value))
        {
            dispatch = provider.Dispatch;
            return true;
        }

        dispatch = null;
        return false;
    }

    /// <summary>
    /// Gets the object that a value stands for where the support code uses an object of the application as a COM object:
    /// an <see cref="UnknownWrapper"/> stands for its object, also a nested one, and a <see cref="DispatchWrapper"/> is
    /// rejected. Any other value stands for itself.
    /// </summary>
    /// <remarks>
    /// The wrappers of .NET only name an object: the server could not use the interface of the wrapper itself, which only
    /// has <c>IUnknown</c>. Without the built-in COM interop, a <see cref="DispatchWrapper"/> can only wrap
    /// <see langword="null"/>, which an <see cref="UnknownWrapper"/> passes; <see cref="ComVariantMarshaller"/> rejects it
    /// in a <c>VARIANT</c> too. <c>ComDynamic</c>, which may only use the public API, has a copy of the rule.
    /// </remarks>
    /// <param name="value">The value.</param>
    /// <param name="paramName">The name of the parameter that the value was passed in, for the exception.</param>
    /// <returns>The object; <see langword="null"/> for <see langword="null"/> and an UnknownWrapper of null.</returns>
    /// <exception cref="ArgumentException">The value is, or wraps, a <see cref="DispatchWrapper"/>.</exception>
    internal static object? UnwrapUnknownWrapper(object? value, string paramName)
    {
        while (value is UnknownWrapper wrapper)
        {
            value = wrapper.WrappedObject;
        }

        return value is DispatchWrapper
            ? throw new ArgumentException("A DispatchWrapper cannot be passed to COM: pass the object itself, or an UnknownWrapper.", paramName)
            : value;
    }

    /// <summary>
    /// Gets the <c>IDispatch</c> that stands for an object of the application that is exposed to COM where an interface
    /// is required that it does not implement, e.g. a decorator of a COM object: the <see cref="IDispatchProvider.Dispatch"/>
    /// that it provides (see <see cref="IDispatchProvider"/>). A late-bound wrapper always stands for its COM object (see
    /// <see cref="IsLateBoundWrapper"/>).
    /// </summary>
    /// <remarks>
    /// The test of the interface is made by the caller: <see cref="QueryInterface"/> asks the COM interface of the object,
    /// which can also answer with <see cref="ICustomQueryInterface"/>, a conversion to an interface tests the managed type
    /// and then asks the COM interface (see <see cref="TryConvertExposedObject"/>), and a conversion to a late-bound
    /// wrapper tests the managed type, which it calls. <c>ComDynamic</c>, which may only use the public API, makes the
    /// test of the late-bound wrapper.
    /// </remarks>
    /// <returns><see langword="false"/> if the object is not exposed to COM, or provides no <c>IDispatch</c>.</returns>
    internal static bool TryGetProvidedDispatch(object value, [NotNullWhen(true)] out IDispatch? dispatch)
    {
        // A COM object is not exposed, and is not tested for the interface, which its strategy would look up on every call.
        // The Dispatch of another provider, a late-bound wrapper, is not read: it provides nothing here.
        dispatch = value is not ComObject && value is IDispatchProvider provider && IsExposedToCom(value) ? provider.Dispatch : null;
        return dispatch is not null;
    }

    /// <summary>
    /// Asks the COM object of a value for an interface, as a call through COM would: a late-bound or <c>dynamic</c> wrapper
    /// through its COM object, and an object that is exposed to COM through itself, or through the <c>IDispatch</c> that it
    /// provides when it does not implement the interface (see <see cref="TryGetProvidedDispatch"/>), also when a late-bound
    /// wrapper wraps it. It is the rule of <see cref="IDispatchProvider"/> for the interfaces of COM objects, which the
    /// object answers. An <see cref="UnknownWrapper"/> stands for its object, as in <see cref="ToUnmanaged{T}(T)"/>.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="iid">The identifier of the interface.</param>
    /// <param name="pointer">
    /// The AddRef'd pointer to the interface; 0 when the value stands for no COM object, a wrapper without one, which is no
    /// failure.
    /// </param>
    /// <returns>The <c>HRESULT</c> of the <c>QueryInterface</c> call that decides.</returns>
    internal static int QueryInterface(object value, in Guid iid, out nint pointer)
    {
        // The whole rule applies to the object of an UnknownWrapper, also the IDispatch that it provides: ToUnmanaged alone
        // would only ask its own interfaces. An UnknownWrapper of null stands for no object. The exception of a
        // DispatchWrapper names the parameter value, also for the public callers, e.g. instead of the source of
        // EventConnection.Advise: a known issue, since its message says what is wrong. A late-bound wrapper stands for the
        // COM object of its Dispatch, with the rule of what that is, as ComDynamic connects to the events of the wrapper:
        // an object of the application that is exposed to COM, e.g. a decorator that a call returned, which a dispinterface
        // wrapper wraps, falls back to the IDispatch that it provides too. A wrapper without a COM object, whose Dispatch
        // breaks its contract, stands for no object.
        object? target = UnwrapUnknownWrapper(value, nameof(value));
        if (target is not null && IsLateBoundWrapper(target, out IDispatch? wrapped))
        {
            target = wrapped;
        }

        if (target is null)
        {
            pointer = 0;
            return OleNativeMethods.S_OK;
        }

        return QueryObjectInterface(target, iid, out pointer);
    }

    // Asks the object that a value stands for, once a late-bound wrapper has been unwrapped, for an interface: its own COM
    // interface, and then the IDispatch that it provides when it is an object of the application that is exposed to COM
    // (see TryGetProvidedDispatch). ToUnmanaged asks the Dispatch of a wrapper with it too.
    private static int QueryObjectInterface(object target, in Guid iid, out nint pointer)
    {
        int hr = QueryOwnInterface(target, iid, out pointer);
        if (hr < 0 && TryGetProvidedDispatch(target, out IDispatch? provided))
        {
            hr = QueryOwnInterface(provided, iid, out pointer);
        }

        return hr;
    }

    /// <summary>
    /// Converts an object of the application that is exposed to COM (see <see cref="IsExposedToCom"/>) to
    /// <typeparamref name="T"/> through its COM interface, as its pointer is converted: to a COM object of its own, which
    /// calls the object through COM, when its COM interface answers the interface, e.g. because its class implements
    /// another declaration of it, of another interop assembly. The caller tests the managed type first.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if <typeparamref name="T"/> is no COM interface, the object is not exposed to COM, or its
    /// COM interface does not answer the interface.
    /// </returns>
    internal static bool TryConvertExposedObject<T>(object value, [NotNullWhen(true)] out T? result)
    {
        // An object that only a custom instance exposes is not asked, as IsExposedToCom does not ask the instance; nor is
        // any other value, e.g. a string where a server returned a BSTR for an interface, which would get a COM interface.
        if (InterfaceId<T>.Value is { } iid && IsExposedToCom(value) && QueryOwnInterface(value, iid, out nint pointer) >= 0)
        {
            try
            {
                result = ToManaged<T>((void*)pointer)!;
                return true;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }

        result = default;
        return false;
    }

    // Asks the COM interface of an object itself for an interface: the Dispatch of a wrapper, or the IDispatch that an
    // object provides, is not unwrapped again, as ToUnmanaged unwraps a wrapper once.
    private static int QueryOwnInterface(object value, in Guid iid, out nint pointer)
    {
        nint unknown = (nint)ToUnmanaged(value, unwrap: false);
        try
        {
            return Marshal.QueryInterface(unknown, in iid, out pointer);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Converts a managed object to an AddRef'd pointer to the interface <typeparamref name="T"/>, or to its <c>IUnknown</c>
    /// when <typeparamref name="T"/> is not a COM interface. A late-bound or <c>dynamic</c> wrapper is converted to the
    /// pointer of its COM object, which is asked for <typeparamref name="T"/>, also when the wrapper implements
    /// <typeparamref name="T"/> itself, with the rule of <see cref="QueryInterface"/>: an object of the application that
    /// is exposed to COM in it is asked itself, and then the <c>IDispatch</c> that it provides. A managed object that is
    /// exposed to COM (see <see cref="IsExposedToCom"/>) is converted to its own pointer, also when it implements
    /// <see cref="IDispatchProvider"/>. An <see cref="UnknownWrapper"/> is converted like its object, and a
    /// <see cref="DispatchWrapper"/> is rejected, as in a <c>VARIANT</c>.
    /// </summary>
    /// <param name="managed">The object.</param>
    /// <exception cref="ArgumentException">The object is a <see cref="DispatchWrapper"/>.</exception>
    /// <exception cref="InvalidCastException">The object does not implement the interface.</exception>
    /// <exception cref="COMException">Asking the object for the interface failed with another error.</exception>
    internal static void* ToUnmanaged<T>(T? managed) => ToUnmanaged(managed, unwrap: true);

    /// <summary>
    /// Converts a managed object like <see cref="ToUnmanaged{T}(T)"/>, and unwraps a late-bound or <c>dynamic</c> wrapper
    /// or an <see cref="UnknownWrapper"/> only when <paramref name="unwrap"/> is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A wrapper is unwrapped once, so that its object is converted itself, also when it is a managed object that wraps
    /// another one, or provides itself: the callers that have unwrapped a late-bound wrapper themselves pass its
    /// <see cref="IDispatchProvider.Dispatch"/> with <paramref name="unwrap"/> set to <see langword="false"/>.
    /// </remarks>
    internal static void* ToUnmanaged<T>(T? managed, bool unwrap)
    {
        // The interface is checked also for null, like in ToManaged. Null is no COM object: converting it does not use
        // the instance, which can still be set afterwards, and neither does the check.
        ThrowIfNotComInterface<T>();
        if (managed is null)
        {
            return null;
        }

        nint unknown;
        if (managed is ComObject comObject)
        {
            // The pointer is that of the object itself, which it caches per interface and which the methods of the
            // generated interfaces are called with: IUnknown when T is no COM interface. ComWrappers.TryGetComInstance
            // is not asked, because it still returns the pointer of an object that has been released, which is not
            // valid any more; the object throws an ObjectDisposedException then. A release on another thread between the
            // lookup and the AddRef is not caught: releasing an object that another thread uses is the caller's error,
            // see Release.
            Type key = InterfaceId<T>.Value is null ? typeof(IUnknownObject) : typeof(T);
            nint pointer = (nint)((IUnmanagedVirtualMethodTableProvider)comObject).GetVirtualMethodTableInfoForKey(key).ThisPointer;
            if (pointer != 0)
            {
                // The object owns the pointer: it must not be finalized before the reference is added.
                Marshal.AddRef(pointer);
                GC.KeepAlive(comObject);
                return (void*)pointer;
            }

            // The object returns no pointer, without failing, when its strategy has no details of the interface, as a
            // strategy of a custom instance that does not know IUnknownObject. The object has not been released, which
            // it checks first, so ComWrappers has its valid pointer.
            if (!global::System.Runtime.InteropServices.ComWrappers.TryGetComInstance(comObject, out unknown))
            {
                throw new InvalidCastException($"The object of type {managed.GetType()} does not implement the COM interface {typeof(T)}.");
            }
        }
        else if (unwrap && IsLateBoundWrapper(managed, out IDispatch? dispatch))
        {
            // A late-bound or dynamic wrapper is not exposed to COM: its COM object is (see IDispatchProvider), which is
            // asked for T, also when the wrapper implements T itself, e.g. IDispatch by forwarding to its Dispatch: the
            // COM interface of the wrapper would only have IUnknown. A class that only a custom instance exposes is a
            // wrapper too, as IsExposedToCom does not ask the instance. A managed object that is exposed to COM is exposed
            // itself, also when it wraps a COM object, as Release leaves it alone: its pointer converts back to the
            // object. The callers that have tested the object already, e.g. VariantMarshaller and ComObjects.GetDispatch,
            // test it again here only when it is an IDispatchProvider that is exposed to COM: one lookup of its class,
            // which costs less than the COM interface that follows, and keeps this method complete for its other callers.
            if (dispatch is null || InterfaceId<T>.Value is not { } wrappedIid)
            {
                return ToUnmanaged<object>(dispatch, unwrap: false);
            }

            // The Dispatch stands for the object with the rule of QueryInterface, as in ComConvert.To: an object of the
            // application that is exposed to COM, e.g. a decorator, is asked itself, and then the IDispatch that it
            // provides. It is not unwrapped again.
            int result = QueryObjectInterface(dispatch, wrappedIid, out nint pointer);
            return result >= 0 ? (void*)pointer : throw NoInterface(managed, typeof(T), result, lateBound: true);
        }
        else if (unwrap && InterfaceId<T>.Value is null && managed is UnknownWrapper or DispatchWrapper)
        {
            // An UnknownWrapper is converted like its object, wherever an object goes to COM, and not only in a VARIANT; a
            // DispatchWrapper is rejected everywhere (see UnwrapUnknownWrapper).
            return ToUnmanaged(UnwrapUnknownWrapper(managed, nameof(managed)), unwrap: true);
        }
        else if (Current is not { } wrappers)
        {
            return ComInterfaceMarshaller<T>.ConvertToUnmanaged(managed);
        }
        else if (!global::System.Runtime.InteropServices.ComWrappers.TryGetComInstance(managed, out unknown))
        {
            // Not the object of a COM object that another ComWrappers has created either: a managed object.
            unknown = wrappers.GetOrCreateComInterfaceForObject(managed, CreateComInterfaceFlags.None);
        }

        if (unknown == 0 || InterfaceId<T>.Value is not { } iid)
        {
            return (void*)unknown;
        }

        int hr = Marshal.QueryInterface(unknown, in iid, out nint converted);
        Marshal.Release(unknown);
        return hr >= 0 ? (void*)converted : throw NoInterface(managed, typeof(T), hr, lateBound: false);
    }

    // The exception of a QueryInterface that failed: E_NOINTERFACE is an interface that the object does not implement, and
    // another error, e.g. of a disconnected proxy, throws the exception of its HRESULT, as the cast of a COM object does.
    private static Exception NoInterface(object managed, Type type, int hr, bool lateBound) =>
        hr != OleNativeMethods.E_NOINTERFACE
            ? Marshal.GetExceptionForHR(hr)!
            : new InvalidCastException(
                lateBound
                    ? $"The object of type {managed.GetType()} stands for the COM object of its Dispatch, which does not implement the COM interface {type}."
                    : $"The object of type {managed.GetType()} does not implement the COM interface {type}.",
                hr);

    /// <summary>
    /// Releases an interface pointer.
    /// </summary>
    internal static void Free(void* unmanaged)
    {
        if (unmanaged is not null)
        {
            Marshal.Release((nint)unmanaged);
        }
    }

    // The IID of a source-generated COM interface, which the default strategy reads from the attribute that the generator
    // adds to the interface; null for other types such as object.
    private static class InterfaceId<T>
    {
        public static readonly Guid? Value =
            StrategyBasedComWrappers.DefaultIUnknownInterfaceDetailsStrategy.GetIUnknownDerivedDetails(typeof(T).TypeHandle)?.Iid;
    }
}
