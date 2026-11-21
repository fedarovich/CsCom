# SAFEARRAYs

`SAFEARRAY`s of `VARIANT`, `BSTR`, `VARIANT_BOOL`, integers, floating-point numbers, `DECIMAL`, `DATE`, enums, `IUnknown*`,
`IDispatch*` and interfaces are supported; `SAFEARRAY`s of structs, `CURRENCY`, `SCODE` and dispinterfaces are always passed as
raw `SAFEARRAY*` (`nint`).

Arrays inside `VARIANT`s (`object` parameters, `dynamic` and late-bound wrappers) can have up to three dimensions: Excel's
`Range.Value2` returns an `object[,]`, and `T[,]` and `T[,,]` can be passed. The elements keep their indices: the element
`(i, j)` of the `SAFEARRAY` is `[i, j]` of the .NET array, although `SAFEARRAY`s store the first index and .NET arrays the last index
fastest. The .NET arrays are **zero-based**, because Native AOT does not support other lower bounds, so the 1-based arrays of
Visual Basic and Excel start at `[0, 0]`.

Type libraries do not record the rank of `SAFEARRAY(T)` parameters, so their representation is selected by `--safearray-marshalling`
and, for [known ranks](#known-ranks), by the configuration file:

| Value | `SAFEARRAY(T)` | Ranks |
| --- | --- | --- |
| `array` (default) | `T[]?` (`SafeArrayMarshaller<T>`), or `T[,]?`/`T[,,]?` for known ranks | 1, or the known rank |
| `sysarray` | `System.Array?` (`SafeArrayMarshaller`), e.g. a `double[,]` | 1–3 |
| `wrapper` | `ComSafeArray<T>?` | any |
| `none` | the `SAFEARRAY*` (`nint`) | any |

- **`sysarray` mode:** the element type of the arrays passed to COM methods determines the element type of the `SAFEARRAY`, so pass
  e.g. a `string[,]` to a `SAFEARRAY(BSTR)`.
- **Other places:** `SAFEARRAY(IUnknown*)` stays `object?[]` (`UnknownSafeArrayMarshaller`) in the `array` and `sysarray` modes.
  Late-bound wrappers use `System.Array?` in all modes other than `array`.
- **Late-bound wrappers and dispinterface events** in the `array` mode use `T[]?` too, enums included (`Color[]?`), and convert
  the elements, since servers often return other element types, e.g. a `SAFEARRAY` of `VARIANT`s for a `SAFEARRAY(BSTR)`.
- <a id="known-ranks"></a>**Known ranks:** when the rank of a parameter is known, the `SetSafeArrayRank` element of the [configuration file](configuration.md#model-transformers)
  gives `T[,]` or `T[,,]` instead of `T[]` in the `array` mode, also in late-bound wrappers and events:

  ```xml
  <SetSafeArrayRank TypeName="Range" MemberName="Value2" Rank="2" />   <!-- object?[,]? instead of object?[]? -->
  ```

  The source generator of COM interop only supports generic marshallers of one-dimensional arrays, so the project gets a
  marshaller per element type and rank in `CsComSafeArrayMarshallers.cs`. A `SAFEARRAY` of another rank throws a
  `SafeArrayRankMismatchException`.

`ComSafeArray<T>` wraps the `SAFEARRAY` and converts the elements to and from `T` on each access. Unlike the .NET arrays, it keeps
the **native bounds**:

```csharp
// --safearray-marshalling wrapper
using ComSafeArray<double>? matrix = engine.GetMatrix("A");
double first = matrix![1, 1];           // indexers with 1, 2 and 3 indices; GetValue/SetValue for any rank
double[,] copy = matrix.ToArray2D();    // zero-based copy

using var input = ComSafeArray<double>.Create(new double[3, 3]);  // or Create(VarEnum.VT_R8, lengths, lowerBounds)
engine.PutMatrix("B", input);
```

It is disposable and follows the COM ownership rules:
- **Passed arrays:** they stay owned by the caller.
- **Returned arrays:** they are owned by the receiver; dispose them.
- **`[in, out]` parameters:** an array replaced by the callee is detached from its old wrapper.
- **COM objects implemented in .NET:**
  - The arrays they receive are views that are only valid during the call.
  - An owned array they return is detached and transferred to the caller; a returned view is copied.

A finalizer destroys the arrays that are not disposed. `ComSafeArray<T>.FromPointer` wraps a `SAFEARRAY*`, e.g. a structure
field or a parameter in the `none` mode, and `Detach` gives the pointer up.

`AsSpan()` gives the elements of a one-dimensional array in place, without converting them, so `T` must have their layout:
the numeric type of the elements or an enum based on it, `decimal`, `ComBool`, `ComDate` or `ComCurrency`. The span is valid
until the wrapper is disposed.
