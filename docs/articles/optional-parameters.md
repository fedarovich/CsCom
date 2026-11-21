# Optional parameters

C# requires the optional parameters to follow the required ones and their default values to be constants, so a parameter whose
type library default has no C# constant makes it and the parameters before it required: `DATE` defaults (`DateTime` has no
constants), `VARIANT` defaults of interface methods (an `object` parameter can only default to `null`, and nothing applies the
default of an omitted argument in a vtable call), and `VARIANT_BOOL`, `CURRENCY`, `DATE` and `BSTR` defaults with the `wrapper`
and `none` options.
`--overloads-for-default-values` generates overloads that omit them and pass their default values:

```csharp
// --bool-marshalling wrapper --bstr-marshalling wrapper --overloads-for-default-values
// HRESULT Move([in] long dx, [in, defaultvalue(0)] long dy, [in, optional] VARIANT scale, [in, defaultvalue("label")] BSTR label,
//              [in, defaultvalue(2)] Color color, [in, defaultvalue(1.5)] double factor, [in, defaultvalue(-1)] VARIANT_BOOL flag);
shape.Move(1);                                    // Move(dx, dy = 0, scale = null): passes ComBstr "label", ..., ComBool.True
shape.Move(1, 2, null, label, Color.Blue);        // Move(dx, dy, scale, label, color = 2, factor = 1.5): passes ComBool.True
shape.Move(1, 2, null, label, Color.Blue, 2, f);  // the method itself
```

- **Where:** the overloads of interface methods are extension methods in the `…Extensions` class, which only take the calls that
  the method itself does not accept; dispinterface wrappers and modules get regular overloads. Property accessors get none.
- **Limit:** each overload ends before one of these parameters, so the overloads never accept the same number of arguments. To
  keep the code small, a method gets at most 4 overloads, for its first 4 such parameters.
- **Temporary strings:** `BSTR` default values are allocated for the call and released afterwards.
- **`[lcid]` parameters** of interface methods default to `0x0400` (`LOCALE_USER_DEFAULT`), so they do not make the optional
  parameters before them required. Properties and indexers pass this value to their accessors; the `[lcid]` parameter is not
  one of their index parameters.
- **Values that do not fit:** a default value outside the range of the type of its parameter is not generated, and the
  parameter is required. An unsigned value with the bits of a 32-bit value keeps its bits, e.g. `0xFFFFFFFF` is `-1`.
- **Property setters:** the value of a `set_X` or `putref_X` method follows its index parameters, so they are required in C#.
  A `null` optional `VARIANT` index is still omitted, as by the getter.
