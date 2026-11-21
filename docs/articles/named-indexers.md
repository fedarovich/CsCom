# Named indexers

COM properties can have parameters, e.g. Excel's `Worksheet.Range(Cell1, [Cell2])` or Scripting's `Dictionary.Key(Key)`. C#
has no named indexers, and calls them as indexers only on `[ComImport]` types, so they are generated as accessor methods
(`get_Range("A1")`, `set_Key("a", "b")`); only the default member is an indexer. `--named-indexers` gives the others indexer
syntax through a proxy, which a member named after the property returns and whose indexer calls the accessor methods:

```csharp
// --named-indexers classproperty: a property returns a sealed proxy class
Range range = sheet.Range["A1"];
dictionary.Key["a"] = "b";

// --named-indexers structmethod: a method returns a readonly record struct proxy
Range range = sheet.Range()["A1"];
dictionary.Key()["a"] = "b";
```

- **`classproperty`:** C# cannot call a setter on a struct that a property returns (CS1612), so the property returns a class.
  Its members are `[MethodImpl(MethodImplOptions.AggressiveInlining)]`, so that the JIT can allocate the proxy on the stack
  instead of the heap. A `structproperty` value can follow when C# allows setter calls on readonly structs that are not
  variables ([csharplang#9174](https://github.com/dotnet/csharplang/issues/9174)).
- **`structmethod`:** the proxy is a struct, which never allocates, but it is returned by a method.
- **The indexer** has the parameters of the getter, including the default values of the optional ones, e.g. `Cell2` above.
- **Which properties:** those of interfaces and dispinterfaces with parameters, except the indexer: the extension indexer of
  the default member with C# 15, or the indexer of a dispinterface wrapper. With C# 14, the default member gets a proxy too
  (`dictionary.Item["a"]`). A property whose name is used by a method is skipped.
- **The proxy types** are nested in the `<Interface>Extensions` class or the dispinterface wrapper, e.g.
  `WorksheetExtensions.RangeIndexer`. Use them right away rather than store them.
