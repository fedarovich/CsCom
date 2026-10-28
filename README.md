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

# Regenerate the C# files of a project created with 'new project'
cscom update project C:\Windows\System32\scrrun.dll -o Interop.Scripting

# Build a ready-to-use interop assembly
cscom new assembly C:\Windows\System32\scrrun.dll -o bin
```

The type library can be a `.tlb` file or a `.dll`, `.ocx` or `.exe` file with an embedded type library.

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
| `--date-parameter-marshalling`, `--currency-parameter-marshalling`, `--bool-parameter-marshalling` | How `DATE`, `CURRENCY` and `VARIANT_BOOL` parameters and return values are represented: `managed` (default), `com` or `wrapper`, see [Marshalling options](#marshalling-options). |
| `--date-field-marshalling`, `--currency-field-marshalling`, `--bool-field-marshalling` | How `DATE`, `CURRENCY` and `VARIANT_BOOL` structure fields are represented: `com` (default) or `wrapper`. |
| `--event-style <events\|observable\|interface\|none>` | The code generated for the events of coclasses: `events` (default), `observable`, `interface` or `none`, see [Events](#events). |
| `--package-source <PATH>` | An additional NuGet source with the CsCom.Runtime package. |
| `--keep-temp` | `new assembly` only: keeps the temporary project used to build the assembly. |
| `-v`, `--verbosity <LEVEL>` | `quiet`, `minimal`, `normal` (default), `detailed` or `diagnostic`. |

`new project` writes `<Name>.csproj`, a `cscom.json` file with the options it was generated with, and the C# files into the
`Generated` folder. `update project` replaces the C# files of the `Generated` folder using the options recorded in `cscom.json`.
It accepts `--namespace`, `--csharp-version`, `--single-file [true|false]`, the marshalling options and `--event-style` to change them. Code outside of the `Generated`
folder, e.g. partial classes, is not touched.

`new assembly` generates the project in a temporary directory, builds it with `dotnet build` once per runtime identifier and copies
the assemblies, PDBs and XML documentation (and `CsCom.Runtime.dll`) to the output directory. With a single target framework and at
most one runtime identifier, the files are copied directly into the output directory; otherwise into `<TFM>\<RID>` subfolders.

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
- **Enums** become enums and **structs and unions** become blittable structs (`BSTR` and interface pointer fields are `nint`,
  `VARIANT` fields are `ComVariant`, `DATE`, `CURRENCY` and `VARIANT_BOOL` fields are `double`, `long` and `short` or
  [wrappers](#marshalling-options), `SAFEARRAY` fields are `nint` and
  fixed-size arrays are inline arrays).
- **Modules** become static classes with `[LibraryImport]` functions and constants.
- **Aliases** are replaced by the aliased types.
- **Types imported from other type libraries** are generated into `<Namespace>.<LibraryName>`. The well-known OLE Automation types
  `IUnknown`, `IDispatch`, `IEnumVARIANT` and `GUID` are mapped to `object`, `IDispatch`, `IEnumVARIANT` and `Guid`.

| Type library | C# |
| --- | --- |
| `BSTR` | `string?` |
| `LPSTR`, `LPWSTR` | `string?` with `MarshalAs` |
| `VARIANT_BOOL` | `bool` (configurable, see below) |
| `VARIANT` | `object?` (`VariantMarshaller`; `null` is an omitted argument for optional parameters) |
| `IUnknown*` | `object?` |
| `IDispatch*` | `IDispatch?` |
| `DECIMAL` | `decimal` |
| `CURRENCY` | `decimal` (`CurrencyMarshaller`; configurable, see below) |
| `DATE` | `DateTime` (`DateMarshaller`; configurable, see below) |
| `SAFEARRAY(T)` | `T[]?` (`SafeArrayMarshaller<T>`), see below |
| `[out] T*`, `[in, out] T*` | `out T`, `ref T` |

`SAFEARRAY`s of `VARIANT`, `BSTR`, `VARIANT_BOOL`, integers, floating-point numbers, `DECIMAL`, `DATE`, enums, `IUnknown*`
(`UnknownSafeArrayMarshaller`), `IDispatch*` and interfaces are marshalled as zero-based one-dimensional .NET arrays, and so are
arrays inside `VARIANT`s. `SAFEARRAY`s of structs, `CURRENCY`, `SCODE` and dispinterfaces are passed as raw `SAFEARRAY*` (`nint`).
Multi-dimensional `SAFEARRAY`s are not supported.

### Marshalling options

How `DATE`, `CURRENCY` and `VARIANT_BOOL` values are represented can be chosen separately for parameters (and return values)
and for structure fields, which must stay blittable:

| Option value | `DATE` | `CURRENCY` | `VARIANT_BOOL` | Parameters | Fields |
| --- | --- | --- | --- | --- | --- |
| `managed` | `DateTime` | `decimal` | `bool` | default | n/a |
| `com` | `double` | `long` | `short` | ✓ | default |
| `wrapper` | `ComDate` | `ComCurrency` | `ComBool` | ✓ | ✓ |

`ComDate`, `ComCurrency` and `ComBool` are `readonly record struct`s from CsCom.Runtime that store the COM value in `Value`
(an OLE Automation date, a currency value scaled by 10,000, and `VARIANT_TRUE` = -1 / `VARIANT_FALSE` = 0), so they have the
layout of the COM types, and convert implicitly to and from `DateTime`, `decimal` and `bool`:

```csharp
var record = new Record { Date = DateTime.Today, Money = 12.5m, Flag = true }; // with --*-field-marshalling wrapper
DateTime date = record.Date;
```

`ComBool` compares like a Boolean: any non-zero value equals `ComBool.True`. It also has the `true`/`false` operators and the
Boolean operators `!`, `&`, `|` and `^`, which return `ComBool.True` or `ComBool.False`, so it can be used in conditions and
with `&&` and `||` (`if (record.Flag && !other.Flag)`). Passed to `VARIANT` parameters or `dynamic` calls,
the wrappers become `VT_DATE`, `VT_CY` and `VT_BOOL` values. A wrapper parameter can only have its default value as a C# default
value, so optional parameters whose default is `true` or non-zero become required. The options do not affect late-bound
dispinterface wrappers, constants and `SAFEARRAY`s, which use the .NET types.

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

The generated code depends on a few support types: `IDispatch`, `IEnumVARIANT`, `ComActivator`, the `VARIANT`, `SAFEARRAY`, `DATE` and `CURRENCY` marshallers, the
base class of the late-bound wrappers, the event sinks and the `dynamic` support. With `--support-code package`, the generated project references the **CsCom.Runtime**
NuGet package. With `public` or `internal`, the support code is emitted into the project, in the `<Namespace>.ComSupport` namespace.
In the `internal` mode all generated types are internal, because a public interface cannot derive from an internal `IDispatch`.

The generated projects disable runtime marshalling, which source-generated COM interop requires to marshal `VARIANT`s and `DECIMAL`s.

### Limitations

- Events cannot be handled through `dynamic`, and they are connected through the sink classes rather than through events of the
  COM interfaces as with TlbImp (`app.WorkbookOpen += ...`), because C# has no extension events.
- Structure fields stay blittable, so `BSTR`, `SAFEARRAY` and interface pointer fields are raw values.
- `SAFEARRAY`s are converted to zero-based one-dimensional arrays; their lower bounds are not preserved.
- Types imported from other type libraries are always generated into the project; referencing other interop assemblies is not supported yet.
- CsCom.Runtime only targets `net10.0` for now. Source-generated COM expects a base interface from another assembly (`IDispatch`)
  to be compiled for the same target framework, so a `net11.0` build of CsCom.Runtime should be added together with .NET 11 support.
- Transformers can only be registered through the `InteropPipeline` API.

## Architecture

The tool runs the type library through a pipeline (`CsCom.Core`):

1. `TypeLibraryReader` reads the type library with source-generated `ITypeLib`/`ITypeInfo` bindings into the model (`CsCom.Model`).
2. The model transformers (`IModelTransformer`) change the model. The built-in ones resolve aliases and sanitize names.
3. `CSharpEmitter` generates the Roslyn syntax trees.
4. The syntax transformers (`ISyntaxTransformer`) change the syntax trees.
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
