using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Interop.Scripting;
using BuiltInDictionary = CsCom.Benchmarks.BuiltIn.IDictionary;
using BuiltInFileSystem = CsCom.Benchmarks.BuiltIn.IFileSystem;
using CsComDictionary = Interop.Scripting.IDictionary;
using CsComFileSystem = Interop.Scripting.IFileSystem;

namespace CsCom.Benchmarks;

/// <summary>
/// Static dispatch: the members are called through the vtables of the interfaces, with the <c>[GeneratedComInterface]</c>
/// interfaces that CsCom generates and with the <c>[ComImport]</c> interfaces of the built-in COM interop.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class StaticDispatchBenchmarks : ComBenchmarks
{
    private BuiltInDictionary _builtInDictionary = null!;
    private CsComDictionary _csComDictionary = null!;
    private BuiltInFileSystem _builtInFileSystem = null!;
    private CsComFileSystem _csComFileSystem = null!;

    [GlobalSetup]
    public void Setup()
    {
        CheckApartment();
        _builtInDictionary = (BuiltInDictionary)CreateBuiltIn(Dictionary.Clsid);
        _csComDictionary = Dictionary.Create();
        for (int i = 0; i < ItemCount; i++)
        {
            object? key = GetKey(i);
            object? item = i;
            _builtInDictionary.Add(ref key, ref item);
            _csComDictionary.Add(key, item);
        }

        _builtInFileSystem = (BuiltInFileSystem)CreateBuiltIn(FileSystemObject.Clsid);
        _csComFileSystem = FileSystemObject.Create();
    }

    // A property without arguments: the cost of the call itself.

    [Benchmark(Baseline = true), BenchmarkCategory("Count"), STAThread]
    public int BuiltIn_Count() => _builtInDictionary.get_Count();

    [Benchmark, BenchmarkCategory("Count"), STAThread]
    public int CsCom_Count() => _csComDictionary.Count;

    // A VARIANT argument and a VARIANT_BOOL result.

    [Benchmark(Baseline = true), BenchmarkCategory("Exists"), STAThread]
    public bool BuiltIn_Exists()
    {
        object? key = Key;
        return _builtInDictionary.Exists(ref key);
    }

    [Benchmark, BenchmarkCategory("Exists"), STAThread]
    public bool CsCom_Exists() => _csComDictionary.Exists(Key);

    // A VARIANT argument and a VARIANT result.

    [Benchmark(Baseline = true), BenchmarkCategory("GetItem"), STAThread]
    public object? BuiltIn_GetItem()
    {
        object? key = Key;
        return _builtInDictionary.get_Item(ref key);
    }

    [Benchmark, BenchmarkCategory("GetItem"), STAThread]
    public object? CsCom_GetItem() => _csComDictionary.get_Item(Key);

    // Two VARIANT arguments.

    [Benchmark(Baseline = true), BenchmarkCategory("SetItem"), STAThread]
    public void BuiltIn_SetItem()
    {
        object? key = Key;
        object? value = Value;
        _builtInDictionary.set_Item(ref key, ref value);
    }

    [Benchmark, BenchmarkCategory("SetItem"), STAThread]
    public void CsCom_SetItem() => _csComDictionary.set_Item(Key, Value);

    // BSTR arguments and a BSTR result.

    [Benchmark(Baseline = true), BenchmarkCategory("BuildPath"), STAThread]
    public string? BuiltIn_BuildPath() => _builtInFileSystem.BuildPath(Folder, FileName);

    [Benchmark, BenchmarkCategory("BuildPath"), STAThread]
    public string? CsCom_BuildPath() => _csComFileSystem.BuildPath(Folder, FileName);

    // The keys of the dictionary, enumerated with IEnumVARIANT.

    [Benchmark(Baseline = true), BenchmarkCategory("Enumerate"), STAThread]
    public int BuiltIn_Enumerate()
    {
        int count = 0;
        foreach (object? key in _builtInDictionary)
        {
            count++;
        }

        return count;
    }

    [Benchmark, BenchmarkCategory("Enumerate"), STAThread]
    public int CsCom_Enumerate()
    {
        int count = 0;
        foreach (object? key in _csComDictionary)
        {
            count++;
        }

        return count;
    }
}
