using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Interop.Scripting;

namespace CsCom.Benchmarks;

/// <summary>
/// Dynamic dispatch with C# <c>dynamic</c>: the members are found by name and invoked through <c>IDispatch</c>, with the
/// dynamic objects of CsCom.Runtime.Dynamic and with the COM binder of the built-in COM interop.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DynamicKeywordBenchmarks : ComBenchmarks
{
    private dynamic _builtInDictionary = null!;
    private dynamic _csComDictionary = null!;
    private dynamic _builtInFileSystem = null!;
    private dynamic _csComFileSystem = null!;

    [GlobalSetup]
    public void Setup()
    {
        CheckApartment();
        _builtInDictionary = CreateBuiltIn(Dictionary.Clsid);
        _csComDictionary = Dictionary.CreateDynamic();
        for (int i = 0; i < ItemCount; i++)
        {
            _builtInDictionary.Add(GetKey(i), i);
            _csComDictionary.Add(GetKey(i), i);
        }

        _builtInFileSystem = CreateBuiltIn(FileSystemObject.Clsid);
        _csComFileSystem = FileSystemObject.CreateDynamic();
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Count"), STAThread]
    public int BuiltIn_Count() => _builtInDictionary.Count;

    [Benchmark, BenchmarkCategory("Count"), STAThread]
    public int CsCom_Count() => _csComDictionary.Count;

    [Benchmark(Baseline = true), BenchmarkCategory("Exists"), STAThread]
    public bool BuiltIn_Exists() => _builtInDictionary.Exists(Key);

    [Benchmark, BenchmarkCategory("Exists"), STAThread]
    public bool CsCom_Exists() => _csComDictionary.Exists(Key);

    [Benchmark(Baseline = true), BenchmarkCategory("GetItem"), STAThread]
    public object? BuiltIn_GetItem() => _builtInDictionary[Key];

    [Benchmark, BenchmarkCategory("GetItem"), STAThread]
    public object? CsCom_GetItem() => _csComDictionary[Key];

    [Benchmark(Baseline = true), BenchmarkCategory("SetItem"), STAThread]
    public void BuiltIn_SetItem() => _builtInDictionary[Key] = Value;

    [Benchmark, BenchmarkCategory("SetItem"), STAThread]
    public void CsCom_SetItem() => _csComDictionary[Key] = Value;

    [Benchmark(Baseline = true), BenchmarkCategory("BuildPath"), STAThread]
    public string? BuiltIn_BuildPath() => _builtInFileSystem.BuildPath(Folder, FileName);

    [Benchmark, BenchmarkCategory("BuildPath"), STAThread]
    public string? CsCom_BuildPath() => _csComFileSystem.BuildPath(Folder, FileName);

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
