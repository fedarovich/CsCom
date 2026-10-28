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
        int hr = OleNativeMethods.CoCreateInstance(clsid, null, (uint)context, OleNativeMethods.IID_IUnknown, out void* unknown);
        if (hr == OleNativeMethods.CO_E_NOTINITIALIZED && JoinApartment())
        {
            hr = OleNativeMethods.CoCreateInstance(clsid, null, (uint)context, OleNativeMethods.IID_IUnknown, out unknown);
        }

        Marshal.ThrowExceptionForHR(hr);
        return ToObject<T>(unknown, $"of class {clsid:B}");
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="progId"/> and returns it as <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A <see cref="GeneratedComInterfaceAttribute"/>-attributed interface implemented by the class, or <see cref="object"/>.</typeparam>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Scripting.Dictionary</c>.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    public static T CreateInstance<T>(string progId, ClassContext context = DefaultContext) where T : class
    {
        return CreateInstance<T>(GetClassId(progId), context);
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="clsid"/> for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    public static dynamic CreateDynamicInstance(Guid clsid, ClassContext context = DefaultContext)
    {
        return DynamicDispatchObject.GetOrCreate(CreateInstance<IDispatch>(clsid, context));
    }

    /// <summary>
    /// Creates an instance of the COM class identified by <paramref name="progId"/> for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Excel.Application</c>.</param>
    /// <param name="context">The context in which the code that manages the newly created object will run.</param>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    public static dynamic CreateDynamicInstance(string progId, ClassContext context = DefaultContext)
    {
        return CreateDynamicInstance(GetClassId(progId), context);
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
        return GetActiveInstance<T>(clsid, $"the class {clsid:B}");
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
        return GetActiveInstance<T>(GetClassId(progId), $"'{progId}'");
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="clsid"/> for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="clsid">The class identifier.</param>
    /// <exception cref="COMException">No instance of the class is registered (<c>MK_E_UNAVAILABLE</c>).</exception>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    public static dynamic GetActiveDynamicInstance(Guid clsid)
    {
        return DynamicDispatchObject.GetOrCreate(GetActiveInstance<IDispatch>(clsid));
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="progId"/> for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Excel.Application</c>.</param>
    /// <exception cref="COMException">No instance of the class is registered (<c>MK_E_UNAVAILABLE</c>).</exception>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    public static dynamic GetActiveDynamicInstance(string progId)
    {
        return DynamicDispatchObject.GetOrCreate(GetActiveInstance<IDispatch>(progId));
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
        return TryGetActiveInstance(clsid, $"the class {clsid:B}", out instance);
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
        if (!TryGetClassId(progId, out Guid clsid))
        {
            instance = null;
            return false;
        }

        return TryGetActiveInstance(clsid, $"'{progId}'", out instance);
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="clsid"/>, if one is registered in the running
    /// object table, for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="clsid">The class identifier.</param>
    /// <param name="instance">The running instance, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an instance is running; <see langword="false"/> otherwise.</returns>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    public static bool TryGetActiveDynamicInstance(Guid clsid, [NotNullWhen(true)] out dynamic? instance)
    {
        instance = TryGetActiveInstance(clsid, out IDispatch? dispatch) ? DynamicDispatchObject.GetOrCreate(dispatch) : null;
        return instance is not null;
    }

    /// <summary>
    /// Gets the running instance of the COM class identified by <paramref name="progId"/>, if one is registered in the running
    /// object table, for late binding with <c>dynamic</c>.
    /// </summary>
    /// <param name="progId">The programmatic identifier of the class, e.g. <c>Excel.Application</c>.</param>
    /// <param name="instance">The running instance, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if an instance is running; <see langword="false"/> otherwise, also if the ProgID is not registered.</returns>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    public static bool TryGetActiveDynamicInstance(string progId, [NotNullWhen(true)] out dynamic? instance)
    {
        instance = TryGetActiveInstance(progId, out IDispatch? dispatch) ? DynamicDispatchObject.GetOrCreate(dispatch) : null;
        return instance is not null;
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

        return ToObject<T>(unknown, $"'{name}'");
    }

    /// <summary>
    /// Gets the object identified by a moniker display name for late binding with <c>dynamic</c>, like VB's <c>GetObject</c>.
    /// </summary>
    /// <param name="name">The display name of the moniker, e.g. the path of a document or <c>winmgmts:</c>.</param>
    /// <exception cref="COMException">The name is not a valid moniker or the object cannot be bound.</exception>
    /// <exception cref="InvalidCastException">The object does not implement <c>IDispatch</c>.</exception>
    [RequiresDynamicCode(ComDynamic.RequiresDynamicCodeMessage)]
    public static dynamic BindToDynamicMoniker(string name)
    {
        return DynamicDispatchObject.GetOrCreate(BindToMoniker<IDispatch>(name));
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
        return T.Create(CreateInstance<IDispatch>(clsid, context));
    }

    private static T GetActiveInstance<T>(Guid clsid, string description) where T : class
    {
        int hr = GetActiveObject(clsid, out void* unknown);
        if (hr == OleNativeMethods.MK_E_UNAVAILABLE)
        {
            throw new COMException($"No running instance of {description} is registered.", hr);
        }

        Marshal.ThrowExceptionForHR(hr);
        return ToObject<T>(unknown, $"of {description}");
    }

    private static bool TryGetActiveInstance<T>(Guid clsid, string description, [NotNullWhen(true)] out T? instance) where T : class
    {
        int hr = GetActiveObject(clsid, out void* unknown);
        if (hr == OleNativeMethods.MK_E_UNAVAILABLE)
        {
            instance = null;
            return false;
        }

        Marshal.ThrowExceptionForHR(hr);
        instance = ToObject<T>(unknown, $"of {description}");
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

    // Converts an IUnknown pointer and releases it.
    private static T ToObject<T>(void* unknown, string description) where T : class
    {
        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged(unknown)
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
