# MSBuild integration

The **CsCom.Build** package generates the interop code during the build of a project, without a separate interop project or
assembly. [`cscom reference add`](#managing-references) adds the package to a project together with the references, or
add it yourself with `dotnet add package CsCom.Build`
([![CsCom.Build](https://img.shields.io/nuget/v/CsCom.Build.svg?label=CsCom.Build)](https://www.nuget.org/packages/CsCom.Build)).
The type libraries and interop assemblies are listed as `CsComReference` items:

```xml
<ItemGroup>
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
| `Config` | | The [configuration file](configuration.md) of the reference. |
| `EventStyle` | `CsComEventStyle` | `events` (default), `observable`, `interface` or `none`, see [Events](events.md). |
| `StructProperties` | `CsComStructProperties` | `auto` (default) or `never`, see [Structures](structures.md). |
| `OverloadsForDefaultValues` | `CsComOverloadsForDefaultValues` | `false` (default) or `true`, see [Optional parameters](optional-parameters.md). |
| `NamedIndexers` | `CsComNamedIndexers` | `none` (default), `classproperty` or `structmethod`, see [Named indexers](named-indexers.md). |
| | `CsComDynamic` | `false` (default) or `true`: the late binding with `dynamic` for all the libraries. With the `package` support code, the project must reference the CsCom.Runtime.Dynamic package, which `cscom reference add --dynamic` adds; the build fails otherwise. See [Late binding with `dynamic`](dynamic.md). |
| `DateMarshalling`, `CurrencyMarshalling`, `BoolMarshalling` | `CsComDateMarshalling`, `CsComCurrencyMarshalling`, `CsComBoolMarshalling` | `bcl` (default), `none` or `wrapper`, see [Marshalling options](marshalling.md). |
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

## Managing references

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
