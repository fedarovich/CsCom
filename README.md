# CsCom

CsCom is a command line tool that produces COM interop code from type libraries, similar to TlbImp.
Unlike TlbImp, it does not rely on the built-in COM interop of .NET: it generates C# code that uses the
[source-generated COM interop](https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation)
added in .NET 8, so the interop assemblies can be trimmed and used with Native AOT.

## Requirements

- Windows: type libraries are read with the OLE Automation APIs.
- .NET 10 SDK or later. The generated projects target .NET 10 or later.

## Usage

```powershell
# Generate a C# project with the interop types
cscom new project C:\Windows\System32\scrrun.dll -o Interop.Scripting

# Regenerate the C# files of a project created with 'new project' (from the type library recorded in cscom.json)
cscom update project -o Interop.Scripting

# Build a ready-to-use interop assembly
cscom new assembly C:\Windows\System32\scrrun.dll -o bin

# List the types and members of a type library
cscom list Scripting.Dictionary --type ^IDictionary$
```

The type library (`TlbPath`) can be:
- **A file:** a `.tlb` file, or a `.dll`, `.ocx` or `.exe` file with an embedded type library. A resource index can be appended,
  e.g. `foo.dll\2`.
- **A registered LIBID:** `{00020813-0000-0000-C000-000000000046}`, optionally with the version as in the registry
  (`{00020813-0000-0000-C000-000000000046}:1.9`). The highest registered version is used by default.
- **The ProgID of a registered class,** e.g. `Excel.Application` or `Scripting.Dictionary`, whose type library is used.

Registered type libraries are found in the registry, so Office type libraries don't have to be looked up inside the Office
installation. `cscom.json` records the LIBID or the ProgID rather than the path, and `update project` looks it up again, e.g.
after an Office update.

