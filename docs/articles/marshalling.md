# Marshalling options

`--date-marshalling`, `--currency-marshalling` and `--bool-marshalling` select how `DATE`, `CURRENCY` and `VARIANT_BOOL` values are
represented in parameters, return values and structure members:

| Option value | `DATE` | `CURRENCY` | `VARIANT_BOOL` |
| --- | --- | --- | --- |
| `bcl` (default) | `DateTime` | `decimal` | `bool` |
| `none` | `double` | `long` | `short` |
| `wrapper` | `ComDate` | `ComCurrency` | `ComBool` |

The .NET types have another layout than the COM types, so structures store `bcl` members as the COM types in private fields and
convert them in properties; with `--struct-properties never` they are public fields of the COM types instead.

`ComDate`, `ComCurrency` and `ComBool` are `readonly record struct`s from CsCom.Runtime that store the COM value in `Value`
(an OLE Automation date, a currency value scaled by 10,000, and `VARIANT_TRUE` = -1 / `VARIANT_FALSE` = 0), so they have the
layout of the COM types, and convert implicitly to and from `DateTime`, `decimal` and `bool`:

```csharp
var record = new Record { Date = DateTime.Today, Money = 12.5m, Flag = true }; // with the wrapper options: ComDate, ComCurrency and ComBool fields
DateTime date = record.Date;
```

`ComBool` compares like a Boolean: any non-zero value equals `ComBool.True`. It also has the `true`/`false` operators and the
Boolean operators `!`, `&`, `|` and `^`, which return `ComBool.True` or `ComBool.False`, so it can be used in conditions and
with `&&` and `||` (`if (record.Flag && !other.Flag)`). Passed to `VARIANT` parameters or `dynamic` calls,
the wrappers become `VT_DATE`, `VT_CY` and `VT_BOOL` values. A wrapper parameter can only have its default value as a C# default
value, so optional parameters whose default is `true` or non-zero become required, unless [overloads](optional-parameters.md)
are generated. The options do not affect late-bound dispinterface wrappers, constants and the elements of `SAFEARRAY`s, which use
the .NET types.

All marshalling options, including `--bstr-marshalling` and `--safearray-marshalling`, can be changed for individual
methods, parameters, dispinterface properties and structure fields with the `SetMarshalling` element of the
[configuration file](configuration.md#model-transformers), which also selects the marshaller of interface pointers.
