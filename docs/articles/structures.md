# Structures

Structures keep the layout of the COM structures, so they can be passed by pointer and by reference without marshalling. Their
fields have the COM types: `BSTR`s are `ComBstrValue`s, interface pointers are `nint`, `VARIANT`s are `ComVariant`, `SAFEARRAY`s
are `nint`, fixed-size arrays are inline arrays of the COM element types, and `DATE`, `CURRENCY` and `VARIANT_BOOL` members are
stored as `double`, `long` and `short` or as the wrappers. `--struct-properties` selects how the members are exposed:

| Value | Members with the COM type or a wrapper | `bcl` members (`DateTime`, `decimal`, `bool`) |
| --- | --- | --- |
| `auto` (default) | public fields | properties that convert private fields |
| `never` | public fields | public fields of the COM types |

```csharp
// --struct-properties auto (default)
public ComBstrValue Name;               // BSTR
private short _flag;
public bool Flag { readonly get => _flag != 0; set => _flag = value ? (short)-1 : (short)0; }
```

Nested structures and inline arrays are always fields, so their members can be changed in place (`rect.TopLeft.X = 5`).

`ComBstrValue` is a pointer-sized view of a `BSTR` field: `IsNull`, `Length`, `ToString()` and the implicit conversion to `string?`
read it, and `AsComBstr()` gives a `ComBstr` that does not own it. As in C, the owner of the structure owns its strings, so
`ComBstrValue.Allocate("text")` allocates a string to store in a structure, and `Free()` releases a string the structure owns:

```csharp
var record = new Record { Name = ComBstrValue.Allocate("Ann") };
try { server.Update(ref record); string? name = record.Name; }  // a method that takes a Record*
finally { record.Name.Free(); }
```