| Option | Description |
| --- | --- |
| `-o`, `--output <PATH>` | The output directory: `./<Name>` for `new project`, the current directory for `new assembly`, and the project directory for `update project`. |
| `-n`, `--name <NAME>` | The project and assembly name. `Interop.<TlbName>` by default. |
| `--ns`, `--namespace <NAMESPACE>` | The namespace of the generated types. `Interop.<TlbName>` by default. |
| `-f`, `--framework <TFM>` | The target framework; can be specified multiple times. `net10.0-windows` by default. |
| `-r`, `--runtime <RID>` | The runtime identifier; can be specified multiple times. `AnyCPU` (no runtime identifier) by default. |
| `-c`, `--csharp-version <VERSION>` | The C# version. C# 14 for .NET 10 and C# 15 for .NET 11 by default; only later versions can be specified. |
| `--single-file` | Generates all types into a single C# file instead of a file per type. |
| `--support-code <package\|public\|internal>` | Where the support code comes from, see below. `package` by default. |
| `--date-marshalling`, `--currency-marshalling`, `--bool-marshalling` `<bcl\|none\|wrapper>` | How `DATE`, `CURRENCY` and `VARIANT_BOOL` parameters, return values and structure members are represented: `bcl` (default), `none` or `wrapper`, see [Marshalling options](#marshalling-options). |
| `--struct-properties <always\|nonblittable\|never>` | Whether structure members are public fields or properties: `nonblittable` (default), `always` or `never`, see [Structures](#structures). |
| `--bstr-marshalling <string\|wrapper\|none>` | How `BSTR` parameters and return values are represented: `string` (default), `wrapper` (`ComBstr`) or `none`, see [BSTRs](#bstrs). |
| `--safearray-marshalling <array\|sysarray\|wrapper\|none>` | How typed `SAFEARRAY(T)` parameters and return values are represented: `array` (default, `T[]`), `sysarray`, `wrapper` or `none`, see [SAFEARRAYs](#safearrays). |
| `--event-style <events\|observable\|interface\|none>` | The code generated for the events of coclasses: `events` (default), `observable`, `interface` or `none`, see [Events](#events). |
| `--config <PATH>` | An XML file that adds and configures model and syntax transformers, see [Configuration file](#configuration-file). |
| `--package-source <PATH>` | An additional NuGet source with the CsCom.Runtime package. |
| `--keep-temp` | `new assembly` only: keeps the temporary project used to build the assembly. |
| `-v`, `--verbosity <LEVEL>` | `quiet`, `minimal`, `normal` (default), `detailed` or `diagnostic`. |

`new project` writes `<Name>.csproj`, a `cscom.json` file with the options it was generated with, and the C# files into the
`Generated` folder. `update project` replaces the C# files of the `Generated` folder using the type library and the options recorded
in `cscom.json`; a `TlbPath` replaces the recorded type library. It accepts `--namespace`, `--csharp-version`, `--single-file [true|false]`, the marshalling options, `--event-style`, `--struct-properties` and `--config`
(an empty value removes the file) to change them. Code outside of the `Generated` folder, e.g. partial classes, is not touched.

`new assembly` generates the project in a temporary directory, builds it with `dotnet build` once per runtime identifier and copies
the assemblies, PDBs and XML documentation (and `CsCom.Runtime.dll`) to the output directory. With a single target framework and at
most one runtime identifier, the files are copied directly into the output directory; otherwise into `<TFM>\<RID>` subfolders.

### Listing a type library

`cscom list <TlbPath>` writes the types and members of a type library with IDL-like signatures, using the names that the selectors
of the [configuration file](#configuration-file) match. `--type <REGEX>` (`-t`) selects the types to list, and `--config <PATH>`
applies the model transformers of a configuration file first, e.g. to check renames.

```
> cscom list Scripting.Dictionary --type ^IDictionary$
interface IDictionary : IDispatch (dual)
    propputref Item([in] VARIANT* Key, [in] VARIANT* pRetItem)
    propput Item([in] VARIANT* Key, [in] VARIANT* pRetItem)
    propget Item([in] VARIANT* Key, [out, retval] VARIANT* pRetItem)
    method Add([in] VARIANT* Key, [in] VARIANT* Item)
    propget Count([out, retval] long* pCount)
    ...
```

## Configuration file

`--config` takes an XML file that adds transformers to the [pipeline](#architecture). Each element of the `ModelTransformers` and
`SyntaxTransformers` sections adds the transformer with that name, configured by its attributes and child elements:

```xml
<CsCom>
  <ModelTransformers>
    <RenameType Type="^_(.*)$" NewName="$1" />
    <RenameMember Type="^IShape$" Member="^Get(.*)$" NewName="Fetch$1" />
    <SetMarshalling Type="^IArrays$" Member="^Matrix$" SafeArrays="wrapper" />
    <RemoveEnumPrefix />
  </ModelTransformers>
</CsCom>
```

- **Order:** the transformers run in the order of their elements. The model transformers run after the aliases are resolved and
  before the names are sanitized, so they see the resolved aliases and the names they produce are made valid and unique.
- **Validation:** unknown transformers, attributes and child elements are reported with their location, e.g.
  `cscom.xml(3,6): Unknown model transformer <Rename>.` Nothing is written when the file is not valid.
- **`update project`:** `new project` records the file in `cscom.json`, relative to the project directory, and `update project` applies
  it again. `--config` replaces it, and `--config ""` removes it.
- **Custom transformers:** they are registered through the API with `InteropPipeline.Registry.AddModelTransformer(name, element => ...)`
  and `AddSyntaxTransformer`, whose factories read their element with `ConfigurationElement`.
- **Schema and starter file:** `cscom new config [-o <dir>]` writes `cscom.xsd`, the schema of the configuration files, and a
  `cscom.xml` with commented examples of every transformer, unless it already exists (the schema is always written again). The
  starter file refers to the schema with `xsi:noNamespaceSchemaLocation`, which gives IntelliSense in Visual Studio, VS Code and
  other XML editors.

### Selectors

The selectors match the current names: the type library names, unless an earlier transformer renamed them.
[`cscom list`](#listing-a-type-library) shows these names.
- **Regular expressions:** `Type`, `Member` and `Parameter` are .NET regular expressions. Like `Regex.IsMatch`, an expression
  matches anywhere in the name, so `^IShape$` is needed to match only `IShape`. The matching is case-sensitive; `(?i)` makes it
  case-insensitive.
- **Exact names:** `TypeName`, `MemberName` and `ParameterName` match a whole name exactly. They cannot be combined with the
  regular expression of the same selector.
- **Kinds:** `Kind` restricts the types to a list of kinds separated by `|` or `,`: `enum`, `struct`, `union`, `interface`,
  `dispinterface`, `coclass` and `module` (the names of `cscom list`), e.g. `Kind="interface|dispinterface"`.
- **Omitted selectors:** an omitted type, member or parameter selector matches everything, where the transformer allows it.
- **New names:** with a regular expression, `NewName` replaces the matches of the last selector, like `Regex.Replace`, so it can use
  substitutions such as `$1`: `Type="Shape" NewName="Figure"` renames `IShapeEvents` to `IFigureEvents`. With an exact name,
  `NewName` is the new name as is. Invalid characters become `_`, and names that clash get a `_` suffix.
- **No match:** a transformer whose selectors match nothing logs a warning, e.g. `RenameType: no type matches Type='^IShap$'`, so
  the same file keeps working with other versions of the type library.

### Model transformers

The type selector (`Type`/`TypeName`) can be combined with `Kind`, except for the transformers that only apply to enums.

| Element | Attributes | Effect |
| --- | --- | --- |
| `RenameType` | `Type`, `NewName` | Renames the matching types. |
| `RenameMember` | `Type` (optional), `Member`, `Parameter` (optional), `NewName` | Renames the matching members: methods and properties of interfaces (the accessors of a property together), fields of structures, members of enums, and functions and constants of modules. With `Parameter`, renames the matching parameters of the matching methods instead. |
| `SetMarshalling` | `Type`, `Member` and `Parameter` (all optional); at least one of `Dates`, `Currencies`, `Booleans` (`bcl\|none\|wrapper`), `Bstrs` (`string\|wrapper\|none`) and `SafeArrays` (`array\|sysarray\|wrapper\|none`) | Changes the [marshalling options](#marshalling-options) of the parameters and return values of the matching methods, and of the matching dispinterface properties and structure fields. With `Parameter`, only of the matching parameters, including `[retval]` parameters. Later elements override the values set by earlier ones. |
| `RemoveEnumPrefix` | `Type` (optional) | Removes the prefix shared by the members of the matching enums, or of all enums. |
| `ExcludeType` | `Type` and/or `Kind` | Removes the matching types. References to interfaces, dispinterfaces and coclasses become `IDispatch*` (if they are dispatchable) or `IUnknown*`, references to enums `long`, and pointers to structures `void*`. Structures used by value and base interfaces of kept interfaces cannot be removed. |

```xml
<RenameMember Type="^IShape$" Member="^Move$" Parameter="^d(.)$" NewName="delta$1" />    <!-- dx, dy -> deltax, deltay -->
<SetMarshalling Type="^IStrings$" Member="^Echo$" Parameter="^Value$" Bstrs="none" />   <!-- Echo(nint Value) -->
<SetMarshalling Type="^Record$" Member="^Date$" Dates="wrapper" />                       <!-- a ComDate field -->
```

`RemoveEnumPrefix` removes the longest common prefix that ends at a word boundary, after an underscore or before an upper case letter
that follows a lower case letter or a digit: `xlArea`/`xlBar` become `Area`/`Bar` and `SHAPE_CIRCLE`/`SHAPE_SQUARE` become
`CIRCLE`/`SQUARE`. Enums with a single member, or whose members would not start with a letter (`Size16`/`Size32`), are not changed.

### Syntax transformers

The syntax transformers run after the code is generated, so their selectors match the final names, after the model transformers.

| Element | Attributes | Effect |
| --- | --- | --- |
| `AddFlagsAttribute` | `Type` | Adds `[System.Flags]` to the matching enums, which type libraries cannot mark as bit fields. Enums that already have it are not changed. |
| `AddAttribute` | `Type` and/or `Kind`, `Member` (optional), `Role` (optional), `Attribute` | Adds a C# attribute (without brackets) to the declarations generated for the matching types or members. `Role` selects which declaration: `Declaration` (default), `Extensions` (the extensions class of an interface), `EventHandler`, `EventSink`, `EventDelegate`, `EventArgs`, `Event`, `Property`, `BackingField`, `InlineArray` or `Enumerator`. Declarations that already have the attribute are not changed. |

```xml
<SyntaxTransformers>
  <AddFlagsAttribute Type="^(ShapeFlags|Options)$" />
  <AddAttribute TypeName="ICircle" Attribute='System.Obsolete("Use IShape")' />
  <AddAttribute TypeName="IShape" Role="Extensions" Attribute="System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)" />
</SyntaxTransformers>
```

## Generated code

```csharp
IDictionary dictionary = Dictionary.Create();       // coclass: CoCreateInstance + ComWrappers
dictionary.Add("a", 1);                             // VARIANT parameters are object?
dictionary.CompareMode = CompareMethod.TextCompare; // C# 14 extension property over set_CompareMode
Console.WriteLine(dictionary.Count);                // C# 14 extension property over get_Count
foreach (object? key in dictionary)                 // _NewEnum via IEnumVARIANT
{
}
```

- **Interfaces and dual interfaces** become `[GeneratedComInterface]` interfaces. HRESULTs are translated to exceptions and
  `[retval]` parameters to return values; methods that do not return an HRESULT get `[PreserveSig]`. Dual interfaces derive from
  `IDispatch`, so that the vtable slots match.
- **Properties** cannot be declared by source-generated COM interfaces, so they are generated as `get_X`, `set_X` (`propput`) and
  `putref_X` (`propputref`) methods. A static `<Interface>Extensions` class adds C# 14 extension properties for them and a
  `GetEnumerator` extension for collections. With C# 15, the default parameterized property (`DISPID_VALUE`, usually `Item`)
  becomes an extension indexer.
- **Dispinterfaces** can only be called through `IDispatch::Invoke`, so they become late-bound wrapper classes with methods,
  properties and indexers that invoke the members by their DISPIDs.
- **Coclasses** become static classes with the `Clsid` and a `Create()` method that returns the default interface.
  Coclasses whose default interface supports `IDispatch` also get `CreateDynamic()`, see [Late binding with `dynamic`](#late-binding-with-dynamic).
  Their event (`[source]`) interfaces get a handler interface and a sink class, see [Events](#events).
- **Enums** become enums and **structs and unions** become structs with the layout of the COM structures, whose `DATE`,
  `CURRENCY` and `VARIANT_BOOL` members are converted by properties, see [Structures](#structures).
- **Modules** become static classes with `[LibraryImport]` functions and constants.
- **Aliases** are replaced by the aliased types.
- **Types imported from other type libraries** are generated into `<Namespace>.<LibraryName>`. The well-known OLE Automation types
  `IUnknown`, `IDispatch`, `IEnumVARIANT` and `GUID` are mapped to `object`, `IDispatch`, `IEnumVARIANT` and `Guid`.

| Type library | C# |
| --- | --- |
| `BSTR` | `string?` (configurable, see [BSTRs](#bstrs)) |
| `LPSTR`, `LPWSTR` | `string?` with `MarshalAs` |
| `VARIANT_BOOL` | `bool` (configurable, see below) |
| `VARIANT` | `object?` (`VariantMarshaller`; `null` is an omitted argument for optional parameters) |
| `IUnknown*` | `object?` |
| `IDispatch*` | `IDispatch?` |
| `DECIMAL` | `decimal` |
| `CURRENCY` | `decimal` (`CurrencyMarshaller`; configurable, see below) |
| `DATE` | `DateTime` (`DateMarshaller`; configurable, see below) |
| `SAFEARRAY(T)` | `T[]?` (`SafeArrayMarshaller<T>`; configurable, see [SAFEARRAYs](#safearrays)) |
| `[out] T*`, `[in, out] T*` | `out T`, `ref T` |

### SAFEARRAYs

`SAFEARRAY`s of `VARIANT`, `BSTR`, `VARIANT_BOOL`, integers, floating-point numbers, `DECIMAL`, `DATE`, enums, `IUnknown*`,
`IDispatch*` and interfaces are supported; `SAFEARRAY`s of structs, `CURRENCY`, `SCODE` and dispinterfaces are always passed as
raw `SAFEARRAY*` (`nint`).

Arrays inside `VARIANT`s (`object` parameters, `dynamic` and late-bound wrappers) can have up to three dimensions: Excel's
`Range.Value2` returns an `object[,]`, and `T[,]` and `T[,,]` can be passed. The elements keep their indices: the element
`(i, j)` of the `SAFEARRAY` is `[i, j]` of the .NET array, although `SAFEARRAY`s store the first index and .NET arrays the last index
fastest. The .NET arrays are **zero-based**, because Native AOT does not support other lower bounds, so the 1-based arrays of
Visual Basic and Excel start at `[0, 0]`.

Type libraries do not record the rank of `SAFEARRAY(T)` parameters, so their representation is selected by `--safearray-marshalling`:

| Value | `SAFEARRAY(T)` | Ranks |
| --- | --- | --- |
| `array` (default) | `T[]?` (`SafeArrayMarshaller<T>`) | 1 |
| `sysarray` | `System.Array?` (`SafeArrayMarshaller`), e.g. a `double[,]` | 1–3 |
| `wrapper` | `ComSafeArray<T>?` | any |
| `none` | the `SAFEARRAY*` (`nint`) | any |

- **`sysarray` mode:** the element type of the arrays passed to COM methods determines the element type of the `SAFEARRAY`, so pass
  e.g. a `string[,]` to a `SAFEARRAY(BSTR)`.
- **Other places:** `SAFEARRAY(IUnknown*)` stays `object?[]` (`UnknownSafeArrayMarshaller`) in the `array` and `sysarray` modes.
  Late-bound wrappers use `System.Array?` in all modes other than `array`.

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

### BSTRs

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

`ComBstr` is disposable and follows the same ownership rules as [`ComSafeArray<T>`](#safearrays): passed strings stay owned by the
caller, returned strings are owned by the receiver, a string replaced by the callee of an `[in, out]` parameter is detached, and
COM objects implemented in .NET receive views that are only valid during the call. `ComBstr.FromPointer` wraps a `BSTR`, e.g. a
structure field, and `Detach` gives the pointer up.

- **Default values:** a `ComBstr` or `nint` parameter cannot have a string as its C# default value, so `BSTR` parameters with
  a `defaultvalue` become required, together with the optional parameters before them.
- **Events:** dual event interfaces can also be invoked through `IDispatch::Invoke`, which passes `BSTR`s in `VARIANT`s. In the
  `wrapper` and `none` modes, their events with `BSTR` parameters are only received through the vtable.
- **Other places:** structure fields stay `nint`, and late-bound wrappers, dispinterface events, `VARIANT`s, the elements of
  `SAFEARRAY`s and constants use `string`. A `ComBstr` passed to a `VARIANT` parameter is passed as a copy.

### Marshalling options

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
value, so optional parameters whose default is `true` or non-zero become required. The options do not affect late-bound
dispinterface wrappers, constants and the elements of `SAFEARRAY`s, which use the .NET types.

All marshalling options, including `--bstr-marshalling` and `--safearray-marshalling`, can be changed for individual methods,
parameters, dispinterface properties and structure fields with the `SetMarshalling` element of the
[configuration file](#model-transformers).

### Structures

Structures keep the layout of the COM structures, so they can be passed by pointer and by reference without marshalling. Their
fields have the COM types: `BSTR` and interface pointers are `nint`, `VARIANT`s are `ComVariant`, `SAFEARRAY`s are `nint`,
fixed-size arrays are inline arrays of the COM element types, and `DATE`, `CURRENCY` and `VARIANT_BOOL` members are stored as
`double`, `long` and `short` or as the wrappers. `--struct-properties` selects how the members are exposed:

| Value | Members with the COM type or a wrapper | `bcl` members (`DateTime`, `decimal`, `bool`) |
| --- | --- | --- |
| `nonblittable` (default) | public fields | properties that convert private fields |
| `always` | properties over private fields | properties that convert private fields |
| `never` | public fields | public fields of the COM types |

```csharp
// --struct-properties nonblittable (default)
public nint Name;                       // BSTR
private short _flag;
public bool Flag { readonly get => _flag != 0; set => _flag = value ? (short)-1 : (short)0; }
```

With `always`, the properties return copies of nested structures and inline arrays, so `rect.TopLeft.X = 5` does not compile;
assign a modified copy (`var topLeft = rect.TopLeft; topLeft.X = 5; rect.TopLeft = topLeft;`) or use `nonblittable` or `never`,
which keep them as fields.

### Late binding with `dynamic`

The COM objects created by source-generated COM interop do not support `dynamic` by themselves. CsCom.Runtime provides
`DynamicDispatchObject`, which resolves members by name with `IDispatch::GetIDsOfNames` and invokes them with `IDispatch::Invoke`:

```csharp
dynamic fso = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject"); // by ProgID or CLSID, no interop types needed
dynamic dictionary = Dictionary.CreateDynamic();   // generated for coclasses that support IDispatch
dynamic shape = typedShape.AsDynamic();            // any IDispatch-based interface or late-bound wrapper
dynamic tag = ComDynamic.ToDynamic(typedShape.Tag); // an object returned in a VARIANT

dictionary.Add("a", 1);                            // methods
int count = dictionary.Count;                      // properties
object item = dictionary.Item("a");                // parameterized properties are invoked like methods
dictionary["b"] = 2;                               // indexers use the default member (DISPID_VALUE)
string path = fso.BuildPath(Path: @"C:\a", Name: "b"); // named arguments
string drive = fso.GetFolder(@"C:\Windows").Drive.DriveLetter; // returned COM objects are dynamic too
foreach (var key in dictionary) { }                // _NewEnum
IDispatch dispatch = dictionary;                   // conversions to IDispatch and to the implemented COM interfaces
```

COM objects returned by dynamic calls are dynamic as well, so calls can be chained. Assigning a COM object to a property or an
indexer uses `PROPERTYPUTREF` when the property supports it, like VB's `Set`. Unknown members throw `RuntimeBinderException` and
errors raised by the COM object throw `COMException`.

The C# runtime binder requires dynamic code, so `dynamic` cannot be used with Native AOT: the entry points are annotated with
`[RequiresDynamicCode]`, and the typed interop stays trimming and AOT compatible. Arguments are passed by value, so `ref` and `out`
arguments are not updated, and parameterized properties other than the default one cannot be set.

### Events

COM objects raise events by calling the methods of event interfaces, the `[source]` interfaces of their coclasses, on the sinks
connected to their connection points. For each event interface, CsCom generates `I<Interface>Handler` (without the `I` prefix for
names that already start with `I` or `_`), an interface with a method per event that does nothing by default, and `<Interface>Sink`,
which connects to the events. What else is generated
depends on `--event-style`:

```csharp
// events (default): .NET events, with TlbImp-style delegates such as AppEvents_WorkbookBeforeCloseEventHandler
using var events = new AppEventsSink(app);          // connects (IConnectionPoint::Advise); Dispose disconnects
events.NewWorkbook += workbook => Console.WriteLine(workbook?.Name);
events.WorkbookBeforeClose += (Workbook? workbook, ref bool cancel) => cancel = true;

// observable: an IObservable<T> per event, with an args class whose ref and out parameters and Result can be set
using var events = new AppEventsSink(app);
using var subscription = events.WorkbookBeforeClose.Subscribe(e => e.Cancel = true);

// events, observable and interface: implement only the methods of the handler interface you need
sealed class Handler : IAppEventsHandler
{
    public void WorkbookBeforeClose(Workbook? Wb, ref bool Cancel) => Cancel = true;
}

using var connection = AppEventsSink.Connect(app, new Handler());
```

With `interface`, `<Interface>Sink` is a static class with only `Connect`, and `none` generates no event code. The observables
are hot: events are not buffered, and disposing the sink completes them. They are plain `IObservable<T>`s, so they work with
Reactive Extensions without depending on it.

All kinds of event interfaces are supported:

- **Dispinterfaces**, the most common ones (Office, VB6 and ATL components), are received by `DispatchEventSink` of the support
  code, which implements `IDispatch` and answers `QueryInterface` for the dispinterface. The arguments of `IDispatch::Invoke` are
  converted to the late-bound types, as in the late-bound wrappers, and `ref` and `out` arguments and results are passed back.
- **Vtable interfaces** are implemented by a generated `[GeneratedComClass]` sink with the signatures of the generated interface.
- **Dual interfaces** are implemented in the same way, and the sink also implements `IDispatch::Invoke`, which event sources often use.

The source can be any object returned by the generated code: a COM interface, a late-bound wrapper or a `dynamic` object.
`EventConnection.Advise` and `EventConnection.AdviseDispatch` of the support code connect hand-written sinks.

Events are delivered on the threads chosen by COM: a single-threaded apartment (STA) object, such as an Office application,
delivers them through the message loop of the thread that connected the sink. Dispose the sink on that thread, because
`IConnectionPoint::Unadvise` must be called from the same apartment; there is no finalizer that disconnects it. An exception thrown
by a handler is returned to the source as `DISP_E_EXCEPTION` with the error information (or as the `HRESULT` for vtable calls),
and most sources ignore it.

### Support code

The generated code depends on a few support types: `IDispatch`, `IEnumVARIANT`, `ComActivator`, the `VARIANT`, `SAFEARRAY`, `BSTR`, `DATE` and `CURRENCY` marshallers, the
base class of the late-bound wrappers, the event sinks and the `dynamic` support. With `--support-code package`, the generated project references the **CsCom.Runtime**
NuGet package. With `public` or `internal`, the support code is emitted into the project, in the `<Namespace>.ComSupport` namespace.
In the `internal` mode all generated types are internal, because a public interface cannot derive from an internal `IDispatch`.

The generated projects disable runtime marshalling, which source-generated COM interop requires to marshal `VARIANT`s and `DECIMAL`s.

### Limitations

- Events cannot be handled through `dynamic`, and they are connected through the sink classes rather than through events of the
  COM interfaces as with TlbImp (`app.WorkbookOpen += ...`), because C# has no extension events.
- Structures keep the layout of the COM structures, so `BSTR`, `VARIANT`, `SAFEARRAY` and interface pointer members are raw values;
  only `DATE`, `CURRENCY` and `VARIANT_BOOL` members are converted.
- `SAFEARRAY`s are converted to zero-based .NET arrays of up to three dimensions; only `ComSafeArray<T>` keeps their bounds and
  supports more dimensions.
- Types imported from other type libraries are always generated into the project; referencing other interop assemblies is not supported yet.
- CsCom.Runtime only targets `net10.0` for now. Source-generated COM expects a base interface from another assembly (`IDispatch`)
  to be compiled for the same target framework, so a `net11.0` build of CsCom.Runtime should be added together with .NET 11 support.

## Architecture

The tool runs the type library through a pipeline (`CsCom.Core`):

1. `TypeLibraryReader` reads the type library with source-generated `ITypeLib`/`ITypeInfo` bindings into the model (`CsCom.Model`).
2. The model transformers (`IModelTransformer`) change the model. The built-in ones resolve aliases first and sanitize names last;
   those of `InteropPipeline.ModelTransformers` and of the [configuration file](#configuration-file) run in between.
3. `CSharpEmitter` generates the Roslyn syntax trees.
4. The syntax transformers (`ISyntaxTransformer`) change the syntax trees. The generated declarations carry annotations that
   associate them with the model elements, which `TransformationContext.Annotations` resolves: `GetElement(node)` gives the
   element of a node and its role (`SyntaxRole.Declaration`, `Extensions`, `EventSink`, …), and `GetNodes(root, element, role)`
   finds the nodes generated for an element.
5. `ProjectGenerator` and `AssemblyGenerator` write the project or build the assembly.

| Project | Description |
| --- | --- |
| `src/CsCom` | The command line tool (`cscom`), packed as a .NET tool together with the CsCom.Runtime package. |
| `src/CsCom.Core` | The pipeline. |
| `src/CsCom.Runtime` | The support code used by the generated code. Its sources are also embedded into CsCom.Core for the `public` and `internal` modes. |
| `src/CsCom.Tests` | MSTest and Verify tests. |

## Development

```powershell
dotnet build src/CsCom.slnx
dotnet test src/CsCom.slnx                                       # all tests
dotnet test src/CsCom.slnx --filter "TestCategory!=Integration"  # without the tests that build generated projects
```

- The projects use central package management: the package versions are in `src/Directory.Packages.props`.
- Building CsCom.Runtime creates its package in `artifacts/packages`, which the tool bundles and the integration tests use.
  The integration tests restore it into a temporary global packages folder. When running the tool from source, clear
  `%USERPROFILE%\.nuget\packages\cscom.runtime` after changing CsCom.Runtime without changing the version.
- `src/CsCom.Tests/TestAssets/CsComTest.tlb` is built from `CsComTest.idl` with `build-tlb.ps1`, which requires Visual Studio
  with the C++ tools. The `.tlb` is committed.
- The snapshots of the generated code are in `src/CsCom.Tests/Snapshots`. Verify.MSTest is pinned to 32.0.0, the last version
  without the sponsorship check.
