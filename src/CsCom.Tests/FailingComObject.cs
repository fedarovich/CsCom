using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Tests;

/// <summary>
/// A native COM object that answers <c>QueryInterface</c> for <c>IUnknown</c>, and for <c>IDispatch</c> if asked to, and
/// fails it with an <c>HRESULT</c> for every other interface, like the proxy of a server that has ended or is busy: a
/// managed object exposed to COM could only fail it with <c>E_NOINTERFACE</c>.
/// </summary>
internal static unsafe class FailingComObject
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    // The table of the methods, which all the objects share; it is never freed.
    private static readonly void** Vtable = CreateVtable();

    /// <summary>
    /// Creates the object in a unique managed object of a <see cref="StrategyBasedComWrappers"/> of its own, which calls it
    /// through COM; the object is freed when the last reference is released.
    /// </summary>
    /// <param name="hr">The <c>HRESULT</c> of <c>QueryInterface</c> for the other interfaces.</param>
    /// <param name="dispatch">Whether the object answers <c>IDispatch</c>, whose methods return <c>E_NOTIMPL</c>.</param>
    public static object Create(int hr, bool dispatch = false)
    {
        var instance = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
        *instance = new Instance { Vtable = Vtable, References = 1, Error = hr, AnswersDispatch = dispatch ? 1 : 0 };
        try
        {
            return new StrategyBasedComWrappers().GetOrCreateObjectForComInstance((nint)instance, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release((nint)instance);
        }
    }

    private static void** CreateVtable()
    {
        var vtable = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
        vtable[0] = (delegate* unmanaged[Stdcall]<Instance*, Guid*, void**, int>)&QueryInterface;
        vtable[1] = (delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<Instance*, uint>)&Release;
        vtable[3] = (delegate* unmanaged[Stdcall]<Instance*, uint*, int>)&GetTypeInfoCount;
        vtable[4] = (delegate* unmanaged[Stdcall]<Instance*, uint, uint, void**, int>)&GetTypeInfo;
        vtable[5] = (delegate* unmanaged[Stdcall]<Instance*, Guid*, char**, uint, uint, int*, int>)&GetIDsOfNames;
        vtable[6] = (delegate* unmanaged[Stdcall]<Instance*, int, Guid*, uint, ushort, void*, void*, void*, uint*, int>)&Invoke;
        return vtable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(Instance* self, Guid* iid, void** result)
    {
        if (*iid == IID_IUnknown || (self->AnswersDispatch != 0 && *iid == IID_IDispatch))
        {
            Interlocked.Increment(ref self->References);
            *result = self;
            return 0;
        }

        *result = null;
        return self->Error;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->References);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(Instance* self)
    {
        int references = Interlocked.Decrement(ref self->References);
        if (references == 0)
        {
            NativeMemory.Free(self);
        }

        return (uint)references;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoCount(Instance* self, uint* count) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfo(Instance* self, uint index, uint lcid, void** typeInfo) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIDsOfNames(Instance* self, Guid* iid, char** names, uint count, uint lcid, int* dispIds) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(Instance* self, int dispId, Guid* iid, uint lcid, ushort flags, void* parameters, void* result, void* exception, uint* argumentError) =>
        E_NOTIMPL;

    private struct Instance
    {
        public void** Vtable;
        public int References;
        public int Error;
        public int AnswersDispatch;
    }
}
