using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace CsCom.Tests;

/// <summary>
/// Finds the native memory and the COM references that an action leaks.
/// </summary>
/// <remarks>
/// <para>
/// The memory is that of the COM task allocator, which <c>BSTR</c>s, <c>SAFEARRAY</c>s and <c>CoTaskMemAlloc</c> allocate from:
/// an <c>IMallocSpy</c> records the blocks that the thread of the action allocates and that nobody frees. The references are
/// those of the COM objects that the test names, whose reference counts must be the same before and after the action.
/// </para>
/// <para>
/// The action runs once before the measurement, so that the caches it fills are not reported, and the finalizers run before the
/// blocks and the references are counted, since wrappers that are not disposed release their resources there.
/// </para>
/// </remarks>
internal static class LeakDetector
{
    private const int Iterations = 3;

    /// <summary>
    /// Runs an action several times and fails if it leaves memory of the COM task allocator allocated, or changes the
    /// reference count of one of the COM objects.
    /// </summary>
    /// <param name="action">The action, which must release everything it allocates and may be repeated.</param>
    /// <param name="comObjects">The COM objects, managed or native, whose references are counted.</param>
    public static void AssertNoLeaks(Action action, params object[] comObjects)
    {
        if (Find(action, comObjects) is { } leaks)
        {
            Assert.Fail(leaks);
        }
    }

    /// <summary>
    /// Runs an action several times and describes what it leaks, or returns <see langword="null"/> if it leaks nothing.
    /// </summary>
    public static string? Find(Action action, params object[] comObjects)
    {
        // The first run fills the caches: the interfaces of the COM objects, the type information, the call sites of dynamic.
        Run(action, 1);
        Collect();

        int[] references = [.. comObjects.Select(GetReferenceCount)];
        ComAllocationSpy.Allocation[] blocks;
        using (var session = ComAllocationSpy.Track())
        {
            Run(action, Iterations);
            Collect();
            blocks = session.GetOutstanding();
        }

        var message = new StringBuilder();
        for (int i = 0; i < comObjects.Length; i++)
        {
            int count = GetReferenceCount(comObjects[i]);
            if (count != references[i])
            {
                message.AppendLine($"The reference count of the COM object {i} ({comObjects[i].GetType().Name}) changed from {references[i]} to {count} in {Iterations} runs.");
            }
        }

        if (blocks.Length > 0)
        {
            message.AppendLine($"{blocks.Length} blocks of the COM task allocator, {blocks.Sum(static b => (long)b.Size)} bytes, were not freed in {Iterations} runs:");
            foreach (var group in blocks.GroupBy(static b => (b.Size, Stack: b.Stack.ToString())))
            {
                message.AppendLine($"{group.Count()} x {group.Key.Size} bytes allocated at").AppendLine(group.Key.Stack);
            }
        }

        return message.Length > 0 ? message.ToString() : null;
    }

    /// <summary>
    /// Gets the number of references to a COM object: those of the callers of a managed object, or all the references to a
    /// native object, which include those of its wrapper.
    /// </summary>
    public static unsafe int GetReferenceCount(object comObject)
    {
        nint unknown = ComWrappers.TryGetComInstance(comObject, out nint instance)
            ? instance
            : (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(comObject);
        return Marshal.Release(unknown);
    }

    /// <summary>Runs the finalizers of the objects that are no longer used, which release native memory and COM references.</summary>
    public static void Collect()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    // A separate method, so that the objects of the action are not kept alive by the frame of the caller.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(Action action, int count)
    {
        for (int i = 0; i < count; i++)
        {
            action();
        }
    }
}

/// <summary>
/// An <c>IMallocSpy</c> that records the blocks of the COM task allocator allocated by the threads that track them.
/// </summary>
/// <remarks>
/// The spy is registered for the process when it is first used and stays registered: COM only revokes a spy once all the
/// blocks allocated under it are freed. It is called for every allocation of the process, but only records those of the
/// threads with a <see cref="Session"/>; a block is forgotten when any thread frees it. OLE Automation keeps freed
/// <c>BSTR</c>s in a cache, which hides the leaks and the frees, so the cache is disabled too.
/// </remarks>
internal static unsafe class ComAllocationSpy
{
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IMallocSpy = new("0000001D-0000-0000-C000-000000000046");
    private static readonly ConcurrentDictionary<nint, Allocation> Blocks = new();

    [ThreadStatic]
    private static Session? t_session;

    [ThreadStatic]
    private static nuint t_requested;

