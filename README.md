# CsCom

[![Build](https://github.com/fedarovich/CsCom/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/fedarovich/CsCom/actions/workflows/ci.yml?query=branch%3Amain)
[![CsCom](https://img.shields.io/nuget/v/CsCom.svg?label=CsCom)](https://www.nuget.org/packages/CsCom)
[![CsCom.Build](https://img.shields.io/nuget/v/CsCom.Build.svg?label=CsCom.Build)](https://www.nuget.org/packages/CsCom.Build)
[![CsCom.Runtime](https://img.shields.io/nuget/v/CsCom.Runtime.svg?label=CsCom.Runtime)](https://www.nuget.org/packages/CsCom.Runtime)

CsCom is a command line tool that produces COM interop code from type libraries or existing .NET interop assemblies, similar to
TlbImp, and an MSBuild package (CsCom.Build) that generates it during the build of a project, see [MSBuild integration](#msbuild-integration).
Unlike TlbImp, it does not rely on the built-in COM interop of .NET: it generates C# code that uses the
[source-generated COM interop](https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation)
added in .NET 8, so the interop assemblies can be trimmed and used with Native AOT.

## Requirements

- Windows: type libraries are read with the OLE Automation APIs. Interop assemblies are read from their metadata.
- .NET 10 SDK or later. The generated projects target .NET 10 or later.

## Usage

```powershell
# Generate a C# project with the interop types
cscom project create C:\Windows\System32\scrrun.dll -o Interop.Scripting

# Generate several type libraries into one project, each into its own namespace
cscom project create Excel.Application Scripting.Dictionary -n Office.Interop --ns Excel=Office.Excel

# Regenerate the C# files of a project created with 'project create' (from the sources recorded in cscom.json)
cscom project update -o Interop.Scripting

# Build a ready-to-use interop assembly
cscom assembly build C:\Windows\System32\scrrun.dll -o bin -p Version=1.0.0

# Pack it into a NuGet package and a symbols package
cscom assembly pack C:\Windows\System32\scrrun.dll -o packages --package-version 1.0.0 -p Authors=Contoso

# List the types and members of a type library
cscom list Scripting.Dictionary --type ^IDictionary$

# Generate the interop code during the build of an existing project, with the CsCom.Build package
cscom reference add Excel.Application --namespace Office.Excel
```

A source (`Source`, which can be repeated, see [Several sources](#several-sources)) can be:
- **A file:** a `.tlb` file, or a `.dll`, `.ocx` or `.exe` file with an embedded type library. A resource index can be appended,
  e.g. `foo.dll\2`.
- **A registered LIBID:** `{00020813-0000-0000-C000-000000000046}`, optionally with the version as in the registry
  (`{00020813-0000-0000-C000-000000000046}:1.9`). The highest registered version is used by default.
- **The ProgID of a registered class,** e.g. `Excel.Application` or `Scripting.Dictionary`, whose type library is used.
- **A .NET interop assembly,** such as a primary interop assembly or the output of TlbImp, see
  [Interop assemblies](#interop-assemblies).

Registered type libraries are found in the registry, so Office type libraries don't have to be looked up inside the Office
installation. `cscom.json` records the LIBID or the ProgID rather than the path, and `project update` looks it up again, e.g.
after an Office update.

### Several sources

The commands generate the sources together into one project, assembly or package, as [CsCom.Build](#msbuild-integration) does:

```powershell
cscom project create Excel.Application Scripting.Dictionary -n Office.Interop --ns Excel=Office.Excel -c Excel=excel.cscom.xml
```

- **Namespaces:** each library is generated into `Interop.<Library>`, after the name of its type library (e.g. `Interop.Scripting`
  for `scrrun.dll`), into a folder of the same name in `Generated`, e.g. `Generated/Interop.Scripting/IDictionary.cs`.
- **Used libraries:** the types that the sources use from other type libraries are generated into the namespace of their library,
  e.g. `Interop.stdole`, or `Interop.Office` and `Interop.VBIDE` for Excel: only the used types, once for all the sources that use
  them. When the other library is a source too, all its types are generated and the other sources use them.
- **Options by library:** `--namespace` and `--config` take the name of the library: `--ns Excel=Office.Excel` or
  `-c stdole=stdole.cscom.xml`, also for the libraries that are only used. Without a library name, they apply to a single source.
  Two libraries with the same namespace are an error.
- **Name:** `--name` is required with several sources; with a single source, it is `Interop.<Library>` by default.
- **Single file:** `--single-file` generates one file with a namespace block per library.

A single source is generated the same way. The projects of earlier versions, which generated the types used from other libraries
into nested namespaces (`<Namespace>.<Library>`), are converted by `project update`.

| Option | Description |
| --- | --- |
| `-o`, `--output <PATH>` | The output directory: `./<Name>` for `project create`, the current directory for `assembly build` and `assembly pack`, and the project directory for `project update`. |
| `-n`, `--name <NAME>` | The project and assembly name. `Interop.<Library>` by default with a single source; required with several sources. |
| `--ns`, `--namespace <[LIBRARY=]NAMESPACE>` | The namespace of a library, e.g. `Excel=Office.Excel`, whether it is a source or is only used; can be specified multiple times. Without a library name, the namespace of a single source. `Interop.<Library>` by default. |
| `-f`, `--framework <TFM>` | The target framework; can be specified multiple times. `net10.0-windows` by default. |
| `-r`, `--runtime <RID>` | The runtime identifier; can be specified multiple times. `AnyCPU` (no runtime identifier) by default. Not supported by `assembly pack`, whose packages are RID-neutral. |
| `-l`, `--cs`, `--csharp-version <VERSION>` | The C# version. C# 14 for .NET 10 and C# 15 for .NET 11 by default; only later versions can be specified. |
| `--single-file` | Generates all types into a single C# file instead of a file per type. |
| `-s`, `--support-code <package\|public\|internal>` | Where the support code comes from, see below. `package` by default. |
| `--date-marshalling`, `--currency-marshalling`, `--bool-marshalling` `<bcl\|none\|wrapper>` | How `DATE`, `CURRENCY` and `VARIANT_BOOL` parameters, return values and structure members are represented: `bcl` (default), `none` or `wrapper`, see [Marshalling options](#marshalling-options). |
| `--struct-properties <auto\|never>` | Whether structure members converted to .NET types are properties (`auto`, default) or all members are public fields of the COM types (`never`), see [Structures](#structures). |
| `--bstr-marshalling <string\|wrapper\|none>` | How `BSTR` parameters and return values are represented: `string` (default), `wrapper` (`ComBstr`) or `none`, see [BSTRs](#bstrs). |
| `--safearray-marshalling <array\|sysarray\|wrapper\|none>` | How typed `SAFEARRAY(T)` parameters and return values are represented: `array` (default, `T[]`), `sysarray`, `wrapper` or `none`, see [SAFEARRAYs](#safearrays). |
| `-e`, `--event-style <events\|observable\|interface\|none>` | The code generated for the events of coclasses: `events` (default), `observable`, `interface` or `none`, see [Events](#events). |
| `--overloads-for-default-values` | Generates overloads for the methods whose optional parameters have default values that C# cannot express, see [Optional parameters](#optional-parameters). |
| `-c`, `--config <[LIBRARY=]PATH>` | An XML file that adds and configures the model and syntax transformers of a library, e.g. `Excel=excel.cscom.xml`; can be specified multiple times. Without a library name, the file of a single source. See [Configuration file](#configuration-file). |
| `--reference-path <PATH>` | For interop assemblies: an assembly they reference, or a directory of such assemblies; can be specified multiple times, see [Interop assemblies](#interop-assemblies). |
| `--package-source <PATH>` | An additional NuGet source with the CsCom.Runtime package. |
| `--keep-temp` | `assembly build` and `assembly pack` only: keeps the temporary project used to build the assembly. |
| `--package-version <VERSION>` | `assembly pack` only: the version of the package and the assembly. `<major>.<minor>.0` of the type library by default with a single source, `1.0.0` with several sources. |
| `-p`, `--property <NAME=VALUE>` | `assembly build` and `assembly pack` only: an MSBuild property of the temporary project, e.g. `Version=1.2.3`, `Company=Contoso` or `PackageLicenseExpression=MIT`; can be specified multiple times. |
| `-v`, `--verbosity <LEVEL>` | `quiet`, `minimal`, `normal` (default), `detailed` or `diagnostic`. |

`project create` writes `<Name>.csproj`, a `cscom.json` file with the sources and the options it was generated with, and the C#
files into the `Generated` folder, in a folder per namespace. `project update` replaces the C# files of the `Generated` folder using
the sources and the options recorded in `cscom.json`; `Source` arguments replace the recorded sources. It accepts `--namespace` and
`--config`, which change the recorded values of the libraries they name (`LIBRARY=` removes one, and `--config ""` removes all the
configuration files), `--csharp-version`, `--single-file [true|false]`, the marshalling options, `--event-style`, `--struct-properties`,
`--overloads-for-default-values [true|false]` and `--reference-path` (an empty value removes the paths) to change them. Code outside of
the `Generated` folder, e.g. partial classes, is not touched.

`assembly build` generates the project in a temporary directory, builds it with `dotnet build` once per runtime identifier and copies
the assemblies, PDBs and XML documentation (and `CsCom.Runtime.dll`) to the output directory. With a single target framework and at
most one runtime identifier, the files are copied directly into the output directory; otherwise into `<TFM>\<RID>` subfolders.
`--property` sets MSBuild properties of the project and overrides the ones CsCom sets, e.g. the assembly version (`Version=1.2.3`)
or a strong name (`SignAssembly=true` and `AssemblyOriginatorKeyFile=<full path>`; relative paths are relative to the temporary
project).

`assembly pack` generates the same project and packs it with `dotnet pack` into `<Name>.<Version>.nupkg`, with the assembly and its
XML documentation in `lib/<TFM>` for each target framework, and `<Name>.<Version>.snupkg` with the PDBs. The package is RID-neutral,
because the interop assembly is IL-only.
- **Metadata:** the package ID is the assembly name (`--name`), the version is `--package-version` or the type library version
  (Excel 1.9 gives `1.9.0`; `1.0.0` with several sources), and the description is `<help string of the type library>: COM interop
  types generated by CsCom.` (`<Library>, <Library>: …` with several sources).
  `--property` sets any other MSBuild property, such as `Authors`, `PackageLicenseExpression`, `RepositoryUrl` or `Copyright`, and
  overrides the defaults.
- **Dependencies:** with `--support-code package`, the package depends on the CsCom.Runtime package of the same version as the
  tool, which the consumers must be able to restore. With `public` or `internal`, it has no dependencies.

### Listing a type library

`cscom list <Source>...` writes the types and members of the libraries with IDL-like signatures, using the names that the
selectors of the [configuration file](#configuration-file) match. Each library starts with a header with its name, version and
namespace; the libraries whose types are only used are marked `(used types)`. `--type <REGEX>` (`-t`) selects the types to list, and
`--config <[LIBRARY=]PATH>` (`-c`) applies the model transformers of configuration files first, e.g. to check renames.

```
> cscom list Scripting.Dictionary --type ^IDictionary$
// Scripting 1.0: Interop.Scripting
interface IDictionary : IDispatch (dual)
    propputref Item([in] VARIANT* Key, [in] VARIANT* pRetItem)
    propput Item([in] VARIANT* Key, [in] VARIANT* pRetItem)
    propget Item([in] VARIANT* Key, [out, retval] VARIANT* pRetItem)
    method Add([in] VARIANT* Key, [in] VARIANT* Item)
    propget Count([out, retval] long* pCount)
    ...
```

### Interop assemblies

The source can also be a .NET interop assembly: a primary interop assembly (PIA), such as an Office PIA from NuGet or from the
global assembly cache, or an assembly that TlbImp imported from a type library. The type library does not have to be installed,
so projects can be generated on build machines:

```powershell
cscom project create Microsoft.Office.Interop.Excel.dll --reference-path C:\packages\office -o Interop.Excel
```

The model is rebuilt from the metadata of the assembly, which is not loaded for execution, following the conventions of TlbImp:
`[retval]` parameters from the return values of methods, `[lcid]` parameters from `LCIDConversion`, coclasses from the `XClass`
classes and their `ComSourceInterfaces`, `propputref` accessors from `let_X`/`set_X` pairs, the properties of dispinterfaces from
their accessors, and the native types from the marshalling information. The default interfaces that TlbImp replaced with coclass
interfaces are restored. The generated code is then the same as from the type library, except for what interop assemblies do not
keep:

- **Documentation:** help strings are not stored, so the generated code has no documentation comments from the library.
- **Modules and aliases:** TlbImp imports only the constants of modules, not their functions, and resolves typedefs.
- **Structure fields:** TlbImp imports `VARIANT_BOOL` fields as `short` and pointers to structures as `IntPtr` (`void*`).
- **Setters:** a property with only a setter is assumed to be a `propput`, or a `propputref` for interfaces, as TlbImp does not
  record which one it was; this matters only for late-bound wrappers.
- **Types:** minor distinctions are lost, e.g. `int` and `long`, or `SCODE` and `HRESULT`. Types are named and ordered as in the
  interop assemblies, e.g. `IFontDisp` of stdole for its `Font` alias, and the coclasses of referenced libraries that are only
  used as parameter types are not generated.

The assemblies that the interop assembly references, e.g. `office.dll` and `stdole.dll` for Excel, are searched next to it, in the
`--reference-path` paths, among the assemblies of the .NET runtime and in the global assembly caches of .NET Framework; `cscom.json`
records the reference paths. When the type library that the assembly was imported from is registered with the same version, CsCom
logs that generating from it keeps the documentation and the functions of modules. Assemblies with embedded interop types
("Embed Interop Types"), which contain only the members their code uses, are rejected: generate from the primary interop assembly
or the type library instead.

**Hand-written declarations.** Assemblies with `[ComImport]` interfaces and classes written for the built-in COM interop are read
too, which migrates them to source-generated COM interop. They can be internal and nested, e.g. in a `NativeMethods` class; only
they and the types they use are imported. The defaults of the built-in COM interop apply where there is no `MarshalAs`:

- **Interfaces** are dual unless `[InterfaceType]` says otherwise, and a `[ComImport]` class gets its default interface from the
  interface that names it in `[CoClass]`.
- **Parameters:** `string`, `object` and `bool` are `BSTR`, `VARIANT` and `VARIANT_BOOL`, arrays are `SAFEARRAY`s, and
  `[MarshalAs(UnmanagedType.LPStruct)] Guid` is a `GUID*`.
- **Structure fields:** `bool` is a 4-byte `BOOL`, and `string` and `char` follow the `CharSet` of the structure, ANSI by default
  (`LPSTR` and a 1-byte character).

What type libraries cannot describe is imported as the closest native type, with a warning: C-style arrays (`LPArray`) and
`StringBuilder` buffers become pointers to their first elements, delegates, managed classes, generic types and custom marshalers
become `IUnknown*` (or `void*` for delegates marshalled as `FunctionPtr`), enums that are not 32-bit become their underlying type,
other value types become `void*`, and explicit layouts other than unions become sequential layouts. `[Flags]` is not imported; the
`AddFlagsAttribute` element of the [configuration file](#configuration-file) adds it.

## MSBuild integration

The **CsCom.Build** package generates the interop code during the build of a project, without a separate interop project or
assembly. The type libraries and interop assemblies are listed as `CsComReference` items:

```xml
<ItemGroup>
  <PackageReference Include="CsCom.Build" Version="0.1.0" />
  <CsComReference Include="C:\Windows\System32\scrrun.dll" />
  <CsComReference Include="Excel.Application" Namespace="Office.Excel" Config="excel.cscom.xml" />
  <CsComReference Include="libs\Legacy.Interop.dll" ReferencePath="libs" />
</ItemGroup>
```

- **Sources:** the item is a source as for the commands: a type library or interop assembly file, relative to the project, a
  registered LIBID or a ProgID. In Visual Studio, a type library or an interop assembly of the project can be given the build
  action `CsComReference`. MSBuild item names are case-insensitive, so `CSComReference` works as well.
- **Namespaces:** each reference is generated into `Interop.<Library>`, after the name of the type library (e.g. `Interop.Scripting`
  for `scrrun.dll`), unless its `Namespace` metadata sets another one.
- **Referenced libraries:** the types that the references use from other type libraries are generated into the namespace of their
  library, e.g. `Interop.stdole`, or `Interop.Office` and `Interop.VBIDE` for Excel. Only the used types are generated, once for
  all the references that use them. When the other library is a reference too, all its types are generated and the other
  references use them. Adding a library as a reference also sets its namespace, options and configuration file. Two libraries
  with the same namespace are an error.
- **Libraries by name:** `CsComLibrary` items set the namespace and the configuration file of a library by its name, like the
  `Library=` values of the commands, also for the libraries whose types are only used:
  `<CsComLibrary Include="stdole" Namespace="Ole.Interop" Config="stdole.cscom.xml" />`. The `Namespace` and `Config` metadata
  of a `CsComReference` win. A name that matches no generated library gives a warning.

The properties are the defaults of all the libraries, including those whose types are only used by the references; the metadata
of the items override them for a reference:

| Item metadata | Property | Values |
| --- | --- | --- |
| `Namespace` | | The namespace of the reference. |
| `ReferencePath` | | For interop assemblies: assemblies or directories that they reference, separated by `;`. |
| `Config` | | The [configuration file](#configuration-file) of the reference. |
| `EventStyle` | `CsComEventStyle` | `events` (default), `observable`, `interface` or `none`, see [Events](#events). |
| `StructProperties` | `CsComStructProperties` | `auto` (default) or `never`, see [Structures](#structures). |
| `OverloadsForDefaultValues` | `CsComOverloadsForDefaultValues` | `false` (default) or `true`, see [Optional parameters](#optional-parameters). |
| `DateMarshalling`, `CurrencyMarshalling`, `BoolMarshalling` | `CsComDateMarshalling`, `CsComCurrencyMarshalling`, `CsComBoolMarshalling` | `bcl` (default), `none` or `wrapper`, see [Marshalling options](#marshalling-options). |
| `BstrMarshalling` | `CsComBstrMarshalling` | `string` (default), `wrapper` or `none`. |
| `SafeArrayMarshalling` | `CsComSafeArrayMarshalling` | `array` (default), `sysarray`, `wrapper` or `none`. |
| | `CsComSupportCode` | `package` (default), `public` or `internal`, see below. |
| | `CsComSupportNamespace` | The namespace of the generated support code; `$(RootNamespace).ComSupport` by default. |
| | `CsComDisableRuntimeMarshalling` | `true` (default) or `false`, see below. |
| | `CsComSuppressGeneratorWarnings` | `true` (default) or `false`, see below. |
| | `CsComOutputPath` | The folder of the generated files; `$(IntermediateOutputPath)CsCom\` by default. |

- **Build:** the code is generated into `obj\<Configuration>\<TFM>\CsCom\<Namespace>\` before the compilation, also in the
  design-time builds of Visual Studio, so that IntelliSense sees the generated types. The package bundles the cscom tool, which
  generates the code on .NET 10 as a separate process, from the MSBuild of the .NET SDK as well as from that of Visual Studio,
  and only when the items, the properties, the package or the files it read (type libraries, interop assemblies, configuration
  files) changed. Unchanged files are not written again. A registered LIBID or ProgID that resolves to another file after an
  update is regenerated by a rebuild.
- **Requirements:** .NET 10 or later and C# 14 or later, taken from the target framework and `LangVersion`. `AllowUnsafeBlocks` is
  enabled.
- **Support code:** it is generated once for all the libraries. With `package`, it comes from the CsCom.Runtime package, which
  CsCom.Build depends on, so the projects that reference the project get it too. With `public` or `internal`, it is generated
  into `CsComSupportNamespace`; `internal` makes all the generated types internal.
- **Runtime marshalling:** source-generated COM interop requires runtime marshalling to be disabled to marshal `VARIANT`s and
  `DECIMAL`s, so CsCom.Build generates `[assembly: DisableRuntimeMarshalling]`. This applies to the whole assembly: the
  `[DllImport]` declarations of the project lose the built-in marshalling of strings, `bool` and other non-blittable types. Use
  `[LibraryImport]` instead, put the interop code into a separate class library, or set `CsComDisableRuntimeMarshalling` to
  `false`, which breaks the members with `VARIANT` and `DECIMAL` values.
- **Warnings:** the generated files disable CS1591, and SYSLIB1230 with CsCom.Runtime. The code that the COM source generator adds
  for public interfaces reports CA2256 when they derive from `IDispatch` of CsCom.Runtime, and CS1591 for the members of base
  interfaces that it declares without documentation. Analyzer configurations do not apply to generated code, so CsCom.Build
  disables these warnings for the project (CS1591 only when it generates documentation), unless `CsComSuppressGeneratorWarnings`
  is `false`. The `internal` support code has neither.
- **Errors:** the errors and warnings are reported in the format of MSBuild, with the location of errors in configuration files,
  e.g. `cscom.xml(3,6): error CSCOM1000: Unknown model transformer <Rename>.`

### Managing references

The `reference` commands edit the `CsComReference` items of a project file, like `dotnet add package` does for packages:

```powershell
cscom reference add Excel.Application --namespace Office.Excel --config excel.cscom.xml
cscom reference add libs\Legacy.Interop.dll --reference-path libs --event-style none
cscom reference list
cscom reference remove Excel.Application
```

- **Project:** `--project <PATH>` selects the project file, or a directory with a single `.csproj`; the current directory by
  default.
- **`reference add <Source>...`:** adds an item for each source, after the existing `CsComReference` items or in a new
  `ItemGroup`. The item of a source that the project already has is updated.
  - **The CsCom.Build package:** a `PackageReference` to it is added too if the project file has none. With central package
    management, it has no version, and a warning gives the `PackageVersion` to add to `Directory.Packages.props`.
  - **Options:** `--namespace` (only with a single source), `--config`, `--reference-path` and the options of the generated
    code (`--event-style`, `--struct-properties`, `--overloads-for-default-values` and the `--*-marshalling` options) are
    written as the metadata of the table above. The options that are not specified are not written, so the `CsCom*`
    properties of the project apply to them.
  - **Sources:** files are written relative to the project, except those that share only the drive with it, such as system
    files, which keep their full paths. LIBIDs are normalized, and ProgIDs are written as is. A source that is not found on
    this machine gives a warning but is added, e.g. a library that is only registered on the build machines.
- **`reference list`:** writes each item of the project file with its metadata, e.g.
  `Excel.Application Namespace=Office.Excel Config=excel.cscom.xml`. The project is not evaluated: the items of imported
  files and of targets are not listed.
- **`reference remove <Source>...`:** removes the items of the sources, given as `reference list` shows them or as files
  relative to the current directory, and the item groups that become empty. If the project has no item for one of the
  sources, nothing is removed. The reference to CsCom.Build is kept.
- **Formatting:** only the edited elements change. The indentation, the line endings, the byte order mark and the XML
  declaration of the file are kept, and the elements of old-style projects get the MSBuild namespace.

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
- **`project update`:** `project create` records the files in `cscom.json` by library name, relative to the project directory, and
  `project update` applies them again. `--config LIBRARY=PATH` replaces the file of a library, `--config LIBRARY=` removes it, and
  `--config ""` removes all of them.
- **Custom transformers:** they are registered through the API with `InteropPipeline.Registry.AddModelTransformer(name, element => ...)`
  and `AddSyntaxTransformer`, whose factories read their element with `ConfigurationElement`.
- **Schema and starter file:** `cscom config create [-o <dir>]` writes `cscom.xsd`, the schema of the configuration files, and a
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
| `RenameMembersToPascalCase` | `Type` and `Member` (both optional) | Converts the names of the matching members, or of all members, to PascalCase, see below. Parameters and types are not renamed. |
| `SetMarshalling` | `Type`, `Member` and `Parameter` (all optional); at least one of `Dates`, `Currencies`, `Booleans` (`bcl\|none\|wrapper`), `Bstrs` (`string\|wrapper\|none`) and `SafeArrays` (`array\|sysarray\|wrapper\|none`) | Changes the [marshalling options](#marshalling-options) of the parameters and return values of the matching methods, and of the matching dispinterface properties and structure fields. With `Parameter`, only of the matching parameters, including `[retval]` parameters. Later elements override the values set by earlier ones. |
| `RemoveEnumPrefix` | `Type` (optional) | Removes the prefix shared by the members of the matching enums, or of all enums. |
| `ExcludeType` | `Type` and/or `Kind` | Removes the matching types. References to interfaces, dispinterfaces and coclasses become `IDispatch*` (if they are dispatchable) or `IUnknown*`, references to enums `long`, and pointers to structures `void*`. Structures used by value and base interfaces of kept interfaces cannot be removed. |
| `SetType` | `Type` (optional), `Member`, `Parameter` (optional), `To` | Changes the type of the matching parameters, or without `Parameter` of the members themselves (return values, property values, dispinterface properties, structure fields), to the type of the library named `To`, e.g. a `long` that is meant to be an enum. Parameters passed by reference keep their pointer. Vtable and dual interfaces, functions and structures only accept compatible types: 4-byte integers to enums, interface pointers to interfaces, and structures of the same size; dispinterfaces accept any type. |
| `SetSafeArrayRank` | `Type`, `Member` and `Parameter` (all optional), `Rank` (`1`–`3`) | Sets the number of dimensions of the `SAFEARRAY`s of the matching parameters, return values and dispinterface properties, see [SAFEARRAYs](#safearrays). |

```xml
<RenameMember Type="^IShape$" Member="^Move$" Parameter="^d(.)$" NewName="delta$1" />    <!-- dx, dy -> deltax, deltay -->
<SetMarshalling Type="^IStrings$" Member="^Echo$" Parameter="^Value$" Bstrs="none" />   <!-- Echo(nint Value) -->
<SetMarshalling Type="^Record$" Member="^Date$" Dates="wrapper" />                       <!-- a ComDate field -->
<RenameMembersToPascalCase Kind="enum|struct" />                                         <!-- MAX_VALUE -> MaxValue, checked -> Checked -->
```

`RemoveEnumPrefix` removes the longest common prefix that ends at a word boundary, after an underscore or before an upper case letter
that follows a lower case letter or a digit: `xlArea`/`xlBar` become `Area`/`Bar` and `SHAPE_CIRCLE`/`SHAPE_SQUARE` become
`CIRCLE`/`SQUARE`. Enums with a single member, or whose members would not start with a letter (`Size16`/`Size32`), are not changed.

`RenameMembersToPascalCase` splits a name into words at the characters that are not letters or digits, usually `_`, and joins them
with the first letter of each word in upper case. Words in upper case with more than two letters become title case, and the other
words keep the rest of their letters, so names that are PascalCase already, acronyms included, are kept:

| Name | New name |
| --- | --- |
| `MAX_VALUE`, `checked`, `_NewEnum`, `xlArea` | `MaxValue`, `Checked`, `NewEnum`, `XlArea` |
| `HWND`, `IO_ERROR`, `OK_BUTTON` | `Hwnd`, `IOError`, `OKButton` (two-letter words stay in upper case) |
| `Count`, `XMLDocument`, `IO`, `Value2` | not changed |
| property `SOME_PROPERTY` | `SomeProperty`, with the accessors `get_SomeProperty` and `set_SomeProperty` |
| method `get_SomeValue` | `GetSomeValue` |

- **Accessors:** the accessors of a property share its name, so only the part after `get_`, `set_` or `putref_` changes. Ordinary
  methods whose names look like accessors are converted like any other name.
- **Collisions:** a member keeps its name, with a warning, if the new name is the name of another member of the type, e.g.
  `count` next to `Count`. Names that would be empty or start with a digit, such as `_1`, are kept as well.
- **Enum prefixes:** with `RemoveEnumPrefix`, either order gives the same names for enums like `SHAPE_CIRCLE`/`SHAPE_SQUARE`:
  `Circle`/`Square`.

### Syntax transformers

The syntax transformers run after the code is generated, so their selectors match the final names, after the model transformers.

| Element | Attributes | Effect |
| --- | --- | --- |
| `AddFlagsAttribute` | `Type` | Adds `[System.Flags]` to the matching enums, which type libraries cannot mark as bit fields. Enums that already have it are not changed. |
| `AddAttribute` | `Type` and/or `Kind`, `Member` (optional), `Role` (optional), `Attribute` | Adds a C# attribute (without brackets) to the declarations generated for the matching types or members. `Role` selects which declaration: `Declaration` (default), `Extensions` (the extensions class of an interface), `EventHandler`, `EventSink`, `EventDelegate`, `EventArgs`, `Event`, `Property`, `BackingField`, `InlineArray`, `Enumerator` or `Overload` (the [overloads](#optional-parameters) of a method). Declarations that already have the attribute are not changed. |

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
- **Types imported from other type libraries** are generated into the namespace of their library, `Interop.<Library>` by default,
  see [Several sources](#several-sources). The well-known OLE Automation types
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
- <a id="known-ranks"></a>**Known ranks:** when the rank of a parameter is known, the `SetSafeArrayRank` element of the [configuration file](#model-transformers)
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
  a `defaultvalue` become required, together with the optional parameters before them, unless
  [overloads](#optional-parameters) are generated.
- **Events:** dual event interfaces can also be invoked through `IDispatch::Invoke`, which passes `BSTR`s in `VARIANT`s. In the
  `wrapper` and `none` modes, their events with `BSTR` parameters are only received through the vtable.
- **Other places:** structure fields are `ComBstrValue`s (see [Structures](#structures)), or `nint` with `none`, and late-bound
  wrappers, dispinterface events, `VARIANT`s, the elements of `SAFEARRAY`s and constants use `string`. A `ComBstr` passed to a
  `VARIANT` parameter is passed as a copy.

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
value, so optional parameters whose default is `true` or non-zero become required, unless [overloads](#optional-parameters)
are generated. The options do not affect late-bound dispinterface wrappers, constants and the elements of `SAFEARRAY`s, which use
the .NET types.

All marshalling options, including `--bstr-marshalling` and `--safearray-marshalling`, can be changed for individual methods,
parameters, dispinterface properties and structure fields with the `SetMarshalling` element of the
[configuration file](#model-transformers).

### Optional parameters

C# requires the optional parameters to follow the required ones and their default values to be constants, so a parameter whose
type library default has no C# constant makes it and the parameters before it required: `DATE` defaults (`DateTime` has no
constants), and `VARIANT_BOOL`, `CURRENCY`, `DATE` and `BSTR` defaults with the `wrapper` and `none` options.
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

### Structures

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

### Late binding with `dynamic`

The COM objects created by source-generated COM interop do not support `dynamic` by themselves. CsCom.Runtime provides
`DynamicDispatchObject`, which resolves members by name with `IDispatch::GetIDsOfNames` and invokes them with `IDispatch::Invoke`:

```csharp
dynamic fso = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject"); // by ProgID or CLSID, no interop types needed
dynamic dictionary = Dictionary.CreateDynamic();   // generated for coclasses that support IDispatch
dynamic shape = typedShape.AsDynamic();            // any IDispatch-based interface or late-bound wrapper
dynamic tag = ComDynamic.ToDynamic(typedShape.Tag); // an object returned in a VARIANT
dynamic excel = ComActivator.GetActiveDynamicInstance("Excel.Application"); // a running instance
dynamic book = ComActivator.BindToDynamicMoniker(@"C:\Data\Book.xlsx");      // VB's GetObject: a document, winmgmts:, ...

dictionary.Add("a", 1);                            // methods
int count = dictionary.Count;                      // properties
object item = dictionary.Item("a");                // parameterized properties are invoked like methods
dictionary.Item["a"] = 3;                          // or indexed, also to set them, like sheet.Range["A1"].Value = 5
dictionary["b"] = 2;                               // indexers and invocations use the default member (DISPID_VALUE)
string path = fso.BuildPath(Path: @"C:\a", Name: "b"); // named arguments
calculator.Swap(ref a, ref b);                     // ref and out arguments
string drive = fso.GetFolder(@"C:\Windows").Drive.DriveLetter; // returned COM objects are dynamic too
foreach (var key in dictionary) { }                // _NewEnum
IDispatch dispatch = dictionary;                   // conversions to IDispatch, the implemented COM interfaces
DShapeEvents events = shape.GetEvents();           // and the late-bound wrappers of dispinterfaces
```

- **Returned objects:** COM objects returned by dynamic calls are dynamic as well, so calls can be chained. There is one dynamic
  object per COM object, so `==` compares COM objects (`if (sheet == excel.ActiveSheet)`), like the runtime callable wrappers
  of the built-in COM interop. The dynamic objects are attached to the managed objects of the COM objects and do not keep them
  alive.
- **Properties:** assigning a COM object to a property or an indexer uses `PROPERTYPUTREF` when the property supports it, like
  VB's `Set`. A property that requires arguments, or can only be set, can be indexed with its arguments: `dictionary.Item["a"]`,
  `dictionary.Key["a"] = "b"`.
- **`ref` and `out` arguments:** they receive the new values. A variable of a COM type is passed as a reference to its type,
  e.g. an `int` as `VT_BYREF | VT_I4`, which typed by-reference parameters such as `long*` require, so its type must match the
  parameter: `int` for `long`, `double` for `double`, `bool` for `VARIANT_BOOL`, `string` for `BSTR`. An `object` or `dynamic`
  variable is passed as a reference to a `VARIANT`, for `VARIANT*` parameters.
- **Running objects and monikers:** `ComActivator.GetActiveInstance<T>` and `GetActiveDynamicInstance` return an instance
  registered in the running object table, like `Marshal.GetActiveObject` of .NET Framework, which .NET does not have;
  `TryGetActiveInstance<T>` and `TryGetActiveDynamicInstance` return `false` instead of throwing when no instance is running
  or the ProgID is not registered.
  `BindToMoniker<T>` and `BindToDynamicMoniker` bind a moniker display name, like VB's `GetObject`: the path of a document,
  which its application opens, `winmgmts:` for WMI or `new:ProgID` for a new instance. Office applications register
  themselves only after their window has lost the focus once, so an instance that has just been started may not be found.
- **Events:** `+=` and `-=` add and remove handlers of the events of the source dispinterfaces of the object's coclass, found
  through `IProvideClassInfo` or, without it, through the first coclass of the type library whose default interface is that
  of the object. A handler is a delegate with at most the parameters of the event, so a lambda needs a cast. Its `ref` and
  `out` parameters and its result are returned to the COM object:

  ```csharp
  excel.WorkbookOpen += (Action<dynamic>)(book => Console.WriteLine(book.Name));
  excel.WorkbookBeforeClose += new BeforeCloseHandler((dynamic book, ref bool cancel) => cancel = true);
  ComDynamic.AddEventHandler(book, "Activate", handler); // also for events named like methods, e.g. Workbook.Activate
  ComDynamic.RemoveEventHandlers(excel);                 // removes all handlers of the object

  delegate void BeforeCloseHandler(dynamic book, ref bool cancel);
  ```

  The object is connected to the event interface while it has handlers, so remove them, on the thread that added them, when
  they are no longer needed: the COM object holds the handlers, and handlers that refer to the object keep both alive. Only
  dispinterfaces are supported; dual and vtable event interfaces need the generated sinks (see [Events](#events)).
- **Releasing objects:** the COM references are released when the objects are garbage collected, and finalizers do not run
  when the process exits. An Office application exits after `Quit()` only when all its objects are released, so let them go
  out of scope and call `GC.Collect()` and `GC.WaitForPendingFinalizers()`, otherwise it keeps running for minutes.
- **Debugger:** the dynamic view of the debugger lists the properties described by the type information of the object, and
  `ComDynamic.GetTypeName` returns the name of its type, like VB's `TypeName`.
- **Errors:** unknown members throw `RuntimeBinderException`. Errors raised by the COM object throw `COMException`, and so do
  failed invocations, with messages that name the member and the argument, e.g. "Type mismatch in argument 2 of 'BuildPath'."

The C# runtime binder requires dynamic code, so `dynamic` cannot be used with Native AOT: the entry points are annotated with
`[RequiresDynamicCode]`, and the typed interop stays trimming and AOT compatible.

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

- Events of the generated interfaces are connected through the sink classes rather than through events of the COM interfaces as
  with TlbImp (`app.WorkbookOpen += ...`), because C# has no extension events; `dynamic` objects support `+=` for the events
  of dispinterfaces.
- Structures keep the layout of the COM structures, so `VARIANT`, `SAFEARRAY` and interface pointer members are raw values, and
  `BSTR` members are `ComBstrValue` views that the code using the structure allocates and frees; only `DATE`, `CURRENCY` and
  `VARIANT_BOOL` members are converted.
- `SAFEARRAY`s are converted to zero-based .NET arrays of up to three dimensions, and `T[,]`/`T[,,]` require a
  [known rank](#known-ranks); only `ComSafeArray<T>` keeps their bounds and supports more dimensions.
- The types used from other type libraries are generated into the project, in the namespaces of their libraries; referencing
  other interop assemblies that contain them is not supported yet.
- Interop assemblies lack the documentation, the functions of modules and some type information of their type libraries, see
  [Interop assemblies](#interop-assemblies).
- CsCom.Runtime only targets `net10.0` for now. Source-generated COM expects a base interface from another assembly (`IDispatch`)
  to be compiled for the same target framework, so a `net11.0` build of CsCom.Runtime should be added together with .NET 11 support.

## Architecture

The tool runs the type library through a pipeline (`CsCom.Core`):

1. `TypeLibraryReader` reads the type library with source-generated `ITypeLib`/`ITypeInfo` bindings into the model (`CsCom.Model`),
   or `InteropAssemblyReader` reads an interop assembly with `MetadataLoadContext` into the same model.
2. The model transformers (`IModelTransformer`) change the model. The built-in ones resolve aliases first and sanitize names last;
   those of `InteropPipeline.ModelTransformers` and of the [configuration file](#configuration-file) run in between.
3. `CSharpEmitter` generates the Roslyn syntax trees.
4. The syntax transformers (`ISyntaxTransformer`) change the syntax trees. The generated declarations carry annotations that
   associate them with the model elements, which `TransformationContext.Annotations` resolves: `GetElement(node)` gives the
   element of a node and its role (`SyntaxRole.Declaration`, `Extensions`, `EventSink`, …), and `GetNodes(root, element, role)`
   finds the nodes generated for an element.
5. `ProjectGenerator` and `AssemblyGenerator` write the project, or build or pack the assembly.

For CsCom.Build, `LibrarySetGenerator` generates several libraries into one project: it reads each reference, merges the types
that they import from other libraries into a model per library, transforms each library with its options and configuration, and
emits it with the namespaces of all the libraries. The commands and CsCom.Build both use it: `LibrarySetBuild` runs the request
of the MSBuild task, which the task passes to the hidden `build-generate` command of the bundled cscom tool.
`CsComReferenceEditor` edits the `CsComReference` items of project files for the `reference` commands.

| Project | Description |
| --- | --- |
| `src/CsCom` | The command line tool (`cscom`), packed as a .NET tool together with the CsCom.Runtime package. |
| `src/CsCom.Build` | The CsCom.Build package: the MSBuild props, targets and task (netstandard2.0), which runs the cscom tool bundled in its `tools` folder. |
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
- Building CsCom.Runtime and CsCom.Build creates their packages in `artifacts/packages`, which the tool bundles (CsCom.Runtime)
  and the integration tests use. The integration tests restore them into a temporary global packages folder; the tests of
  CsCom.Build also build with the 32-bit and 64-bit MSBuild of Visual Studio when it is installed. When using the packages from
  source, clear `%USERPROFILE%\.nuget\packages\cscom.runtime` and `cscom.build` after changing them without changing the version.
- The test type libraries `src/CsCom.Tests/TestAssets/CsComTest.tlb` and `CsComDrawing.tlb` are built from their `.idl` files
  with `build-tlb.ps1`, which requires Visual Studio with the C++ tools. The `.tlb` files are committed.
- The snapshots of the generated code are in `src/CsCom.Tests/Snapshots`. Verify.MSTest is pinned to 32.0.0, the last version
  without the sponsorship check.

### CI and releases

The [CI workflow](.github/workflows/ci.yml) builds the solution in Release on Windows, runs the unit tests (without the
`Integration` category) and packs the three packages for every push and pull request; the packages are kept as the `packages`
artifact of the run.

- **Releasing:** push a tag with a semantic version, e.g. `1.2.3` or `1.2.3-beta.1`. The tag sets the version of CsCom,
  CsCom.Runtime and CsCom.Build (instead of the `VersionPrefix` of `src/Directory.Build.props`), and the `publish` job pushes
  them to nuget.org. A tag that is not a semantic version fails the build.
- **Trusted publishing:** the job gets a short-lived API key from nuget.org instead of using a stored one. This requires a
  Trusted Publishing policy on nuget.org (repository owner `fedarovich`, repository `CsCom`, workflow file `ci.yml`) and the
  repository secret `NUGET_USER` with the nuget.org profile name (not the email address).
