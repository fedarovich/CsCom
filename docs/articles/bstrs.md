# BSTRs

`BSTR` parameters and return values are copied to and from `string`s by default. `--bstr-marshalling` selects another
representation, e.g. to read a large string without copying it or to keep the exact length of a `BSTR` that holds binary data:

| Value | `BSTR` |
| --- | --- |
| `string` (default) | `string?` (`BStrStringMarshaller`) |
| `wrapper` | `ComBstr?`, which wraps the `BSTR` without copying it |
| `none` | the `BSTR` pointer (`nint`) |

```csharp
// --bstr-marshalling wrapper
using ComBstr? xml = document.GetXml();
ReadOnlySpan<char> characters = xml!.AsSpan();  // valid until disposed; ToString() copies, AsBytes() gives the bytes

using var name = ComBstr.Create("Shape 1");     // or CreateFromBytes for binary data
shape.Name = name;
```

`ComBstr` is disposable and follows the same ownership rules as [`ComSafeArray<T>`](safearrays.md): passed strings stay owned by the
caller, returned strings are owned by the receiver, a string replaced by the callee of an `[in, out]` parameter is detached, and
COM objects implemented in .NET receive views that are only valid during the call. `ComBstr.FromPointer` wraps a `BSTR`, e.g. a
structure field, and `Detach` gives the pointer up.

- **Default values:** a `ComBstr` or `nint` parameter cannot have a string as its C# default value, so `BSTR` parameters with
  a `defaultvalue` become required, together with the optional parameters before them, unless
  [overloads](optional-parameters.md) are generated.
- **Events:** dual event interfaces can also be invoked through `IDispatch::Invoke`, which passes `BSTR`s in `VARIANT`s. In the
  `wrapper` and `none` modes, their events with `BSTR` parameters are only received through the vtable.
- **Other places:** structure fields are `ComBstrValue`s (see [Structures](structures.md)), or `nint` with `none`, and late-bound
  wrappers, dispinterface events, `VARIANT`s, the elements of `SAFEARRAY`s and constants use `string`. A `ComBstr` passed to a
  `VARIANT` parameter is passed as a copy.
