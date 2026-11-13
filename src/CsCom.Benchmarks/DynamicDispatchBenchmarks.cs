using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using CsCom.Benchmarks.BuiltIn;
using Interop.Scripting;

namespace CsCom.Benchmarks;

/// <summary>
/// Dynamic dispatch with typed members: the members are invoked by their DISPIDs through <c>IDispatch::Invoke</c>, with
/// late-bound wrappers like those that CsCom generates for dispinterfaces and with the dispinterfaces
/// (<see cref="System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIDispatch"/>) of the built-in COM interop.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class DynamicDispatchBenchmarks : ComBenchmarks
{
    private IDictionaryDispatch _builtInDictionary = null!;
    private DictionaryWrapper _csComDictionary = null!;
    private IFileSystemDispatch _builtInFileSystem = null!;
    private FileSystemWrapper _csComFileSystem = null!;

    [GlobalSetup]
    public void Setup()
    {
        CheckApartment();
        var dictionary = Dictionary.Create();
        CheckDispId(dictionary, "Item", ScriptingDispIds.Item);
        CheckDispId(dictionary, "Count", ScriptingDispIds.Count);
        CheckDispId(dictionary, "Exists", ScriptingDispIds.Exists);
        _builtInDictionary = (IDictionaryDispatch)CreateBuiltIn(Dictionary.Clsid);
        _csComDictionary = new DictionaryWrapper(dictionary);
        for (int i = 0; i < ItemCount; i++)
        {
            _builtInDictionary[GetKey(i)] = i;
            _csComDictionary[GetKey(i)] = i;
        }

        var fileSystem = FileSystemObject.Create();
        CheckDispId(fileSystem, "BuildPath", ScriptingDispIds.BuildPath);
        _builtInFileSystem = (IFileSystemDispatch)CreateBuiltIn(FileSystemObject.Clsid);
        _csComFileSystem = new FileSystemWrapper(fileSystem);
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