    static ComAllocationSpy()
    {
        SetOaNoCache();

        // A COM object without state: a pointer to the table of its methods. Neither is ever freed.
        var vtable = (void**)NativeMemory.Alloc(15, (nuint)sizeof(void*));
        vtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
        vtable[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
        vtable[3] = (delegate* unmanaged[Stdcall]<void*, nuint, nuint>)&PreAlloc;
        vtable[4] = (delegate* unmanaged[Stdcall]<void*, void*, void*>)&PostAlloc;
        vtable[5] = (delegate* unmanaged[Stdcall]<void*, void*, int, void*>)&PreFree;
        vtable[6] = (delegate* unmanaged[Stdcall]<void*, int, void>)&PostFree;
        vtable[7] = (delegate* unmanaged[Stdcall]<void*, void*, nuint, void**, int, nuint>)&PreRealloc;
        vtable[8] = (delegate* unmanaged[Stdcall]<void*, void*, int, void*>)&PostRealloc;
        vtable[9] = (delegate* unmanaged[Stdcall]<void*, void*, int, void*>)&PreGetSize;
        vtable[10] = (delegate* unmanaged[Stdcall]<void*, nuint, int, nuint>)&PostGetSize;
        vtable[11] = (delegate* unmanaged[Stdcall]<void*, void*, int, void*>)&PreDidAlloc;
        vtable[12] = (delegate* unmanaged[Stdcall]<void*, void*, int, int, int>)&PostDidAlloc;
        vtable[13] = (delegate* unmanaged[Stdcall]<void*, void>)&PreHeapMinimize;
        vtable[14] = (delegate* unmanaged[Stdcall]<void*, void>)&PostHeapMinimize;
        var spy = (void***)NativeMemory.Alloc((nuint)sizeof(void*));
        *spy = vtable;

        Marshal.ThrowExceptionForHR(CoRegisterMallocSpy(spy));
    }

    /// <summary>
    /// Starts recording the blocks that the current thread allocates, until the session is disposed.
    /// </summary>
    public static Session Track() => new();

    [DllImport("ole32.dll")]
    private static extern int CoRegisterMallocSpy(void* spy);

    [DllImport("oleaut32.dll")]
    private static extern void SetOaNoCache();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (*iid == IID_IUnknown || *iid == IID_IMallocSpy)
        {
            *result = self;
            return 0;
        }

        *result = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    // COM calls the methods of an allocation one after the other on the thread that allocates.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nuint PreAlloc(void* self, nuint size)
    {
        t_requested = size;
        return size;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* PostAlloc(void* self, void* block)
    {
        Record(block);
        return block;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* PreFree(void* self, void* block, int spied)
    {
        Forget(block);
        return block;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void PostFree(void* self, int spied)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nuint PreRealloc(void* self, void* block, nuint size, void** newBlock, int spied)
    {
        Forget(block);
        t_requested = size;
        *newBlock = block;
        return size;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* PostRealloc(void* self, void* block, int spied)
    {
        Record(block);
        return block;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* PreGetSize(void* self, void* block, int spied) => block;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nuint PostGetSize(void* self, nuint size, int spied) => size;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void* PreDidAlloc(void* self, void* block, int spied) => block;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int PostDidAlloc(void* self, void* block, int spied, int actual) => actual;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void PreHeapMinimize(void* self)
    {
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void PostHeapMinimize(void* self)
    {
    }

    private static void Record(void* block)
    {
        if (t_session is { } session && block is not null)
        {
            Blocks[(nint)block] = new Allocation(session, t_requested, new StackTrace(2, fNeedFileInfo: false));
        }
    }

    private static void Forget(void* block)
    {
        if (block is not null && !Blocks.IsEmpty)
        {
            Blocks.TryRemove((nint)block, out _);
        }
    }

    /// <summary>A block that was allocated and not freed yet.</summary>
    /// <param name="Session">The session that recorded the block.</param>
    /// <param name="Size">The size of the block in bytes.</param>
    /// <param name="Stack">The managed code that allocated the block.</param>
    public sealed record Allocation(Session Session, nuint Size, StackTrace Stack);

    /// <summary>Records the blocks that the thread that created it allocates.</summary>
    public sealed class Session : IDisposable
    {
        internal Session()
        {
            if (t_session is not null)
            {
                throw new InvalidOperationException("The thread already tracks its allocations.");
            }

            t_session = this;
        }

        /// <summary>Gets the blocks that the thread allocated and that no thread has freed.</summary>
        public Allocation[] GetOutstanding() => [.. Blocks.Values.Where(b => b.Session == this)];

        /// <summary>Stops recording and forgets the blocks of the session.</summary>
        public void Dispose()
        {
            t_session = null;
            foreach (var (block, allocation) in Blocks)
            {
                if (allocation.Session == this)
                {
                    Blocks.TryRemove(block, out _);
                }
            }
        }
    }
}
