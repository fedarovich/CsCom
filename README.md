# CsCom

[![Build](https://github.com/fedarovich/CsCom/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/fedarovich/CsCom/actions/workflows/ci.yml?query=branch%3Amain)
[![CsCom](https://img.shields.io/nuget/v/CsCom.svg?label=CsCom)](https://www.nuget.org/packages/CsCom)
[![CsCom.Build](https://img.shields.io/nuget/v/CsCom.Build.svg?label=CsCom.Build)](https://www.nuget.org/packages/CsCom.Build)
[![CsCom.Runtime](https://img.shields.io/nuget/v/CsCom.Runtime.svg?label=CsCom.Runtime)](https://www.nuget.org/packages/CsCom.Runtime)
[![CsCom.Runtime.Dynamic](https://img.shields.io/nuget/v/CsCom.Runtime.Dynamic.svg?label=CsCom.Runtime.Dynamic)](https://www.nuget.org/packages/CsCom.Runtime.Dynamic)

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
  (`{00020813-0000-0000-C000-000000000046}:1.9`). By default, the highest registered version whose file exists is used,
  whether it is registered for 64-bit or for 32-bit applications.
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
  for `scrrun.dll`), with the characters that a namespace cannot have replaced by underscores and an underscore after a C# keyword
  (e.g. `Interop.event_`), into a folder of the same name in `Generated`, e.g. `Generated/Interop.Scripting/IDictionary.cs`. The file
  of a type named like a device, which Windows cannot create, gets a trailing underscore, e.g. `Aux_.cs`.
- **Used libraries:** the types that the sources use from other type libraries are generated into the namespace of their library,
  e.g. `Interop.stdole`, or `Interop.Office` and `Interop.VBIDE` for Excel: only the used types, once for all the sources that use
  them. When the other library is a source too, all its types are generated and the other sources use them.
- **Options by library:** `--namespace` and `--config` take the name of the library: `--ns Excel=Office.Excel` or
  `-c stdole=stdole.cscom.xml`, also for the libraries that are only used. Without a library name, they apply to a single source,
  except that `--namespace` is the root namespace of several sources. Two libraries with the same namespace are an error.
- **Name:** `--name` is required with several sources; with a single source, it is `Interop.<Library>` by default.
- **Root namespace:** `--ns` without a library name sets the root namespace of several sources: the `RootNamespace` of the
  project, and the namespace under which the support code of `--support-code public` or `internal` is generated, once, into
  `<RootNamespace>.ComSupport`. The libraries keep their own namespaces. It is `Interop.<Name>` by default, e.g. `Interop.Office`
  for `-n Office`, with the characters of the name that a namespace cannot have replaced by underscores and an underscore after a
  C# keyword (e.g. `Interop.Set.class_` for `-n Set.class`); a name that is `Interop` or starts with `Interop.`, in this case, gets
  no second prefix. The root namespace of a single source is its namespace. A namespace of the
  generated code cannot be the name of a generated type: with `--support-code public` or `internal`, `-n Scripting.Dictionary`
  is an error, because the support code would be in `Interop.Scripting.Dictionary.ComSupport` next to the coclass
  `Interop.Scripting.Dictionary`; choose another root namespace with `--ns`.
- **Single file:** `--single-file` generates one file with a namespace block per library.

A single source is generated the same way. The projects of earlier versions, which generated the types used from other libraries
into nested namespaces (`<Namespace>.<Library>`), are converted by `project update`.

| Option | Description |
| --- | --- |
| `-o`, `--output <PATH>` | The output directory: `./<Name>` for `project create`, the current directory for `assembly build` and `assembly pack`, and the project directory for `project update`. |
| `-n`, `--name <NAME>` | The project and assembly name. `Interop.<Library>` by default with a single source; required with several sources, whose root namespace is `Interop.<Name>` by default, see above. |
| `--ns`, `--namespace <[LIBRARY=]NAMESPACE>` | The namespace of a library, e.g. `Excel=Office.Excel`, whether it is a source or is only used; can be specified multiple times. `Interop.<Library>` by default. Without a library name, the namespace of a single source, or the root namespace of several sources (`Interop.<Name>` by default), under which the support code is generated. |
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
| `--ni`, `--named-indexers <none\|classproperty\|structmethod>` | Gives indexer syntax to the parameterized properties other than the indexer: `none` (default), `classproperty` (`sheet.Range["A1"]`) or `structmethod` (`sheet.Range()["A1"]`), see [Named indexers](#named-indexers). |
| `--dynamic`, `-d` | Generates the late binding with C# `dynamic`: `CreateDynamic()` of the coclasses and the support of CsCom.Runtime.Dynamic, see [Late binding with `dynamic`](#late-binding-with-dynamic). |
| `-c`, `--config <[LIBRARY=]PATH>` | An XML file that adds and configures the model and syntax transformers of a library, e.g. `Excel=excel.cscom.xml`; can be specified multiple times. Without a library name, the file of a single source. See [Configuration file](#configuration-file). |
| `--reference-path <PATH>` | For interop assemblies: an assembly they reference, or a directory of such assemblies; can be specified multiple times, see [Interop assemblies](#interop-assemblies). |
| `--package-source <PATH>` | An additional NuGet source, a folder or the URL of a feed, with the CsCom.Runtime and CsCom.Runtime.Dynamic packages, e.g. when they are not available on nuget.org. `project create` writes it into a `nuget.config` of the project directory, and fails when the directory already has one. |
| `--keep-temp` | `assembly build` and `assembly pack` only: keeps the temporary project used to build the assembly. The project of a build that fails is kept in any case. The projects that were kept are deleted by a run of these commands a week later. |
| `--package-version <VERSION>` | `assembly pack` only: the version of the package and the assembly. `<major>.<minor>.0` of the type library by default with a single source, `1.0.0` with several sources. |
| `-p`, `--property <NAME=VALUE>` | `assembly build` and `assembly pack` only: an MSBuild property of the temporary project, e.g. `Version=1.2.3`, `Company=Contoso` or `PackageLicenseExpression=MIT`; can be specified multiple times. |
| `-v`, `--verbosity <LEVEL>` | `quiet`, `minimal`, `normal` (default), `detailed` or `diagnostic`. |

`project create` writes `<Name>.csproj`, a `cscom.json` file with the sources and the options it was generated with, and the C#
files into the `Generated` folder, in a folder per namespace. `project update` replaces the C# files of the `Generated` folder using
the sources and the options recorded in `cscom.json`; `Source` arguments replace the recorded sources. It accepts `--namespace` and
`--config`, which change the recorded values of the libraries they name (`LIBRARY=` removes one, and `--config ""` removes all the
configuration files), `--csharp-version`, `--single-file [true|false]`, the marshalling options, `--event-style`, `--struct-properties`,
`--overloads-for-default-values [true|false]`, `--named-indexers`, `--dynamic [true|false]` and `--reference-path` (an empty value removes the paths) to change them.
`cscom.json` records the root namespace of several sources, which the update keeps, so that the support code does not move: also when
the order or the number of the sources changes, down to a single source, until `--namespace` without a library name sets another one.
With a single source, that is its namespace, which is its root namespace again. The projects of several sources of earlier versions,
and the projects of a single source that get more sources, keep the namespace of their first source as their root namespace, which the
update records: on that update, list the original first source first, also when the sources are replaced or reordered, and keep its
namespace, or set the root namespace with `--namespace`, so that the support code stays where it is. Update a project of a single source
of an earlier version once without `Source` arguments before you add sources: its namespace and configuration file apply only to a
single source until the update records them by library name. Update a project with the version
of the tool that created it or a later one, e.g. pinned in a tool manifest: earlier versions do not know the recorded root namespace,
see [Known issues](docs/known-issues.md#projects). Code outside of
the `Generated` folder, e.g. partial classes, is not touched. `cscom.json` records the type libraries and interop assemblies of the
project directory by their relative paths, so the project can be updated from another clone. In the project file, the update sets the versions of the CsCom.Runtime
packages, `LangVersion` only with `--csharp-version`, and `RootNamespace` only with `--namespace` without a library name; neither is
added to a project file without it, and the rest of the file keeps its text and its encoding.

`assembly build` generates the project in a temporary directory, builds it with `dotnet build` once per runtime identifier and copies
the assemblies, PDBs and XML documentation (and `CsCom.Runtime.dll`) to the output directory. With a single target framework and at
most one runtime identifier, the files are copied directly into the output directory; otherwise into `<TFM>\<RID>` subfolders.
`--property` sets MSBuild properties of the project and overrides the ones CsCom sets, e.g. the assembly version (`Version=1.2.3`)
or a strong name (`SignAssembly=true` and `AssemblyOriginatorKeyFile=<full path>`; relative paths are relative to the temporary
project). Ctrl+C stops the build and deletes the temporary project, once the code is generated; a second Ctrl+C ends the tool
at once. The project of a build that fails is kept, and its path is reported.

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
- **Structure fields:** `bool` is a 4-byte `BOOL`, `string` and `char` follow the `CharSet` of the structure, ANSI by default
  (`LPSTR` and a 1-byte character), and `object` is an `IUnknown*`, or a `VARIANT` with `UnmanagedType.Struct`.

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
  <PackageReference Include="CsCom.Build" Version="3.0.0" />
  <CsComReference Include="C:\Windows\System32\scrrun.dll" />
  <CsComReference Include="Excel.Application" Namespace="Office.Excel" Config="excel.cscom.xml" />
  <CsComReference Include="libs\Legacy.Interop.dll" ReferencePath="libs" />
</ItemGroup>
```

- **Sources:** the item is a source as for the commands: a type library or interop assembly file, relative to the project, a
  registered LIBID or a ProgID. In Visual Studio, a type library or an interop assembly of the project can be given the build
  action `CsComReference`. MSBuild item names are case-insensitive, so `CSComReference` works as well.
- **Namespaces:** each reference is generated into `Interop.<Library>`, after the name of the type library (e.g. `Interop.Scripting`
  for `scrrun.dll`, and `Interop.event_` for a library named like a C# keyword), unless its `Namespace` metadata sets another one.
  A namespace cannot be the name of a generated type, e.g. a `CsComSupportNamespace` of `Interop.Scripting.Dictionary.ComSupport`
  next to the coclass `Interop.Scripting.Dictionary`.
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
| `NamedIndexers` | `CsComNamedIndexers` | `none` (default), `classproperty` or `structmethod`, see [Named indexers](#named-indexers). |
| | `CsComDynamic` | `false` (default) or `true`: the late binding with `dynamic` for all the libraries. With the `package` support code, the project must reference the CsCom.Runtime.Dynamic package, which `cscom reference add --dynamic` adds; the build fails otherwise. See [Late binding with `dynamic`](#late-binding-with-dynamic). |
| `DateMarshalling`, `CurrencyMarshalling`, `BoolMarshalling` | `CsComDateMarshalling`, `CsComCurrencyMarshalling`, `CsComBoolMarshalling` | `bcl` (default), `none` or `wrapper`, see [Marshalling options](#marshalling-options). |
| `BstrMarshalling` | `CsComBstrMarshalling` | `string` (default), `wrapper` or `none`. |
| `SafeArrayMarshalling` | `CsComSafeArrayMarshalling` | `array` (default), `sysarray`, `wrapper` or `none`. |
| | `CsComSupportCode` | `package` (default), `public` or `internal`, see below. |
| | `CsComSupportNamespace` | The namespace of the generated support code; `$(RootNamespace).ComSupport` by default. |
| | `CsComDisableRuntimeMarshalling` | `true` (default) or `false`, see below. |
| | `CsComSuppressGeneratorWarnings` | `true` (default) or `false`, see below. |
| | `CsComOutputPath` | The folder of the generated files; `$(IntermediateOutputPath)CsCom\` by default, see below. |
| | `CsComShowGeneratedFiles` | `true` (default) or `false`: whether Solution Explorer shows the generated files. |
| | `CsComGeneratedFilesFolder` | The virtual folder of Solution Explorer that shows the generated files of the intermediate output; `CsCom` by default. |

- **Build:** the code is generated into `obj\<Configuration>\<TFM>\CsCom\<Namespace>\` before the compilation, also in the
  design-time builds of Visual Studio, so that IntelliSense sees the generated types. The package bundles the cscom tool, which
  generates the code on .NET 10 as a separate process, from the MSBuild of the .NET SDK as well as from that of Visual Studio,
  and only when the items, the properties, the package or the files it read (type libraries, interop assemblies, configuration
  files) changed. Unchanged files are not written again. A registered LIBID or ProgID that resolves to another file after an
  update is regenerated by a rebuild. The warnings of the generator are reported by every build, also by those that do not
  run it, and the files it read are inputs of the fast up-to-date check of Visual Studio.
- **Generated files in Solution Explorer:** the files of the intermediate output are shown under the virtual folder `CsCom`
  (`CsComGeneratedFilesFolder`), e.g. `CsCom\Interop.Scripting\Dictionary.cs`. They are overwritten by the build, so they are
  for reading and navigating. Solution Explorer shows the files of the last build when Visual Studio evaluates the project, so
  after the first build, or after a reference adds or removes files, they appear when the project is evaluated again, e.g. after
  a change of the project file. IntelliSense always has the current files.
- **Committing the generated files:** set `CsComOutputPath` to a folder of the project, e.g. `Generated\CsCom\`, which
  Solution Explorer shows in place. The code can depend on the target framework (e.g. C# 15 for .NET 11), so a project with
  several target frameworks needs a folder per framework, e.g. `Generated\$(TargetFramework)\`. Only the files that CsCom
  generated (those that start with its `<auto-generated>` header) are deleted from the folder when they are no longer generated.
  The folder only gets the generated files: the state of the generation stays in the intermediate output, so a rebuild, or a
  build of a fresh clone, generates the code again and writes the files that changed.
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
    code (`--event-style`, `--struct-properties`, `--overloads-for-default-values`, `--named-indexers` and the `--*-marshalling` options) are
    written as the metadata of the table above. The options that are not specified are not written, so the `CsCom*`
    properties of the project apply to them.
  - **`--dynamic` (`-d`):** sets the `CsComDynamic` property of the project and adds a reference to CsCom.Runtime.Dynamic.
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

- **Order:** the transformers run in the order of their elements, and each sees the model as the elements before it left it,
  e.g. the names that a rename produced and the types that `SetType` set. The model transformers run after the aliases are
  resolved and before the names are sanitized, so they see the resolved aliases and the names they produce are made valid
  and unique.
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
| `SetMarshalling` | `Type`, `Member` and `Parameter` (all optional); at least one of `Dates`, `Currencies`, `Booleans` (`bcl\|none\|wrapper`), `Bstrs` (`string\|wrapper\|none`), `SafeArrays` (`array\|sysarray\|wrapper\|none`) and `Interfaces` (`shared\|unique`) | Changes the [marshalling options](#marshalling-options) of the parameters and return values of the matching methods, and of the matching dispinterface properties and structure fields. With `Parameter`, only of the matching parameters, including `[retval]` parameters. Later elements override the values set by earlier ones. A type that none of the attributes applies to does not match: only `SafeArrays` applies to dispinterfaces, and only `Dates`, `Currencies`, `Booleans` and `Bstrs` to structures; `Interfaces` only applies to interface pointers that are not passed in and out, see [Interface pointers](#interface-pointers): the other attributes apply to the matching parameters whatever their types, but an element with only `Interfaces` applies to the interface pointers at its point, so a `SetType` that makes a parameter an interface pointer goes before it. |
| `RemoveEnumPrefix` | `Type` (optional) | Removes the prefix shared by the members of the matching enums, or of all enums. |
| `ExcludeType` | `Type` and/or `Kind` | Removes the matching types. References to interfaces, dispinterfaces and coclasses become `IDispatch*` (if they are dispatchable) or `IUnknown*`, references to enums `long`, and pointers to structures `void*`. Structures used by value and base interfaces of kept interfaces cannot be removed. |
| `SetType` | `Type` (optional), `Member`, `Parameter` (optional), `To` | Changes the type of the matching parameters, or without `Parameter` of the members themselves (return values, property values, dispinterface properties, structure fields), to the type named `To` of the library, or of another library that is generated with it, e.g. a `long` that is meant to be an enum. The type is only required when something matches. Parameters passed by reference keep their pointer. Vtable and dual interfaces, functions and structures only accept compatible types: 4-byte integers to enums, interface pointers to interfaces, and structures of the same size; dispinterfaces accept any type. |
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
| `AddAttribute` | `Type` and/or `Kind`, `Member` (optional), `Role` (optional), `Attribute` | Adds a C# attribute (without brackets) to the declarations generated for the matching types or members. `Role` selects which declaration: `Declaration` (default), `Extensions` (the extensions class of an interface), `EventHandler`, `EventSink`, `EventDelegate`, `EventArgs`, `Event`, `Property`, `BackingField`, `InlineArray`, `Enumerator`, `Overload` (the [overloads](#optional-parameters) of a method) or `NamedIndexer` (the proxy of a [named indexer](#named-indexers) and its indexer). Declarations that already have the attribute are not changed. |

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
  `[retval]` parameters to return values, except a `[retval] void*`, which stays a parameter; methods that do not return an
  HRESULT get `[PreserveSig]`. Dual interfaces derive from `IDispatch`, and interfaces derived from `IEnumVARIANT` from
  `IEnumVARIANT`, so that the vtable slots match. An interface without a GUID cannot be generated: the pointers to it are
  `IUnknown` or `IDispatch` pointers, and the interfaces that derive from it get placeholders for its vtable slots.
- **Names:** invalid characters become `_`, and a name that clashes gets a trailing `_`: a member named like another member or
  like its type, and a generated type (`<Interface>Extensions`, the types of the [events](#events)) named like a type of the
  library.
- **Pointer parameters** are `out` (`[out]`), `ref` (`[in, out]`) or, for `[in]` pointers to `VARIANT`s, `SAFEARRAY`s,
  structures and GUIDs, `in` parameters. A pointer without a direction, as old type libraries have them, is a `ref` parameter.
- **Properties** cannot be declared by source-generated COM interfaces, so they are generated as `get_X`, `set_X` (`propput`) and
  `putref_X` (`propputref`) methods. A static `<Interface>Extensions` class adds C# 14 extension properties for them and a
  `GetEnumerator` extension for collections. With C# 15, the default parameterized property (`DISPID_VALUE`, usually `Item`)
  becomes an extension indexer. The extensions of a derived interface also use the accessors of its base interfaces, e.g. the
  getter of a read-only property of `IMyInterface` and the setter that `IMyInterface2` adds to it. The other parameterized
  properties can get indexer syntax too, see [Named indexers](#named-indexers).
- **Dispinterfaces** can only be called through `IDispatch::Invoke`, so they become late-bound wrapper classes with methods,
  properties and indexers that invoke the members by their DISPIDs.
- **Coclasses** become static classes with the `Clsid` and a `Create()` method that returns the default interface;
  `Create(unique: true)` returns it in an object of its own, which can be released at once, see [Interface pointers](#interface-pointers).
  With `--dynamic`, coclasses whose default interface supports `IDispatch` also get `CreateDynamic()`, see [Late binding with `dynamic`](#late-binding-with-dynamic).
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

### By-reference arguments

A pointer parameter is passed by reference, and how depends on how the member is called:

| Parameter | Interface (vtable) | Late-bound wrapper |
| --- | --- | --- |
| `[out] T*` | `out T` | `out T` |
| `[in, out] T*`, or `T*` without a direction | `ref T` | `ref T` |
| `[in] T*` | `in T` for `VARIANT`s, `SAFEARRAY`s, structures and GUIDs, otherwise `ref T` | `T`, passed by value |
| `void*` | `void*` | `nint` |

- **Interfaces** are called through the vtable, so the argument is the address of the variable, marshalled by the COM source
  generator: the callee reads and writes the value in place.
- **Late-bound wrappers** call `IDispatch::Invoke`, which takes each argument in a `VARIANT`. A `ref` or `out` argument is
  passed as a reference of the type of the parameter, e.g. `VT_BYREF | VT_I4` for a `long*`, since the objects that implement
  `Invoke` with their type information reject a reference to a `VARIANT` there. The new value is converted back to the type
  of the parameter after the call.
  - **Typed references:** the integers and floating-point numbers, `CURRENCY`, `DATE`, `BSTR`, `VARIANT_BOOL`, `SCODE`,
    enums (as `VT_I4`), and `IUnknown*`, `IDispatch*` and interface pointers.
  - **References to a `VARIANT`:** the other types, such as `VARIANT`, `DECIMAL` and structures.
  - **`SAFEARRAY`s:** the array is first passed as a reference to a `VARIANT`, in which Visual Basic and script servers store
    an array of `VARIANT`s whatever element type the type library declares. When the object rejects that argument with a type
    mismatch, the call is repeated with a reference to the `SAFEARRAY` of the declared type, e.g. `VT_BYREF | VT_ARRAY | VT_I4`.
    The elements that come back are converted to the element type of the parameter. The second call needs an array whose
    element type is exactly the declared one: an `int[]` is a `SAFEARRAY` of `VT_I4`, so it is not passed that way to a
    parameter declared as `SAFEARRAY(int)` (`VT_INT`) or `SAFEARRAY(unsigned int)`, and neither is an `object[]` to one
    declared as an array of `IDispatch*` or `IUnknown*`. Such a call fails with the type mismatch of the first one.
  - **`out` arguments** start as a zero or a null pointer of the type of the parameter. A value that cannot be converted to
    that type is passed as a reference to a `VARIANT`.
  - **Properties:** an accessor with by-reference parameters is a `get_X`, `set_X` or `putref_X` method of the wrapper, which
    is invoked as a property, with the value of a setter as the `DISPID_PROPERTYPUT` argument.
- **Events of dispinterfaces** arrive through `IDispatch::Invoke` the same way: the `ref` and `out` parameters of a handler
  are written back to the references that the source passed.
- **`dynamic`** has no declared types, so the variable decides, see [Late binding with `dynamic`](#late-binding-with-dynamic).
- **By hand:** `ComDispatch.Invoke` of CsCom.Runtime, for objects that have no generated wrapper, takes a
  `DispatchArgumentKind` per argument: `Value`, `Variant` (a reference to a `VARIANT`) or `Typed` (a reference to a value of
  the type of the argument, e.g. `VT_BYREF | VT_I4` for an `int`). The new values are written to its `output` span, which must
  have an element for each argument; this is checked before the member is invoked.

### Named indexers

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
- **Late-bound wrappers and dispinterface events** in the `array` mode use `T[]?` too, enums included (`Color[]?`), and convert
  the elements, since servers often return other element types, e.g. a `SAFEARRAY` of `VARIANT`s for a `SAFEARRAY(BSTR)`.
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

All marshalling options, including `--bstr-marshalling` and `--safearray-marshalling`, can be changed for individual
methods, parameters, dispinterface properties and structure fields with the `SetMarshalling` element of the
[configuration file](#model-transformers), which also selects the marshaller of interface pointers.

### Interface pointers

The interface pointers of parameters and return values are converted to shared managed objects: one per COM object, which
all the references to it share, like `ComInterfaceMarshaller<T>` of .NET. Its COM references are released when it is
garbage collected. The marshaller is `ComObjectMarshaller<T>` of CsCom.Runtime.

`SetMarshalling` selects unique objects for single members and parameters instead, with `UniqueComObjectMarshaller<T>`:
an object per interface pointer, like `UniqueComInterfaceMarshaller<T>` of .NET, which belongs to whoever receives it and
can be released at once. The pointer of a managed object that is exposed to COM, such as your implementation of a callback
interface, is converted to that object itself, as with shared objects, also with your own `ComWrappers`; releasing it does
nothing. As with shared objects, that is the pointer that the instance of the support code has created: the pointer that
another `ComWrappers` has created for a managed object is called through COM, like a COM object:

```xml
<SetMarshalling TypeName="IWorkbooks" MemberName="Open" Interfaces="unique" />
```

To tell the pointer of a managed object that the instance of the support code has created from one of another
`ComWrappers`, a unique conversion compares it with the interface that the instance has for the object, and the instance
creates that interface when it has none yet, e.g. when only another `ComWrappers` has exposed the object: the object
then keeps that unused interface for as long as it lives, since .NET cannot ask an instance for its interface without
creating it. An instance whose strategy cannot expose the object leaves the pointer to COM, as one of another
`ComWrappers`. This costs nothing for the pointers of COM objects and for shared objects, which `ComWrappers` unwraps
itself.

It applies to the parameters and return values of vtable and dual interfaces and of the functions of modules, whether
their type is an interface, a late-bound dispinterface wrapper, `IDispatch` or `IUnknown`. It does not apply to:

- a pointer that is passed in and out (`[in, out]`, a `ref` parameter), which is passed back to its caller;
- dispinterfaces and structures: the arguments and results of late-bound wrappers and of `dynamic` calls, and the objects
  in `VARIANT`s and `SAFEARRAY`s are always shared objects, and the pointers in the fields of structures are not converted.

A handler of a dual event interface gets a unique object whichever way the event is raised: through the vtable, or through
`IDispatch::Invoke`, whose `VARIANT` holds the object. A hand-written `IDispatchEventHandler` reads such an argument with
`EventArguments.GetUnique<T>` or `GetUniqueWrapper<T>`.

Nothing releases a unique object for you: the code that receives it, such as your implementation of a callback interface,
releases it, or leaves it to the garbage collector. The handlers of an event share its unique arguments: every handler
of the event of a generated sink, and every observer of an observable, gets the same object, so release it only where
no other handler uses it, or leave it to the garbage collector; `EventArguments.GetUnique<T>` of a hand-written
`IDispatchEventHandler` creates an object per call. A closed event sink does not release the unique arguments of the
events that it no longer passes on, because it cannot tell them from objects that a managed source in the same process
passes to it directly: they are left to the garbage collector.

**Unique objects** can be released without waiting for the garbage collector, with `ComMarshalling.Release` of the
support code or `ComObject.FinalRelease()` of .NET; a late-bound wrapper has its object in `Dispatch`, which
`ComMarshalling.Release` releases for a wrapper, also one of your own that implements `IDispatchWrapper<T>`, which is an
`IDispatchProvider`. It leaves a managed object that is exposed to COM (`[GeneratedComClass]`) alone, also when it
implements `IDispatchProvider`; `ComMarshalling.IsExposedToCom` tells whether an object is one. It releases the object
of an `UnknownWrapper`, and rejects a `DispatchWrapper`, as where an object is passed to COM. Release an object only
when no other thread uses it: a call that passes it to a COM object at the same time can use its interface pointer after
the release.

```csharp
IDictionary dictionary = Dictionary.Create(unique: true);  // or ComActivator.CreateInstance<IDictionary>(clsid, unique: true)
ComMarshalling.Release(dictionary);                        // the object must not be used afterwards
((ComObject)(object)dictionary).FinalRelease();            // the same
```

All the activation methods of `ComActivator` have an overload with the `unique` parameter: `CreateInstance<T>`,
`CreateDispatchInstance<T>`, `GetActiveInstance<T>`, `TryGetActiveInstance<T>` and `BindToMoniker<T>`; without it the
object is shared. Only the object itself is
unique: the objects that its members return are shared ones, unless `SetMarshalling` makes them unique too. A unique
object does not compare equal to another object of the same COM object, and releasing does nothing for shared objects.
The support code does not keep a unique object alive: when `SetMarshalling` makes `_NewEnum` unique, `foreach` over a
COM object of the generated interface releases the enumerator when it ends, also the object of a late-bound wrapper when
`_NewEnum` returns a dispinterface (one that your own implementation of the interface returns stays yours, and so does a
wrapper of yours that is exposed to COM, whose pointer the collection returns), and a disconnected event sink holds no
reference to its source: one whose disconnection failed with a transient error keeps it until another disconnection
succeeds or gives up. `IConnectionPointContainer.FindConnectionPoint` of the support code returns the connection point
of a COM object in a unique object too, which belongs to the caller: release it with `ComMarshalling.Release`. To
enumerate a unique enumerator yourself, use `new ComEnumerator(enumerator, ownsEnumerator: true)` or
`ComEnumerator.FromObject(value, ownsEnumerator: true)`: they release it when they are disposed, and `FromObject` also
when it does not implement `IEnumVARIANT`. `FromObject` takes what the value stands for, as `ComMarshalling.Release`
does: the COM object of a late-bound wrapper, which it releases, and an object of yours that is exposed to COM, also in
a late-bound wrapper, itself or, when it is no enumerator, the `Dispatch` that it provides, which it never releases.
Without `ownsEnumerator`, disposing them releases nothing.

**Your own `StrategyBasedComWrappers`** creates the managed objects when it is set at startup, before the first COM object
is created. Once the support code has converted an interface pointer, setting another instance throws an
`InvalidOperationException`; setting the same one again does not:

```csharp
ComMarshalling.ComWrappers = new MyComWrappers();          // class MyComWrappers : StrategyBasedComWrappers
```

All the generated code and the support code use it then, for shared and for unique objects: the interface pointers of the
generated interfaces, `ComActivator`, the `VARIANT` and `SAFEARRAY` marshallers, the late-bound wrappers, the `dynamic`
objects and the event sinks. Without it they use the instance of .NET. Hand-written COM interfaces whose interface pointers
are marshalled by `ComInterfaceMarshaller<T>` of .NET, which is the default of `[GeneratedComInterface]`, do not use it:
give their parameters `[MarshalUsing(typeof(ComObjectMarshaller<T>))]`, otherwise a COM object gets two managed objects,
or `UniqueComObjectMarshaller<T>` for unique objects. A late-bound wrapper is marshalled by the `DispatchWrapperMarshaller<T>`
of its `[NativeMarshalling]` attribute, and as a unique object by `UniqueDispatchWrapperMarshaller<T>`.
A managed object in a `VARIANT` is exposed to COM when its class is a `[GeneratedComClass]`, with or without your own
instance; pass an object that only your instance can expose in an `UnknownWrapper`, unless it implements
`IDispatchProvider`: such an object is a late-bound wrapper wherever it goes to COM, also in an `UnknownWrapper`. An
object that is exposed to COM is passed itself, also when it implements `IDispatchProvider`, in a `VARIANT`, a
`SAFEARRAY` and an `IUnknown` parameter alike, and as a parameter of its own type when it is a late-bound wrapper that
implements `IDispatch`, so that its pointer converts back to it; a late-bound or `dynamic` wrapper is passed as its COM
object there, and in a parameter of a COM interface too, also one that a wrapper of yours implements itself, e.g.
`IDispatch` by forwarding: its COM object is asked for the interface. The pointer converts back to the object for
an interface that it implements; for another declaration of the interface, e.g. of another interop assembly, it gets a
COM object of its own, which calls the object through COM, where `ComInterfaceMarshaller<T>` of .NET throws an
`InvalidCastException`: a new one for each conversion, also of a shared parameter, which is not shared, so that
`ComMarshalling.Release` releases it. The object comes back itself from a `VARIANT` or a `SAFEARRAY`, and where a
late-bound wrapper, `EventArguments.Get<T>` or an array converts it to an interface that its class does not implement,
it is asked through its COM interface the same way, e.g. for another declaration of the interface; when that does not
answer, the COM object of its `Dispatch` is asked for the interface, as when the `VARIANT` held that object. Only
where an interface is required that it does not implement, `IDispatch` in a `SAFEARRAY` of `IDispatch`, in a
dispinterface wrapper and for `dynamic`, and `IEnumVARIANT` in `ComEnumerator.FromObject`, it stands for the COM object
of its `Dispatch`, like the source of events. A late-bound or `dynamic` wrapper of such an object, e.g. the one that a
dispinterface wrapper makes of it when a call returns it, stands for it with this rule, also as the source of events,
in `ComEnumerator.FromObject`, where a late-bound wrapper converts it to an interface, and in a parameter of a COM
interface that the wrapper implements. A COM object that fails `QueryInterface` with another error than
`E_NOINTERFACE`, e.g. the proxy of a server that has ended, throws the exception of its `HRESULT`, also in
`ComEnumerator.FromObject`, rather than an `InvalidCastException`. An `UnknownWrapper` stands
for its object wherever an object goes to COM: in an `IUnknown` parameter, a `SAFEARRAY`, as the source of events, for
`ComDynamic` and in `ComEnumerator.FromObject` as in a `VARIANT`, where it is passed as `VT_UNKNOWN`, and
`new UnknownWrapper(null)` passes a null object, where `null` passes an empty `VARIANT`. A `DispatchWrapper` is
rejected everywhere with an `ArgumentException`, as by
`ComVariantMarshaller` of .NET in a `VARIANT`: the interface of such a wrapper itself would only have `IUnknown`. The
interfaces must be `[GeneratedComInterface]`s also when your instance has a strategy of its own for the details of
interfaces: the marshallers and `ComActivator` take their identifiers from the generated attributes, and fail with a
`NotSupportedException` for another interface, `ComActivator` also for a class other than `object` and `ComObject`,
e.g. a late-bound wrapper, which `CreateDispatchInstance` creates, and before anything else: before it looks up a
ProgID, creates or gets the object. Such a strategy must give the details of `IDispatch` of the support code too, which
the late-bound wrappers, the `dynamic` objects and the events call: a `VARIANT` trusts the strategy, so that a COM
object without `IDispatch` is asked for it only once, and passes an object whose strategy has none as `VT_UNKNOWN`;
only a `SAFEARRAY` of `IDispatch` asks the object itself.
With the `public` and `internal` support code, every generated project has its own `ComMarshalling`.

### Optional parameters

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

The COM objects created by source-generated COM interop do not support `dynamic` by themselves. The **CsCom.Runtime.Dynamic**
package provides `DynamicDispatchObject`, which resolves members by name with `IDispatch::GetIDsOfNames` and invokes them with
`IDispatch::Invoke`. It is a separate package, because the C# runtime binder needs dynamic code generation, which the typed
interop of CsCom.Runtime avoids:

- **Opting in:** `--dynamic` (`-d`), or the `CsComDynamic` property of [CsCom.Build](#msbuild-integration), generates
  `CreateDynamic()` for the coclasses. With `--support-code package` the generated project references CsCom.Runtime.Dynamic,
  and with `public` or `internal` its code is emitted into the project. Without the option, neither is.
- **Using it directly:** the package can also be referenced on its own, e.g. by a project that only automates Office with
  `dynamic`. The activation methods are static extension members of `ComActivator` (C# 14), in `ComDynamic`:

```csharp
dynamic fso = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject"); // by ProgID or CLSID, no interop types needed
dynamic dictionary = Dictionary.CreateDynamic();   // generated with --dynamic for coclasses that support IDispatch
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

- **Returned objects:** COM objects returned by dynamic calls are dynamic as well, so calls can be chained. There is one
  dynamic object per COM object, so `==` compares COM objects (`if (sheet == excel.ActiveSheet)`), like the runtime
  callable wrappers of the built-in COM interop. The dynamic objects are attached to the managed objects of the COM
  objects and do not keep them alive. An object of yours that is exposed to COM (`[GeneratedComClass]`), which a COM
  object returns as itself, is made dynamic like by `ComDynamic.ToDynamic`, also in events and `foreach`: itself when it
  implements `IDispatch`, and otherwise the COM object of its `Dispatch`; one that has neither is returned as it is.
- **Properties:** assigning an object to a property or an indexer uses `PROPERTYPUTREF`, like VB's `Set`, and a property
  put when the object has no such setter: a COM object, a late-bound or `dynamic` wrapper, an object of yours that is
  exposed to COM (`[GeneratedComClass]`) or an `UnknownWrapper`, i.e. every value that is passed as an object. A property
  that requires arguments, or can only be set, can be indexed with its arguments: `dictionary.Item["a"]`,
  `dictionary.Key["a"] = "b"`. So can a property whose parameters are all optional, such as
  Excel's `Range.Value`, when the object has type information: `range.Value[xlRangeValueMSPersistXML]`. Without type
  information such a property is read at once and brackets index its value; pass the arguments in parentheses instead.
- **Methods:** a method that is read without parentheses is not invoked when the type information of the object describes it
  as a method: `var quit = app.Quit;` gives a value to call, `quit()`. Without type information, reading a member invokes it.
- **Named arguments:** they work in invocations and indexes, e.g. `sheet.Cells[ColumnIndex: 2, RowIndex: 1]`. Those of the
  default member (`d[index: 1]`, `d(index: 1)`) need the type information of the object, which gives the name of the member;
  without it, or when it describes no default member, they throw `NotSupportedException`.
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
  of the object. An object of yours that is exposed to COM (`[GeneratedComClass]`) is late bound itself when it implements
  `IDispatch`, its events included, and otherwise through the COM object of its `Dispatch`. A handler is a delegate with at
  most the parameters of the event, so a lambda needs a cast. Its `ref` and `out` parameters and its result are returned to
  the COM object:

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

  Adding and removing handlers is safe from several threads and from within a handler, and never waits for another
  thread:

  - `+=` returns only when the object is connected, and throws without adding the handler when it cannot be connected;
    a connection error is reported by the `+=` that caused it.
  - `-=` and `RemoveEventHandlers` never fail. A removed handler gets no more events, also when the object cannot be
    disconnected at the moment, e.g. because the server is busy or the call comes from another apartment: the connection
    then passes no events on and is disconnected again by the next `+=`, `-=` or `RemoveEventHandlers` of that object.
    Any other error than the transient ones listed in [Events](#events) gives the connection up, also the exception of a
    connection point that is a managed object, which counts as its `HRESULT`: the source keeps the closed sink until it
    releases it.
  - `+=` and `-=` of the same handler on several threads take effect one after another, each at some moment between its
    call and its return, as for a .NET event: a `-=` that runs while a `+=` on another thread has not returned yet can
    come before it, and then leaves the handler of that `+=`. A `+=` that fails adds nothing, also when a `-=` removed
    its handler meanwhile.
  - Each event reaches each handler once, with the limitations below: when several threads connect the object at the same
    time, only one connection passes the events on, and the others are disconnected.

  Limitations: an event that is running on another thread can still call a handler after `-=` returns, if it had reached
  the handler before; it does not call one that was removed before it reached it. When several threads connect the first
  handlers at the same time, an event that a multi-threaded object raises meanwhile can be missed, and when a connection
  that has already received events then fails to connect, the events around the switch to another connection can be
  missed or doubled.
- **Releasing objects:** the COM references of the `dynamic` objects are released when the objects are garbage collected, and finalizers do not run
  when the process exits. An Office application exits after `Quit()` only when all its objects are released, so let them go
  out of scope and call `GC.Collect()` and `GC.WaitForPendingFinalizers()`, otherwise it keeps running for minutes. This
  applies to the objects of the generated interfaces and late-bound wrappers too, see [Limitations](#limitations).
- **Debugger:** the dynamic view of the debugger lists the properties described by the type information of the object, and
  `ComDynamic.GetTypeName` returns the name of its type, like VB's `TypeName`.
- **Errors:** unknown members throw `RuntimeBinderException`. Errors raised by the COM object throw `COMException`, and so do
  failed invocations, with messages that name the member and the argument, e.g. "Type mismatch in argument 2 of 'BuildPath'."
  As in the late-bound wrappers, an error that only has a 16-bit code, as Basic raises them, has the `HRESULT` that Visual
  Basic gives it: `0x800A0000` plus the code, e.g. `0x800A000B` for the error 11.

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

With `interface`, `<Interface>Sink` is a static class with only `Connect`, and `none` generates no event code. An event
that the source raises while the sink connects, e.g. its state during `IConnectionPoint::Advise`, reaches no handler or
observer with `events` and `observable`, since they can only subscribe once the sink has been created; a handler passed
to `Connect` receives it, but gets the connection only after `Advise` has returned. The observables are hot: events are
not buffered, and disposing the sink completes them, once no handler runs any more. When no handler runs, `Dispose`
completes them at once. It does not wait for a running handler, on another thread or the one that disposes the sink: the
handler completes them itself when it returns, on its thread and in the event call of the source, like the values that
it pushes. So `OnCompleted` has the constraints of `OnNext`, and an exception of an observer fails that event call like
one of `OnNext`. Unlike the exception of `OnNext`, it stops neither the other observers nor the other observables, since
an observer that missed the completion would never be completed: once all of them are completed, it is thrown as it is,
or in an `AggregateException` when several observers threw. `DisposeAsync` completes them in the synchronization context
of the caller, and throws the exceptions of the observers to it. The observables are plain `IObservable<T>`s, so they
work with Reactive Extensions without depending on it.

An event named like a member of the sink class or of `object`, e.g. `Dispose`, `DisposeAsync`, `Connect`, `Iid` or
`Equals`, gets the suffix `Event` for its .NET event or observable, e.g. `DisposeAsyncEvent`; the method of the handler
interface keeps its name. The sink class is partial and implements `IDisposable` and `IAsyncDisposable`, so a part of your
own cannot declare `Dispose` or `DisposeAsync`.

All kinds of event interfaces are supported:

- **Dispinterfaces**, the most common ones (Office, VB6 and ATL components), are received by `DispatchEventSink` of the support
  code, which implements `IDispatch` and answers `QueryInterface` for the dispinterface. The arguments of `IDispatch::Invoke` are
  converted to the late-bound types, as in the late-bound wrappers, and `ref` and `out` arguments and results are passed back.
- **Vtable interfaces** are implemented by a generated `[GeneratedComClass]` sink with the signatures of the generated interface.
- **Dual interfaces** are implemented in the same way, and the sink also implements `IDispatch::Invoke`, which event sources often use.

The source can be any object returned by the generated code: a COM interface, a late-bound wrapper or a `dynamic` object.
An object of yours that is exposed to COM (`[GeneratedComClass]`), also in a late-bound or `dynamic` wrapper, raises
the events itself, or else, when it implements `IDispatchProvider`, the COM object in its `Dispatch` does, e.g. for a
decorator of a COM object. A source that does not raise the events of the interface fails with an
`InvalidOperationException`: it does not implement `IConnectionPointContainer`, or has no connection point of the
interface. Any other error of the source, e.g. of a server that has ended or is busy (`RPC_E_CALL_REJECTED`), throws the
exception of its `HRESULT`, a `COMException`, so that a connection to a busy server can be tried again.
`EventConnection.Advise` and `EventConnection.AdviseDispatch` of the support code connect hand-written sinks.

Events are delivered on the threads chosen by COM: a single-threaded apartment (STA) object, such as an Office application,
delivers them through the message loop of the thread that connected the sink. Dispose the sink on that thread, because
`IConnectionPoint::Unadvise` must be called from the same apartment; there is no finalizer that disconnects it.
`EventConnection.Disconnect` returns the `HRESULT` of the disconnection and never throws: the exception of a connection
point that is a managed object is returned as its `HRESULT`, as a call through COM would return it. A disconnection that
can succeed later leaves the connection connected (`EventConnection.IsConnected` stays `true`), and another call of
`Disconnect` or `Dispose` retries it. These transient errors are `RPC_E_WRONG_THREAD` from another thread than the one
that connected the sink, `RPC_E_CALL_REJECTED` and `RPC_E_SERVERCALL_RETRYLATER` from a busy server, and
`RPC_E_CANTCALLOUT_INASYNCCALL`, `RPC_E_CANTCALLOUT_INEXTERNALCALL` and `RPC_E_CANTCALLOUT_ININPUTSYNCCALL`. Any other error
gives the connection up, and once the connection is over, `Disconnect` returns the `HRESULT` that ended it: `S_OK`, or the
error that gave it up. `Disconnect` does not wait for another call that is disconnecting the same sink at the moment, but
returns `S_FALSE` at once; it returns any success of `Unadvise` as `S_OK`, so that `S_FALSE` means nothing else. An
exception thrown by a handler is returned to the source as `DISP_E_EXCEPTION` with the error information, whose code is
`E_FAIL` when the `HResult` of the exception is no error code, e.g. `S_FALSE`, and without its description when its
`Message` throws (or as the `HRESULT` for vtable calls), and most sources ignore it.

Every connection has an `EventGate` of its own, which it closes first when it is disconnected, and when it cannot be
established. Once the gate is closed, no handler call starts, also when the source could not be told and still calls the
sink: the sink accepts the events without passing them on, with default results. A call that started before still
completes, since COM does not wait for it either; `DisposeAsync` of the connection or of a generated sink waits for it:

```csharp
await using var events = new AppEventsSink(app);  // DisposeAsync completes when no handler runs any more
```

Like `Dispose`, `DisposeAsync` must run on the thread that connected the sink when the source is a single-threaded apartment
object, e.g. Office. An `await` before the end of the scope can resume on another thread, e.g. on the thread pool of a
console application, and the disconnection then fails with `RPC_E_WRONG_THREAD` and leaves the sink connected. Unlike
`Dispose`, `DisposeAsync` reports a transient error, which leaves the sink connected: once no handler runs any more, it
throws a `COMException` with the `HRESULT` if the sink is still connected then, and a generated sink of the observable
style completes its observables first. Another call can have disconnected the sink in the meantime, e.g. `Dispose` on
the right thread: `DisposeAsync` then completes, and it waits for a call that is disconnecting the sink at that moment.
When an observer throws too, the sink throws both in an `AggregateException`, the `COMException` first, so that an observer
cannot hide it. Dispose the sink again then, e.g. on the right thread; the exception of `await using` replaces an exception
of its body.
When another call is disconnecting the sink at the moment, `DisposeAsync` waits for it without blocking, and reports its
error the same way, also when `DisposeAsync` is called on the right thread: it does not disconnect the sink again, which
it would have to do on the thread of its caller, while that thread may be blocked waiting for it. Dispose the sink again
on that thread then.
Use `await using` where the code resumes on the same thread, e.g. in the synchronization context of a user interface
thread, and otherwise call `Dispose` on that thread.

Do not block on `DisposeAsync` in a handler of the same connection, since it waits for that handler too, nor in an
observer's `OnCompleted`. With the observable style, `DisposeAsync` completes the observables once no handler runs any more,
in the synchronization context of the caller, e.g. on the thread of a user interface. So do not block on it (`Wait()`,
`Result`) on a thread with a synchronization context either: when a handler is running, the completion waits for that
thread, which waits for the completion. Await it there, or call `Dispose`, which does not wait.

`EventConnection.DisposeWithCompletion(completion)` disconnects like `Dispose`, and calls the completion once no handler
call is running any more, without waiting for it and without a synchronization context: at once when none runs, otherwise
on the thread of the last running call, when its handler has returned. Its exception is thrown to the caller in the first
case, and fails the event call like an exception of a handler in the second. `Dispose` of the observable style uses it.
Every call adds its completion: they run one at a time, in the order of the calls. An exception does not stop the
others, which other calls may have added: once all of them ran, the exception of the one that failed is thrown, or an
`AggregateException` of them when several failed. A completion that is added while others run, also by one of them, runs
after them on their thread, and its exception is that thread's. A sink that was passed to `Advise` itself has no gate, so
its completion runs at once. `DisposeAsync` waits for the completions that the last running handler call runs, but not
for one that runs at once on another thread: when `Dispose` of the observable style and `DisposeAsync` run at the same
time on two threads, `DisposeAsync` can complete while the other thread still completes the observers.

The generated sinks and those of `AdviseDispatch` use the gate. A hand-written sink gets it when `EventConnection.Advise`
creates the sink for the connection, and passes an event to its handler between `TryEnter` and `Exit`. Call `Exit` exactly
once after each `TryEnter` that returned `true`, and never after one that returned `false`: the gate counts the running
events, so an `Exit` too many while another event runs counts that event as finished, and `DisposeAsync` can complete while
its handler still runs; one when no event runs throws an `InvalidOperationException`. The last `Exit` of a closed gate runs
the completions of `DisposeWithCompletion`, and throws their exceptions as above: handle them like an exception of the
handler. `EventGate.IsClosed` tells whether the gate is closed, which it is from the start of the first disconnection, also
one that leaves the sink connected.

```csharp
using var connection = EventConnection.Advise(source, iid, gate => new ChangeSink(handler, gate));

// in ChangeSink, for every event:
if (!gate.TryEnter()) return;   // the connection is disconnected
try { handler.Changed(); } finally { gate.Exit(); }
```

A sink that also implements `IDispatch`, e.g. for a dual event interface, passes the invocations of `IDispatch::Invoke` to
an `IDispatchEventHandler` with `DispatchEventSink.InvokeHandler(gate, handler, …)`, which enters and exits the gate,
accepts the invocation with an empty result when it is closed, whatever its `DISPID`, since it does not ask the handler any
more, and returns the exception of a completion like one of the handler. The exception of a completion replaces the result
of the handler, also its error, as a `finally` block would, and as the generated sinks do.

A sink that is passed to `Advise` itself has no gate: it gets events until the source is told, and `DisposeAsync` does not
wait for its handler calls.

### Support code

The generated code depends on a few support types: `IDispatch`, `IEnumVARIANT`, `ComActivator`, `ComMarshalling`, the `VARIANT`, `SAFEARRAY`, `BSTR`, `DATE` and `CURRENCY` marshallers, the
base class of the late-bound wrappers and the event sinks; with `--dynamic`, also the `dynamic` support. With `--support-code package`, the generated project references the **CsCom.Runtime**
NuGet package, and **CsCom.Runtime.Dynamic** with `--dynamic`. With `public` or `internal`, the support code is emitted into the project, in the `<RootNamespace>.ComSupport`
namespace: under the namespace of a single source, or the root namespace of several sources, see [Several sources](#several-sources). With
CsCom.Build, it is emitted into `CsComSupportNamespace`, `$(RootNamespace).ComSupport` by default.
In the `internal` mode all generated types are internal, because a public interface cannot derive from an internal `IDispatch`.

The generated projects disable runtime marshalling, which source-generated COM interop requires to marshal `VARIANT`s and `DECIMAL`s.

### Limitations

- Events of the generated interfaces are connected through the sink classes rather than through events of the COM interfaces as
  with TlbImp (`app.WorkbookOpen += ...`), because C# has no extension events; `dynamic` objects support `+=` for the events
  of dispinterfaces.
- By default COM objects cannot be released manually: the generated interfaces, the late-bound wrappers and the `dynamic`
  objects share one wrapper per COM object, whose references are released when it is garbage collected, and
  `ComObject.FinalRelease()` of .NET only releases unique wrappers, which `Create(unique: true)` and the `unique`
  [marshaller of `SetMarshalling`](#interface-pointers) create; `dynamic` objects, and the objects in `VARIANT`s and `SAFEARRAY`s
  are always shared. Finalizers do not run when the process exits, and an
  out-of-process server such as an Office application only exits after `Quit()` once all its objects are released, not only
  the application object: let them go out of scope and call `GC.Collect()` and `GC.WaitForPendingFinalizers()`.
  `ComBstr`, `ComSafeArray<T>` and the event sinks are disposable and are released at once.
- Structures keep the layout of the COM structures, so `VARIANT`, `SAFEARRAY` and interface pointer members are raw values, and
  `BSTR` members are `ComBstrValue` views that the code using the structure allocates and frees; only `DATE`, `CURRENCY` and
  `VARIANT_BOOL` members are converted.
- `SAFEARRAY`s are converted to zero-based .NET arrays of up to three dimensions, and `T[,]`/`T[,,]` require a
  [known rank](#known-ranks); only `ComSafeArray<T>` keeps their bounds and supports more dimensions.
- The types used from other type libraries are generated into the project, in the namespaces of their libraries; referencing
  other interop assemblies that contain them is not supported yet.
- Interop assemblies lack the documentation, the functions of modules and some type information of their type libraries, see
  [Interop assemblies](#interop-assemblies).

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
| `src/CsCom` | The command line tool (`cscom`), packed as a .NET tool. |
| `src/CsCom.Build` | The CsCom.Build package: the MSBuild props, targets and task (netstandard2.0), which runs the cscom tool bundled in its `tools` folder. |
| `src/CsCom.Core` | The pipeline. |
| `src/CsCom.Runtime` | The support code used by the generated code, built for `net10.0` and `net11.0`: source-generated COM expects a base interface from another assembly (`IDispatch`) to be compiled for the same target framework (SYSLIB1230). Its sources are also embedded into CsCom.Core for the `public` and `internal` modes. |
| `src/CsCom.Runtime.Dynamic` | The late binding with `dynamic`, which only uses the public API of CsCom.Runtime (e.g. `ComDispatch`), for the same target frameworks. Its sources are embedded like those of CsCom.Runtime, for `--dynamic`. |
| `src/CsCom.Tests` | MSTest and Verify tests, for `net10.0-windows` and `net11.0-windows`. |
| `src/CsCom.Benchmarks` | BenchmarkDotNet benchmarks that compare CsCom with the built-in COM interop, see [Benchmarks](#benchmarks). |
| `src/CsCom.Benchmarks.Interop` | The interop code of `scrrun.dll` that the benchmarks call, generated during the build by the task and the tool of the working tree. |

## Development

```powershell
dotnet build src/CsCom.slnx
dotnet test src/CsCom.slnx --no-build --filter "TestCategory!=Integration"  # without the tests that build generated projects
dotnet pack src/CsCom.slnx --no-build                                       # the packages, in artifacts/packages
dotnet test src/CsCom.slnx --no-build                                       # all tests, which need the packages
```

- The projects use central package management: the package versions are in `src/Directory.Packages.props`.
- Building requires the .NET 11 SDK, and the tests also need the .NET 10 runtime. The tests run on .NET 10 and .NET 11, and
  the integration tests generate, build and run the projects for the version they run on; `-f net11.0-windows` runs the tests
  for one version.
- The integration tests restore CsCom.Runtime, CsCom.Runtime.Dynamic and CsCom.Build from `artifacts/packages`, which only
  `dotnet pack` updates: pack again after changing them (or the tool, which CsCom.Build bundles). The tests restore them into a
  temporary global packages folder; the tests of CsCom.Build also build with the 32-bit and 64-bit MSBuild of Visual Studio
  when it is installed. When using the packages from source, clear `%USERPROFILE%\.nuget\packages\cscom.runtime`,
  `cscom.runtime.dynamic` and `cscom.build` after changing them without changing the version.
- `assembly build` and `assembly pack` build with CsCom.Runtime (and CsCom.Runtime.Dynamic with `--dynamic`) of the version of
  the tool, and fail rather than use a later version when that one is not available (NU1603 is an error in their temporary
  project). When running the tool from source, pass `--package-source artifacts\packages`, since the version built from source
  is usually not on nuget.org.
- The test type libraries `src/CsCom.Tests/TestAssets/CsComTest.tlb` and `CsComDrawing.tlb` are built from their `.idl` files
  with `build-tlb.ps1`, which requires Visual Studio with the C++ tools. The `.tlb` files are committed.
- The snapshots of the generated code are in `src/CsCom.Tests/Snapshots`. Verify.MSTest is pinned to 32.0.0, the last version
  without the sponsorship check.
- The memory leak tests (`MemoryLeakTests`) check the runtime support with `LeakDetector.AssertNoLeaks`, which runs an action
  several times and fails with the stacks of the allocations if a `BSTR`, a `SAFEARRAY` or other memory of the COM task
  allocator is not freed, or if the reference count of a COM object changes. It registers an `IMallocSpy` for the test process
  and disables the `BSTR` cache of OLE Automation.
- [`docs/known-issues.md`](docs/known-issues.md) lists the known issues that are not fixed, with the reasons why and
  what a fix would take.

### Benchmarks

`src/CsCom.Benchmarks` measures calls of `Scripting.Dictionary` and `Scripting.FileSystemObject`, in-process COM objects of
every Windows installation that do little work themselves, with CsCom and with the built-in COM interop of .NET, which is
the baseline of each call:

```powershell
dotnet run -c Release -f net11.0-windows --project src/CsCom.Benchmarks -- --filter *                    # all benchmarks
dotnet run -c Release -f net10.0-windows --project src/CsCom.Benchmarks -- --filter *StaticDispatch*     # one class, on .NET 10
dotnet run -c Release -f net11.0-windows --project src/CsCom.Benchmarks -- --filter *GetItem* --job Short
```

| Class | CsCom | Built-in COM interop |
| --- | --- | --- |
| `StaticDispatchBenchmarks` | The generated `[GeneratedComInterface]` interfaces, called through the vtable. | `[ComImport]` dual interfaces, as TlbImp generates them. |
| `DynamicDispatchBenchmarks` | Late-bound wrappers (`DispatchObject`), which invoke the members by their DISPIDs through `IDispatch::Invoke`. | `[ComImport]` dispinterfaces (`InterfaceIsIDispatch`). |
| `DynamicKeywordBenchmarks` | C# `dynamic` with CsCom.Runtime.Dynamic. | C# `dynamic` with the COM binder of the runtime. |

- **Calls:** a property without arguments (`Count`), `VARIANT` arguments and results (`Exists`, `GetItem`, `SetItem`), `BSTR`
  arguments and results (`BuildPath`) and the enumeration of 100 keys with `IEnumVARIANT` (`Enumerate`). All classes have the
  memory diagnoser, so the results include the managed memory allocated by a call.
- **Code under test:** `src/CsCom.Benchmarks.Interop` imports the props and targets of CsCom.Build from the working tree and
  generates the code of `scrrun.dll` with the task and the tool built from it, and the benchmarks reference CsCom.Runtime and
  CsCom.Runtime.Dynamic as projects. It is a separate assembly, because the generated code disables runtime marshalling for
  its assembly, which the `[ComImport]` declarations need. The Scripting Runtime has no dispinterfaces, so the late-bound
  wrappers are written by hand like the generated ones.
- **Apartment:** `Scripting.Dictionary` is apartment-threaded, so the benchmark methods are marked with `[STAThread]`, which
  makes BenchmarkDotNet run them on a single-threaded apartment thread; the setup fails otherwise, because each call would
  be marshalled to another thread.
- BenchmarkDotNet is a preview version, 0.16, because 0.15.8 does not run on .NET 11. The results are written to
  `BenchmarkDotNet.Artifacts`, which is not committed.

The results of a run on .NET 11.0.0 (X64 RyuJIT, x86-64-v3), Windows 11, 13th Gen Intel Core i7-1360P, with
BenchmarkDotNet 0.16.0-preview.2 and its default job. The ratio is the mean of CsCom divided by the mean of the built-in COM
interop, so a ratio below 1 means that CsCom is faster; the memory is the managed memory allocated by a call:

| Class | Call | Built-in | CsCom | Ratio | Built-in memory | CsCom memory |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `StaticDispatchBenchmarks` | `Count` | 10.51 ns | 11.40 ns | 1.08 | - | - |
| | `Exists` | 57.09 ns | 58.08 ns | 1.02 | - | - |
| | `GetItem` | 89.74 ns | 80.26 ns | 0.89 | 24 B | 24 B |
| | `SetItem` | 89.32 ns | 103.27 ns | 1.16 | - | - |
| | `BuildPath` | 102.60 ns | 98.51 ns | 0.96 | 104 B | 104 B |
| | `Enumerate` | 17,813.52 ns | 8,340.56 ns | 0.47 | 3608 B | 4294 B |
| `DynamicDispatchBenchmarks` | `Count` | 377.26 ns | 247.21 ns | 0.66 | 96 B | 48 B |
| | `Exists` | 556.54 ns | 276.92 ns | 0.50 | 184 B | 72 B |
| | `GetItem` | 569.01 ns | 287.99 ns | 0.51 | 184 B | 72 B |
| | `SetItem` | 563.17 ns | 345.21 ns | 0.61 | 104 B | 112 B |
| | `BuildPath` | 979.97 ns | 532.81 ns | 0.54 | 360 B | 176 B |
| | `Enumerate` | 17,696.97 ns | 8,555.24 ns | 0.48 | 3584 B | 4319 B |
| `DynamicKeywordBenchmarks` | `Count` | 245.20 ns | 275.13 ns | 1.12 | 24 B | 48 B |
| | `Exists` | 351.89 ns | 305.35 ns | 0.87 | 48 B | 104 B |
| | `GetItem` | 358.68 ns | 304.65 ns | 0.85 | 48 B | 104 B |
| | `SetItem` | 371.90 ns | 368.12 ns | 0.99 | 24 B | 144 B |
| | `BuildPath` | 624.52 ns | 578.28 ns | 0.93 | 152 B | 216 B |
| | `Enumerate` | 20,456.70 ns | 8,880.83 ns | 0.43 | 3536 B | 4370 B |

- **Static dispatch** costs about the same with both, within 16 % for a single call.
- **Late-bound wrappers** take half to two thirds of the time of the dispinterfaces of the built-in COM interop.
- **`dynamic`** is within 15 % for a single call, and CsCom allocates more memory, from 1.4 times (`BuildPath`) to six times (`SetItem`).
- **Enumerating** takes about half the time with CsCom in all three, with about 20 % more memory.

### CI and releases

The [CI workflow](.github/workflows/ci.yml) builds the solution in Release on Windows, runs the unit tests (without the
`Integration` category) on .NET 10 and .NET 11 and then packs the four packages from the build output for every push and pull
request; the packages are kept as the `packages` artifact of the run. The changes of each version are described in
[CHANGELOG.md](CHANGELOG.md).

- **Releasing:** push a tag with a semantic version, e.g. `1.2.3` or `1.2.3-beta.1`. The tag sets the version of CsCom,
  CsCom.Runtime, CsCom.Runtime.Dynamic and CsCom.Build (instead of the `VersionPrefix` of `src/Directory.Build.props`). The
  build of a tag also runs the tests of the CsCom.Build package on the packed packages, and the `publish` job only pushes them
  to nuget.org when they pass. A tag that is not a semantic version fails the build.
- **Symbols:** CsCom.Runtime and CsCom.Runtime.Dynamic also have symbols packages (`.snupkg`), which `dotnet nuget push` publishes to the symbol server
  of nuget.org together with the package. With Source Link, debuggers get the sources of the exact commit from GitHub.
- **Reproducible builds:** the builds are deterministic, and on GitHub Actions `ContinuousIntegrationBuild` maps the source
  paths to `/_/`, so the same commit and SDK produce identical assemblies. The packages record the commit they were built from.
- **Trusted publishing:** the job gets a short-lived API key from nuget.org instead of using a stored one. This requires a
  Trusted Publishing policy on nuget.org (repository owner `fedarovich`, repository `CsCom`, workflow file `ci.yml`) and the
  repository secret `NUGET_USER` with the nuget.org profile name (not the email address).
