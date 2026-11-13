using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CsCom.Benchmarks.BuiltIn;

// The declarations of the built-in COM interop for the members that the benchmarks call, as TlbImp generates them for
// scrrun.dll. The methods of the vtable interfaces are declared in the order of the vtable, up to the last one that is called.

/// <summary>
/// The dual interface of Scripting.Dictionary, called through its vtable.
/// </summary>
[ComImport]
[Guid("42C642C1-97E1-11CF-978F-00A02463E06F")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IDictionary
{
    void putref_Item([In, MarshalAs(UnmanagedType.Struct)] ref object? key, [In, MarshalAs(UnmanagedType.Struct)] ref object? item);

    void set_Item([In, MarshalAs(UnmanagedType.Struct)] ref object? key, [In, MarshalAs(UnmanagedType.Struct)] ref object? item);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? get_Item([In, MarshalAs(UnmanagedType.Struct)] ref object? key);

    void Add([In, MarshalAs(UnmanagedType.Struct)] ref object? key, [In, MarshalAs(UnmanagedType.Struct)] ref object? item);

    int get_Count();

    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool Exists([In, MarshalAs(UnmanagedType.Struct)] ref object? key);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? Items();

    void set_Key([In, MarshalAs(UnmanagedType.Struct)] ref object? key, [In, MarshalAs(UnmanagedType.Struct)] ref object? value);

    [return: MarshalAs(UnmanagedType.Struct)]
    object? Keys();

    void Remove([In, MarshalAs(UnmanagedType.Struct)] ref object? key);

    void RemoveAll();

    void set_CompareMode(int mode);

    int get_CompareMode();

    [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = Marshalers.Enumerator)]
    IEnumerator GetEnumerator();
}

/// <summary>
/// The interface of Scripting.Dictionary as a dispinterface, whose members are called through <c>IDispatch::Invoke</c>.
/// </summary>
[ComImport]
[Guid("42C642C1-97E1-11CF-978F-00A02463E06F")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IDictionaryDispatch
{
    [DispId(ScriptingDispIds.Item)]
    [IndexerName("Item")]
    object? this[object? key] { get; set; }

    [DispId(ScriptingDispIds.Count)]
    int Count { get; }

    [DispId(ScriptingDispIds.Exists)]
    bool Exists(object? key);

    [DispId(ScriptingDispIds.NewEnum)]
    [return: MarshalAs(UnmanagedType.CustomMarshaler, MarshalType = Marshalers.Enumerator)]
    IEnumerator GetEnumerator();
}

/// <summary>
/// The dual interface of Scripting.FileSystemObject, called through its vtable.
/// </summary>
[ComImport]
[Guid("0AB5A3D0-E5B6-11D0-ABF5-00A0C90FFFC0")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IFileSystem
{
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? get_Drives();

    [return: MarshalAs(UnmanagedType.BStr)]
    string? BuildPath([MarshalAs(UnmanagedType.BStr)] string? path, [MarshalAs(UnmanagedType.BStr)] string? name);
}

/// <summary>
/// The interface of Scripting.FileSystemObject as a dispinterface.
/// </summary>
[ComImport]
[Guid("0AB5A3D0-E5B6-11D0-ABF5-00A0C90FFFC0")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IFileSystemDispatch
{
    [DispId(ScriptingDispIds.BuildPath)]
    string? BuildPath(string? path, string? name);
}

/// <summary>
/// The custom marshalers of the runtime, which TlbImp refers to by name.
/// </summary>
internal static class Marshalers
{
    /// <summary>The marshaler that converts <c>IEnumVARIANT</c> to <see cref="IEnumerator"/>.</summary>
    public const string Enumerator = "System.Runtime.InteropServices.CustomMarshalers.EnumeratorToEnumVariantMarshaler";
}

/// <summary>
/// The dispatch identifiers of the members that are invoked through <c>IDispatch::Invoke</c>, which the generated code and
/// TlbImp take from the type library. The benchmarks check them against those that the objects return.
/// </summary>
internal static class ScriptingDispIds
{
    public const int Item = 0;
    public const int Count = 2;
    public const int Exists = 3;
    public const int NewEnum = -4;
    public const int BuildPath = 10000;
}
