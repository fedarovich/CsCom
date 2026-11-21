# Generated code

An overview of the C# code that CsCom generates from a type library, for this use of `Scripting.Dictionary`. The other
articles of this section describe the details.

```csharp
IDictionary dictionary = Dictionary.Create();       // coclass: CoCreateInstance + ComWrappers
dictionary.CompareMode = CompareMethod.TextCompare; // C# 14 extension property over set_CompareMode
dictionary.Add("a", 1);                             // VARIANT parameters are object?
Console.WriteLine(dictionary.Count);                // C# 14 extension property over get_Count
foreach (object? key in dictionary)                 // _NewEnum via IEnumVARIANT
{
}
```

- **Interfaces and dual interfaces** become `[GeneratedComInterface]` interfaces. HRESULTs are translated to exceptions and
  `[retval]` parameters to return values, except a `[retval] void*`, which stays a parameter; methods that do not return an
  HRESULT get `[PreserveSig]`. Dual interfaces derive from `IDispatch`, and interfaces derived from `IEnumVARIANT` from
  `IEnumVARIANT`, so that the vtable slots match. An interface without a GUID cannot be generated: the pointers to it are
  `IUnknown` or `IDispatch` pointers, and the interfaces that derive from it get placeholders for its vtable slots.
- **Names:** invalid characters become `_`, and a name that clashes gets a trailing `_`: a member named like another member or
  like its type, and a generated type (`<Interface>Extensions`, the types of the [events](events.md)) named like a type of the
  library.
- **Pointer parameters** are `out` (`[out]`), `ref` (`[in, out]`) or, for `[in]` pointers to `VARIANT`s, `SAFEARRAY`s,
  structures and GUIDs, `in` parameters. A pointer without a direction, as old type libraries have them, is a `ref` parameter.
- **Properties** cannot be declared by source-generated COM interfaces, so they are generated as `get_X`, `set_X` (`propput`) and
  `putref_X` (`propputref`) methods. A static `<Interface>Extensions` class adds C# 14 extension properties for them and a
  `GetEnumerator` extension for collections. With C# 15, the default parameterized property (`DISPID_VALUE`, usually `Item`)
  becomes an extension indexer. The extensions of a derived interface also use the accessors of its base interfaces, e.g. the
  getter of a read-only property of `IMyInterface` and the setter that `IMyInterface2` adds to it. The other parameterized
  properties can get indexer syntax too, see [Named indexers](named-indexers.md).
- **Dispinterfaces** can only be called through `IDispatch::Invoke`, so they become late-bound wrapper classes with methods,
  properties and indexers that invoke the members by their DISPIDs.
- **Coclasses** become static classes with the `Clsid` and a `Create()` method that returns the default interface;
  `Create(unique: true)` returns it in an object of its own, which can be released at once, see [Interface pointers](interface-pointers.md).
  With `--dynamic`, coclasses whose default interface supports `IDispatch` also get `CreateDynamic()`, see [Late binding with `dynamic`](dynamic.md).
  Their event (`[source]`) interfaces get a handler interface and a sink class, see [Events](events.md).
- **Enums** become enums and **structs and unions** become structs with the layout of the COM structures, whose `DATE`,
  `CURRENCY` and `VARIANT_BOOL` members are converted by properties, see [Structures](structures.md).
- **Modules** become static classes with `[LibraryImport]` functions and constants.
- **Aliases** are replaced by the aliased types.
- **Types imported from other type libraries** are generated into the namespace of their library, `Interop.<Library>` by default,
  see [Several sources](sources.md#several-sources). The well-known OLE Automation types
  `IUnknown`, `IDispatch`, `IEnumVARIANT` and `GUID` are mapped to `object`, `IDispatch`, `IEnumVARIANT` and `Guid`.

| Type library | C# |
| --- | --- |
| `BSTR` | `string?` (configurable, see [BSTRs](bstrs.md)) |
| `LPSTR`, `LPWSTR` | `string?` with `MarshalAs` |
| `VARIANT_BOOL` | `bool` (configurable, see below) |
| `VARIANT` | `object?` (`VariantMarshaller`; `null` is an omitted argument for optional parameters) |
| `IUnknown*` | `object?` |
| `IDispatch*` | `IDispatch?` |
| `DECIMAL` | `decimal` |
| `CURRENCY` | `decimal` (`CurrencyMarshaller`; configurable, see below) |
| `DATE` | `DateTime` (`DateMarshaller`; configurable, see below) |
| `SAFEARRAY(T)` | `T[]?` (`SafeArrayMarshaller<T>`; configurable, see [SAFEARRAYs](safearrays.md)) |
| `[out] T*`, `[in, out] T*` | `out T`, `ref T` |
