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
        void* unknown = CreateInstanceCore(clsid, context);
        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged(unknown)
                ?? throw new InvalidCastException($"The COM object of class {clsid:B} could not be converted to {typeof(T)}.");
        }
        finally
        {
            Marshal.Release((nint)unknown);
        }
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
        return new DynamicDispatchObject(CreateInstance<IDispatch>(clsid, context));
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
    /// Gets the class identifier (CLSID) of a programmatic identifier (ProgID).
    /// </summary>
    /// <param name="progId">The programmatic identifier, e.g. <c>Scripting.Dictionary</c>.</param>
    /// <exception cref="COMException">The ProgID is not registered.</exception>
    public static Guid GetClassId(string progId)
    {
        ArgumentException.ThrowIfNullOrEmpty(progId);
        int hr = OleNativeMethods.CLSIDFromProgID(progId, out Guid clsid);
        if (hr == OleNativeMethods.CO_E_CLASSSTRING)
        {
            throw new COMException($"The ProgID '{progId}' is not registered.", hr);
        }

        Marshal.ThrowExceptionForHR(hr);
        return clsid;
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

    private static void* CreateInstanceCore(Guid clsid, ClassContext context)
    {
        int hr = OleNativeMethods.CoCreateInstance(clsid, null, (uint)context, OleNativeMethods.IID_IUnknown, out void* unknown);
        if (hr == OleNativeMethods.CO_E_NOTINITIALIZED)
        {
            // The calling thread has not entered an apartment yet; join the multithreaded apartment like the built-in COM interop does.
            int initHr = OleNativeMethods.CoInitializeEx(null, OleNativeMethods.COINIT_MULTITHREADED);
            if (initHr < 0)
            {
                Marshal.ThrowExceptionForHR(initHr);
            }

            hr = OleNativeMethods.CoCreateInstance(clsid, null, (uint)context, OleNativeMethods.IID_IUnknown, out unknown);
        }

        Marshal.ThrowExceptionForHR(hr);
        return unknown;
    }
}
