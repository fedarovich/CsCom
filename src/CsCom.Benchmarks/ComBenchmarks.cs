using CsCom.Runtime;

namespace CsCom.Benchmarks;

/// <summary>
/// The base class of the benchmarks: each one calls a member of a COM object of the Microsoft Scripting Runtime with CsCom
/// and with the built-in COM interop, which is the baseline of its category.
/// </summary>
/// <remarks>
/// The objects do little work, so that the benchmarks measure the interop. Scripting.Dictionary is an apartment-threaded
/// object: the benchmark methods are marked with <see cref="STAThreadAttribute"/>, which makes BenchmarkDotNet run them on a
/// single-threaded apartment thread, as otherwise each call would be marshalled to another thread.
/// </remarks>
public abstract class ComBenchmarks
{
    /// <summary>The number of the items of the dictionaries.</summary>
    protected const int ItemCount = 100;

    /// <summary>The folder passed to <c>BuildPath</c>.</summary>
    protected const string Folder = @"C:\Program Files\Common Files";

    /// <summary>The file name passed to <c>BuildPath</c>.</summary>
    protected const string FileName = "readme.txt";

    /// <summary>The key of an item of the dictionaries. It is an object, so that the benchmarks do not measure boxing.</summary>
    protected static readonly object Key = GetKey(ItemCount / 2);

    /// <summary>The value that the benchmarks set.</summary>
    protected static readonly object Value = 42;

    /// <summary>Gets the key of an item of the dictionaries.</summary>
    protected static string GetKey(int index) => $"key{index}";

    /// <summary>Creates a COM object with the built-in COM interop.</summary>
    protected static object CreateBuiltIn(Guid clsid) => Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)!;

    /// <summary>Fails when the benchmark does not run in a single-threaded apartment.</summary>
    protected static void CheckApartment()
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("The benchmark must run on a single-threaded apartment thread: mark its method with [STAThread].");
        }
    }

    /// <summary>Fails when the object has another dispatch identifier for the member than the benchmarks invoke.</summary>
    protected static void CheckDispId(IDispatch dispatch, string name, int expected)
    {
        Span<int> ids = [0];
        int hr = ComDispatch.GetDispIds(dispatch, [name], ids);
        if (hr < 0 || ids[0] != expected)
        {
            throw new InvalidOperationException($"The DISPID of '{name}' is {ids[0]} (0x{hr:X8}) and not {expected}.");
        }
    }
}
