# Commands

`cscom` is a .NET tool, see [Getting started](getting-started.md) for installing it. Its commands generate a project,
build an assembly, pack it into a NuGet package, list a type library, manage the `CsComReference` items of a project and
export the type library of an assembly:

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

# Export the type library of an assembly with [GeneratedComInterface] and [GeneratedComClass] types
cscom typelib export bin\Release\net10.0-windows\Server.dll
```

The sources are described in [Sources](sources.md), the `reference` commands in [Managing references](msbuild.md#managing-references),
and `typelib export`, which has options of its own, in [Exporting type libraries](exporting-type-libraries.md).
`cscom <command> --help` lists the options of a command.

## Options

The options of the commands that generate code:

| Option | Description |
| --- | --- |
| `-o`, `--output <PATH>` | The output directory: `./<Name>` for `project create`, the current directory for `assembly build` and `assembly pack`, and the project directory for `project update`. |
| `-n`, `--name <NAME>` | The project and assembly name. `Interop.<Library>` by default with a single source; required with several sources, whose root namespace is `Interop.<Name>` by default, see [Several sources](sources.md#several-sources). |
| `--ns`, `--namespace <[LIBRARY=]NAMESPACE>` | The namespace of a library, e.g. `Excel=Office.Excel`, whether it is a source or is only used; can be specified multiple times. `Interop.<Library>` by default. Without a library name, the namespace of a single source, or the root namespace of several sources (`Interop.<Name>` by default), under which the support code is generated. |
| `-f`, `--framework <TFM>` | The target framework; can be specified multiple times. `net10.0-windows` by default. |
| `-r`, `--runtime <RID>` | The runtime identifier; can be specified multiple times. `AnyCPU` (no runtime identifier) by default. Not supported by `assembly pack`, whose packages are RID-neutral. |
| `-l`, `--cs`, `--csharp-version <VERSION>` | The C# version. C# 14 for .NET 10 and C# 15 for .NET 11 by default; only later versions can be specified. |
| `--single-file` | Generates all types into a single C# file instead of a file per type. |
| `-s`, `--support-code <package\|public\|internal>` | Where the support code comes from, see below. `package` by default. |
| `--date-marshalling`, `--currency-marshalling`, `--bool-marshalling` `<bcl\|none\|wrapper>` | How `DATE`, `CURRENCY` and `VARIANT_BOOL` parameters, return values and structure members are represented: `bcl` (default), `none` or `wrapper`, see [Marshalling options](marshalling.md). |
| `--struct-properties <auto\|never>` | Whether structure members converted to .NET types are properties (`auto`, default) or all members are public fields of the COM types (`never`), see [Structures](structures.md). |
| `--bstr-marshalling <string\|wrapper\|none>` | How `BSTR` parameters and return values are represented: `string` (default), `wrapper` (`ComBstr`) or `none`, see [BSTRs](bstrs.md). |
| `--safearray-marshalling <array\|sysarray\|wrapper\|none>` | How typed `SAFEARRAY(T)` parameters and return values are represented: `array` (default, `T[]`), `sysarray`, `wrapper` or `none`, see [SAFEARRAYs](safearrays.md). |
| `-e`, `--event-style <events\|observable\|interface\|none>` | The code generated for the events of coclasses: `events` (default), `observable`, `interface` or `none`, see [Events](events.md). |
| `--overloads-for-default-values` | Generates overloads for the methods whose optional parameters have default values that C# cannot express, see [Optional parameters](optional-parameters.md). |
| `--ni`, `--named-indexers <none\|classproperty\|structmethod>` | Gives indexer syntax to the parameterized properties other than the indexer: `none` (default), `classproperty` (`sheet.Range["A1"]`) or `structmethod` (`sheet.Range()["A1"]`), see [Named indexers](named-indexers.md). |
| `--dynamic`, `-d` | Generates the late binding with C# `dynamic`: `CreateDynamic()` of the coclasses and the support of CsCom.Runtime.Dynamic, see [Late binding with `dynamic`](dynamic.md). |
| `-c`, `--config <[LIBRARY=]PATH>` | An XML file that adds and configures the model and syntax transformers of a library, e.g. `Excel=excel.cscom.xml`; can be specified multiple times. Without a library name, the file of a single source. See [Configuration file](configuration.md). |
| `--reference-path <PATH>` | For interop assemblies: an assembly they reference, or a directory of such assemblies; can be specified multiple times, see [Interop assemblies](interop-assemblies.md). |
| `--package-source <PATH>` | An additional NuGet source, a folder or the URL of a feed, with the CsCom.Runtime and CsCom.Runtime.Dynamic packages, e.g. when they are not available on nuget.org. `project create` writes it into a `nuget.config` of the project directory, and fails when the directory already has one. |
| `--keep-temp` | `assembly build` and `assembly pack` only: keeps the temporary project used to build the assembly. The project of a build that fails is kept in any case. The projects that were kept are deleted by a run of these commands a week later. |
| `--package-version <VERSION>` | `assembly pack` only: the version of the package and the assembly. `<major>.<minor>.0` of the type library by default with a single source, `1.0.0` with several sources. |
| `-p`, `--property <NAME=VALUE>` | `assembly build` and `assembly pack` only: an MSBuild property of the temporary project, e.g. `Version=1.2.3`, `Company=Contoso` or `PackageLicenseExpression=MIT`; can be specified multiple times. |
| `-v`, `--verbosity <LEVEL>` | `quiet`, `minimal`, `normal` (default), `detailed` or `diagnostic`. |

## project create and project update

`project create` writes `<Name>.csproj`, a `cscom.json` file with the sources and the options it was generated with, and
the C# files into the `Generated` folder, in a folder per namespace.

`project update` replaces the C# files of the `Generated` folder using the sources and the options recorded in
`cscom.json`. Code outside of the `Generated` folder, e.g. partial classes, is not touched. `cscom.json` records the type
libraries and interop assemblies of the project directory by their relative paths, so the project can be updated from
another clone.

### Changing the sources and the options

`Source` arguments of `project update` replace the recorded sources, and these options change the recorded values:

- `--namespace` and `--config`, which change the values of the libraries they name: `LIBRARY=` removes one, and
  `--config ""` removes all the configuration files;
- `--csharp-version`, the marshalling options, `--event-style`, `--struct-properties` and `--named-indexers`;
- `--single-file [true|false]`, `--overloads-for-default-values [true|false]` and `--dynamic [true|false]`;
- `--reference-path`, whose empty value removes the paths.

### The project file

In the project file, the update sets the versions of the CsCom.Runtime packages, `LangVersion` only with
`--csharp-version`, and `RootNamespace` only with `--namespace` without a library name; neither is added to a project file
without it, and the rest of the file keeps its text and its encoding.

### The root namespace

`cscom.json` records the root namespace of several sources, which the update keeps, so that the support code does not
move. It keeps it also when the order or the number of the sources changes, down to a single source, until `--namespace`
without a library name sets another one. With a single source, that is its namespace, which is its root namespace again.

The projects of several sources of earlier versions, and the projects of a single source that get more sources, keep the
namespace of their first source as their root namespace, which the update records. So that the support code stays where
it is, on that update either:

- list the original first source first, also when the sources are replaced or reordered, and keep its namespace;
- or set the root namespace with `--namespace`.

### Earlier versions

- Update a project of a single source of an earlier version once without `Source` arguments before you add sources: its
  namespace and configuration file apply only to a single source until the update records them by library name.
- Update a project with the version of the tool that created it or a later one, e.g. pinned in a tool manifest: earlier
  versions do not know the recorded root namespace, see [Known issues](../known-issues.md#projects).

## assembly build

`assembly build` generates the project in a temporary directory, builds it with `dotnet build` once per runtime identifier
and copies the assemblies, PDBs and XML documentation (and `CsCom.Runtime.dll`) to the output directory.

- **Output folders:** with a single target framework and at most one runtime identifier, the files are copied directly
  into the output directory; otherwise into `<TFM>\<RID>` subfolders.
- **Properties:** `--property` sets MSBuild properties of the project and overrides the ones CsCom sets, e.g. the assembly
  version (`Version=1.2.3`) or a strong name (`SignAssembly=true` and `AssemblyOriginatorKeyFile=<full path>`; relative
  paths are relative to the temporary project).
- **Cancellation and failures:** Ctrl+C stops the build and deletes the temporary project, once the code is generated; a
  second Ctrl+C ends the tool at once. The project of a build that fails is kept, and its path is reported.

## assembly pack

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

## Listing a type library

`cscom list <Source>...` writes the types and members of the libraries with IDL-like signatures, using the names that the
selectors of the [configuration file](configuration.md) match. Each library starts with a header with its name, version and
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

## config create

`cscom config create [-o <dir>]` writes `cscom.xsd`, the schema of the configuration files, and a starter `cscom.xml`,
see [Configuration file](configuration.md).

## typelib export

`cscom typelib export <Assembly> [-o <file>]` writes the type library of a .NET assembly with source-generated COM
types, like TlbExp, see [Exporting type libraries](exporting-type-libraries.md).
