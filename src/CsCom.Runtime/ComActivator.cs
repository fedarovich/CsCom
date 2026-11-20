using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// The execution contexts in which a COM class can be activated (<c>CLSCTX</c>).
/// </summary>
[Flags]
public enum ClassContext : uint
{
    /// <summary>The code that creates and manages objects of this class is a DLL that runs in the same process.</summary>
    InprocServer = 0x1,

    /// <summary>The code that manages objects of this class is an in-process handler.</summary>
    InprocHandler = 0x2,

    /// <summary>The EXE code that creates and manages objects of this class runs on the same machine in a separate process.</summary>
    LocalServer = 0x4,

    /// <summary>A remote context.</summary>
    RemoteServer = 0x10,

    /// <summary>The combination of <see cref="InprocServer"/>, <see cref="InprocHandler"/>, <see cref="LocalServer"/> and <see cref="RemoteServer"/>.</summary>
    All = InprocServer | InprocHandler | LocalServer | RemoteServer,
}

/// <summary>
/// Creates instances of COM classes and wraps them using source-generated COM interop.
/// </summary>
/// <remarks>
/// The methods throw a <see cref="NotSupportedException"/> for a type that a COM object cannot be converted to before
/// anything else: before they look up a ProgID, create or get the object. That is an interface that is not a
/// source-generated COM interface (<see cref="GeneratedComInterfaceAttribute"/>), or a class other than
/// <see cref="object"/> and <see cref="ComObject"/>, e.g. a late-bound wrapper, which
/// <see cref="CreateDispatchInstance{T}(Guid, bool, ClassContext)"/> creates.
/// </remarks>
public static unsafe class ComActivator
{
    /// <summary>
    /// The default activation context: in-process and local servers.
    /// </summary>
    public const ClassContext DefaultContext = ClassContext.InprocServer | ClassContext.LocalServer;

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="clsid"/> and returns it as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    public static T CreateInstance<T>(Guid clsid, ClassContext context = DefaultContext) where T : class
    {
        // A context that converts to bool too, i.e. default, still calls this overload, as before the overloads with unique,
        // like in the other methods with a context: C# prefers an overload whose parameters all have arguments to one that
        // needs a default value.
        return CreateInstance<T>(clsid, unique: false, context);
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="clsid"/> and returns it as <typeparamref name="T"/>,
    /// in a managed object of its own if <paramref name="unique"/> is set.
    /// </summary>
    /// <remarks>
    /// A unique managed object is not shared with other references to the COM object, so its COM references can be released
    /// at once with <see cref="ComMarshalling.Release"/>. The objects that its members return are shared, unless they are
    /// marshalled as unique objects too.
    /// </remarks>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="unique">
    /// <see langword="true"/> for a managed object of its own; <see langword="false"/> for the object that all the
    /// references to the COM object share, which is released when it is garbage collected.
    /// </param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    public static T CreateInstance<T>(Guid clsid, bool unique, ClassContext context = DefaultContext) where T : class
    {
        // Checked first, so that no server is started for an object that cannot be converted.
        ThrowIfNotSupported<T>();
        return Create<T>(clsid, unique, context);
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="progId"/> and returns it as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Scripting.Dictionary</c>.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    public static T CreateInstance<T>(string progId, ClassContext context = DefaultContext) where T : class
    {
        return CreateInstance<T>(progId, unique: false, context);
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="progId"/> and returns it as <typeparamref name="T"/>,
    /// in a managed object of its own if <paramref name="unique"/> is set, see
    /// <see cref="CreateInstance{T}(Guid, bool, ClassContext)"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Scripting.Dictionary</c>.</param>
    /// <param name="unique">Whether the COM object gets a managed object of its own rather than the shared one.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    public static T CreateInstance<T>(string progId, bool unique, ClassContext context = DefaultContext) where T : class
    {
        // Checked before the ProgID is looked up, so that an interface that cannot be used fails first, like in the other
        // methods.
        ThrowIfNotSupported<T>();
        return Create<T>(GetClassId(progId), unique, context);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="clsid"/>, registered in the running object table,
    /// and returns it as <typeparamref name="T"/>, like <c>Marshal.GetActiveObject</c> of .NET Framework.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <exception cref="COMException">No instance of the class is registered (<c>MK_E_UNAVAILABLE</c>).</exception>
    public static T GetActiveInstance<T>(Guid clsid) where T : class
    {
        return GetActiveInstance<T>(clsid, unique: false);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="clsid"/>, like
    /// <see cref="GetActiveInstance{T}(Guid)"/>, in a managed object of its own if <paramref name="unique"/> is set, see
    /// <see cref="CreateInstance{T}(Guid, bool, ClassContext)"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="unique">Whether the COM object gets a managed object of its own rather than the shared one.</param>
    /// <exception cref="COMException">No instance of the class is registered (<c>MK_E_UNAVAILABLE</c>).</exception>
    public static T GetActiveInstance<T>(Guid clsid, bool unique) where T : class
    {
        ThrowIfNotSupported<T>();
        return GetActiveInstance<T>(clsid, $"the class {clsid:B}", unique);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="progId"/>, registered in the running object
    /// table, and returns it as <typeparamref name="T"/>, like <c>Marshal.GetActiveObject</c> of .NET Framework.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Excel.Application</c>.</param>
    /// <exception cref="COMException">No instance of the class is registered (<c>MK_E_UNAVAILABLE</c>).</exception>
    public static T GetActiveInstance<T>(string progId) where T : class
    {
        return GetActiveInstance<T>(progId, unique: false);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="progId"/>, like
    /// <see cref="GetActiveInstance{T}(string)"/>, in a managed object of its own if <paramref name="unique"/> is set, see
    /// <see cref="CreateInstance{T}(Guid, bool, ClassContext)"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Excel.Application</c>.</param>
    /// <param name="unique">Whether the COM object gets a managed object of its own rather than the shared one.</param>
    /// <exception cref="COMException">No instance of the class is registered (<c>MK_E_UNAVAILABLE</c>).</exception>
    public static T GetActiveInstance<T>(string progId, bool unique) where T : class
    {
        ThrowIfNotSupported<T>();
        return GetActiveInstance<T>(GetClassId(progId), $"'{progId}'", unique);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="clsid"/>, if one is registered in the running
    /// object table, as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="instance">The running instance, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an instance is running; <see langword="false"/> otherwise.</returns>
    public static bool TryGetActiveInstance<T>(Guid clsid, [NotNullWhen(true)] out T? instance) where T : class
    {
        return TryGetActiveInstance(clsid, unique: false, out instance);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="clsid"/>, like
    /// <see cref="TryGetActiveInstance{T}(Guid, out T)"/>, in a managed object of its own if <paramref name="unique"/> is
    /// set, see <see cref="CreateInstance{T}(Guid, bool, ClassContext)"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="unique">Whether the COM object gets a managed object of its own rather than the shared one.</param>
    /// <param name="instance">The running instance, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an instance is running; <see langword="false"/> otherwise.</returns>
    public static bool TryGetActiveInstance<T>(Guid clsid, bool unique, [NotNullWhen(true)] out T? instance) where T : class
    {
        ThrowIfNotSupported<T>();
        return TryGetActiveInstance(clsid, $"the class {clsid:B}", unique, out instance);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="progId"/>, if one is registered in the running
    /// object table, as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Excel.Application</c>.</param>
    /// <param name="instance">The running instance, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an instance is running; <see langword="false"/> otherwise, also if the ProgID is not registered.</returns>
    public static bool TryGetActiveInstance<T>(string progId, [NotNullWhen(true)] out T? instance) where T : class
    {
        return TryGetActiveInstance(progId, unique: false, out instance);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="progId"/>, like
    /// <see cref="TryGetActiveInstance{T}(string, out T)"/>, in a managed object of its own if <paramref name="unique"/> is
    /// set, see <see cref="CreateInstance{T}(Guid, bool, ClassContext)"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Excel.Application</c>.</param>
    /// <param name="unique">Whether the COM object gets a managed object of its own rather than the shared one.</param>
    /// <param name="instance">The running instance, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an instance is running; <see langword="false"/> otherwise, also if the ProgID is not registered.</returns>
    public static bool TryGetActiveInstance<T>(string progId, bool unique, [NotNullWhen(true)] out T? instance) where T : class
    {
        // Checked before the ProgID, so that an interface that cannot be used fails also when the ProgID is not registered.
        ThrowIfNotSupported<T>();
        if (!TryGetClassId(progId, out Guid clsid))
        {
            instance = null;
            return false;
        }

        return TryGetActiveInstance(clsid, $"'{progId}'", unique, out instance);
    }

    /// <summary>
    /// Gets the object identified by a moniker display name and returns it as <typeparamref name="T"/>, like VB's
    /// <c>GetObject</c>: e.g. the path of a document, which is opened by its application, <c>winmgmts:</c> for WMI or
    /// <c>new:Scripting.Dictionary</c> for a new instance.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the object, or <see cref="object"/>.</typeparam>
    /// <param name="name">The display name of the moniker.</param>
    /// <exception cref="COMException">The name is not a valid moniker or the object cannot be bound.</exception>
    public static T BindToMoniker<T>(string name) where T : class
    {
        return BindToMoniker<T>(name, unique: false);
    }

    /// <summary>
    /// Gets the object identified by a moniker display name, like <see cref="BindToMoniker{T}(string)"/>, in a managed object
    /// of its own if <paramref name="unique"/> is set, see <see cref="CreateInstance{T}(Guid, bool, ClassContext)"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the object, or <see cref="object"/>.</typeparam>
    /// <param name="name">The display name of the moniker.</param>
    /// <param name="unique">Whether the COM object gets a managed object of its own rather than the shared one.</param>
    /// <exception cref="COMException">The name is not a valid moniker or the object cannot be bound.</exception>
    public static T BindToMoniker<T>(string name, bool unique) where T : class
    {
        // Checked before the name, like the ProgID in the other methods.
        ThrowIfNotSupported<T>();
        ArgumentException.ThrowIfNullOrEmpty(name);
        int hr = OleNativeMethods.CoGetObject(name, null, OleNativeMethods.IID_IUnknown, out void* unknown);
        if (hr == OleNativeMethods.CO_E_NOTINITIALIZED && JoinApartment())
        {
            hr = OleNativeMethods.CoGetObject(name, null, OleNativeMethods.IID_IUnknown, out unknown);
        }

        if (hr < 0)
        {
            throw new COMException($"The moniker '{name}' cannot be bound to an object: {Marshal.GetExceptionForHR(hr)!.Message}", hr);
        }

        return ToObject<T>(unknown, $"'{name}'", unique);
    }

    /// <summary>
    /// Gets the class identifier (CLSID) of a programmatic identifier (ProgID).
    /// </summary>
    /// <param name="progId">The programmatic identifier, e.g. <c>Scripting.Dictionary</c>.</param>
    /// <exception cref="COMException">The ProgID is not registered.</exception>
    public static Guid GetClassId(string progId)
    {
        if (!TryGetClassId(progId, out Guid clsid))
        {
            throw new COMException($"The ProgID '{progId}' is not registered.", OleNativeMethods.CO_E_CLASSSTRING);
        }

        return clsid;
    }

    private static bool TryGetClassId(string progId, out Guid clsid)
    {
        ArgumentException.ThrowIfNullOrEmpty(progId);
        int hr = OleNativeMethods.CLSIDFromProgID(progId, out clsid);
        if (hr == OleNativeMethods.CO_E_CLASSSTRING)
        {
            return false;
        }

        Marshal.ThrowExceptionForHR(hr);
        return true;
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="clsid"/> and wraps it into the late-bound wrapper <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A late-bound wrapper of a dispinterface.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    public static T CreateDispatchInstance<T>(Guid clsid, ClassContext context = DefaultContext) where T : class, IDispatchWrapper<T>
    {
        return CreateDispatchInstance<T>(clsid, unique: false, context);
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="clsid"/> and wraps it into the late-bound wrapper
    /// <typeparamref name="T"/>, with a managed object of its own if <paramref name="unique"/> is set, see
    /// <see cref="CreateInstance{T}(Guid, bool, ClassContext)"/>: <see cref="ComMarshalling.Release"/> of the wrapper
    /// releases it.
    /// </summary>
    /// <typeparam name="T">A late-bound wrapper of a dispinterface.</typeparam>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="unique">Whether the COM object gets a managed object of its own rather than the shared one.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    public static T CreateDispatchInstance<T>(Guid clsid, bool unique, ClassContext context = DefaultContext) where T : class, IDispatchWrapper<T>
    {
        IDispatch dispatch = Create<IDispatch>(clsid, unique, context);
        return unique ? UniqueDispatchWrapperMarshaller<T>.CreateWrapper(dispatch) : DispatchWrapperMarshaller<T>.Wrap(dispatch);
    }

    // Tests T first, so that no server is started and no ProgID is looked up for an object that cannot be converted to it.
    // A COM object is a ComObject, which is sealed, so it converts to no other class than ComObject and object; a class
    // of the application whose instances a class factory of its own returns, e.g. in the same process, is got as an
    // interface that it implements, or as object.
    private static void ThrowIfNotSupported<T>()
    {
        ComMarshalling.ThrowIfNotComInterface<T>();
        if (!typeof(T).IsInterface && !typeof(T).IsAssignableFrom(typeof(ComObject)))
        {
            throw new NotSupportedException(typeof(IDispatchProvider).IsAssignableFrom(typeof(T))
                ? $"A COM object cannot be converted to the late-bound wrapper {typeof(T)}: get it as IDispatch and wrap it, e.g. with ComDynamic.ToDynamic for a dynamic object, or create a wrapper that implements IDispatchWrapper<T> with {nameof(CreateDispatchInstance)}."
                : $"A COM object cannot be converted to the class {typeof(T)}: use a source-generated COM interface ([GeneratedComInterface]) or object.");
        }
    }

    // The private methods leave the test of T to the public ones, which make it first: see the remarks of the class.
    private static T Create<T>(Guid clsid, bool unique, ClassContext context) where T : class
    {
        int hr = OleNativeMethods.CoCreateInstance(clsid, null, (uint)context, OleNativeMethods.IID_IUnknown, out void* unknown);
        if (hr == OleNativeMethods.CO_E_NOTINITIALIZED && JoinApartment())
        {
            hr = OleNativeMethods.CoCreateInstance(clsid, null, (uint)context, OleNativeMethods.IID_IUnknown, out unknown);
        }

        Marshal.ThrowExceptionForHR(hr);
        return ToObject<T>(unknown, $"of class {clsid:B}", unique);
    }

    private static T GetActiveInstance<T>(Guid clsid, string description, bool unique) where T : class
    {
        int hr = GetActiveObject(clsid, out void* unknown);
        if (hr == OleNativeMethods.MK_E_UNAVAILABLE)
        {
            throw new COMException($"No running instance of {description} is registered.", hr);
        }

        Marshal.ThrowExceptionForHR(hr);
        return ToObject<T>(unknown, $"of {description}", unique);
    }

    private static bool TryGetActiveInstance<T>(Guid clsid, string description, bool unique, [NotNullWhen(true)] out T? instance) where T : class
    {
        int hr = GetActiveObject(clsid, out void* unknown);
        if (hr == OleNativeMethods.MK_E_UNAVAILABLE)
        {
            instance = null;
            return false;
        }

        Marshal.ThrowExceptionForHR(hr);
        instance = ToObject<T>(unknown, $"of {description}", unique);
        return true;
    }

    private static int GetActiveObject(Guid clsid, out void* unknown)
    {
        int hr = OleNativeMethods.GetActiveObject(clsid, null, out unknown);
        if (hr == OleNativeMethods.CO_E_NOTINITIALIZED && JoinApartment())
        {
            hr = OleNativeMethods.GetActiveObject(clsid, null, out unknown);
        }

        return hr;
    }

    // Converts an IUnknown pointer, to a managed object of its own if 'unique', and releases it.
    private static T ToObject<T>(void* unknown, string description, bool unique) where T : class
    {
        try
        {
            return ComMarshalling.ToManaged<T>(unknown, unique)
                ?? throw new InvalidCastException($"The COM object {description} could not be converted to {typeof(T)}.");
        }
        finally
        {
            Marshal.Release((nint)unknown);
        }
    }

    // The calling thread has not entered an apartment yet: joins the multithreaded apartment like the built-in COM interop
    // does, so that the call can be repeated.
    private static bool JoinApartment()
    {
        int hr = OleNativeMethods.CoInitializeEx(null, OleNativeMethods.COINIT_MULTITHREADED);
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }

        return true;
    }
}
