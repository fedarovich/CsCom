using CsCom.Benchmarks.BuiltIn;
using CsCom.Runtime;

namespace CsCom.Benchmarks;

// The Scripting Runtime has no dispinterfaces, for which CsCom generates late-bound wrappers, so these wrappers are written
// like the generated ones: the members invoke the DISPIDs of the type library through IDispatch::Invoke.

/// <summary>
/// A late-bound wrapper of Scripting.Dictionary.
/// </summary>
internal sealed class DictionaryWrapper(IDispatch dispatch) : DispatchObject(dispatch)
{
    public object? this[object? key] { get => InvokeGet(ScriptingDispIds.Item, key); set => InvokePut(ScriptingDispIds.Item, value, key); }

    public int Count => Convert.ToInt32(InvokeGet(ScriptingDispIds.Count));

    public bool Exists(object? key) => Convert.ToBoolean(InvokeMethod(ScriptingDispIds.Exists, key));

    public ComEnumerator GetEnumerator() => GetEnumeratorCore();
}

/// <summary>
/// A late-bound wrapper of Scripting.FileSystemObject.
/// </summary>
internal sealed class FileSystemWrapper(IDispatch dispatch) : DispatchObject(dispatch)
{
    public string? BuildPath(string? path, string? name) => (string?)InvokeMethod(ScriptingDispIds.BuildPath, path, name);
}
