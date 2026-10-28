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
| `--package-source <PATH>` | An additional NuGet source with the CsCom.Runtime package. |
| `--keep-temp` | `new assembly` only: keeps the temporary project used to build the assembly. |
| `-v`, `--verbosity <LEVEL>` | `quiet`, `minimal`, `normal` (default), `detailed` or `diagnostic`. |

`new project` writes `<Name>.csproj`, a `cscom.json` file with the options it was generated with, and the C# files into the
`Generated` folder. `update project` replaces the C# files of the `Generated` folder using the options recorded in `cscom.json`.
It accepts `--namespace`, `--csharp-version` and `--single-file [true|false]` to change them. Code outside of the `Generated`
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
- **Enums** become enums and **structs and unions** become blittable structs (`BSTR` and interface pointer fields are `nint`,
  `VARIANT` fields are `ComVariant`, `CURRENCY` and `DATE` fields are `long` and `double`, `SAFEARRAY` fields are `nint` and
  fixed-size arrays are inline arrays).
- **Modules** become static classes with `[LibraryImport]` functions and constants.
- **Aliases** are replaced by the aliased types.
- **Types imported from other type libraries** are generated into `<Namespace>.<LibraryName>`. The well-known OLE Automation types
  `IUnknown`, `IDispatch`, `IEnumVARIANT` and `GUID` are mapped to `object`, `IDispatch`, `IEnumVARIANT` and `Guid`.

| Type library | C# |
| --- | --- |
| `BSTR` | `string?` |
| `LPSTR`, `LPWSTR` | `string?` with `MarshalAs` |
| `VARIANT_BOOL` | `bool` |
| `VARIANT` | `object?` (`VariantMarshaller`; `null` is an omitted argument for optional parameters) |
| `IUnknown*` | `object?` |
| `IDispatch*` | `IDispatch?` |
| `DECIMAL` | `decimal` |
| `CURRENCY` | `decimal` (`CurrencyMarshaller`) |
| `DATE` | `DateTime` (`DateMarshaller`) |
| `SAFEARRAY(T)` | `T[]?` (`SafeArrayMarshaller<T>`), see below |
| `[out] T*`, `[in, out] T*` | `out T`, `ref T` |

`SAFEARRAY`s of `VARIANT`, `BSTR`, `VARIANT_BOOL`, integers, floating-point numbers, `DECIMAL`, `DATE`, enums, `IUnknown*`
(`UnknownSafeArrayMarshaller`), `IDispatch*` and interfaces are marshalled as zero-based one-dimensional .NET arrays, and so are
arrays inside `VARIANT`s. `SAFEARRAY`s of structs, `CURRENCY`, `SCODE` and dispinterfaces are passed as raw `SAFEARRAY*` (`nint`).
Multi-dimensional `SAFEARRAY`s are not supported.

### Support code

The generated code depends on a few support types: `IDispatch`, `IEnumVARIANT`, `ComActivator`, the `VARIANT`, `SAFEARRAY`, `DATE` and `CURRENCY` marshallers and the
base class of the late-bound wrappers. With `--support-code package`, the generated project references the **CsCom.Runtime**
NuGet package. With `public` or `internal`, the support code is emitted into the project, in the `<Namespace>.ComSupport` namespace.
In the `internal` mode all generated types are internal, because a public interface cannot derive from an internal `IDispatch`.

The generated projects disable runtime marshalling, which source-generated COM interop requires to marshal `VARIANT`s and `DECIMAL`s.

### Limitations

- COM events (connection points and source interfaces) are not generated yet.
- Structure fields stay blittable, so `BSTR`, `CURRENCY`, `DATE`, `SAFEARRAY` and interface pointer fields are raw values.
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
