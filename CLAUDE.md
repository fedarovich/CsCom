# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

CsCom generates COM interop code from type libraries or .NET interop assemblies, like TlbImp, but the generated C# uses
source-generated COM interop (`[GeneratedComInterface]`, ComWrappers), so it works with trimming and Native AOT. It ships as
four NuGet packages that are always released together with the same version: `CsCom` (the `cscom` .NET tool), `CsCom.Build`
(MSBuild integration), `CsCom.Runtime` and `CsCom.Runtime.Dynamic` (support code of the generated code).

`README.md` is the user documentation and is thorough: read the relevant section before changing a behavior it describes.
`docs/requirements.md` holds the original requirements; the README is the current description.

## Commands

Windows only (type libraries are read with the OLE Automation APIs). Building needs the .NET 11 SDK; the tool and the
`net10.0` tests also need the .NET 10 runtime.

```powershell
dotnet build src/CsCom.slnx
dotnet test src/CsCom.slnx --no-build --filter "TestCategory!=Integration"   # unit tests, on .NET 10 and .NET 11
dotnet pack src/CsCom.slnx --no-build                                        # packages into artifacts/packages
dotnet test src/CsCom.slnx --no-build                                        # all tests; the integration tests need the packages
```

A single test, on one target framework:

```powershell
dotnet test src/CsCom.Tests/CsCom.Tests.csproj -f net11.0-windows --filter "FullyQualifiedName~CodeGenerationTests.GeneratesSingleFile"
```

- Set `$env:DiffEngine_Disabled = 'true'` before running tests, so that Verify does not open a diff tool for a snapshot
  that does not match.
- There is no separate linter: `TreatWarningsAsErrors` and `AnalysisLevel=latest` apply to every project
  (`src/Directory.Build.props`), and CsCom.Runtime and CsCom.Runtime.Dynamic generate documentation files, so a public member
  without an XML comment fails their build.
- The tool from source: `dotnet run --no-launch-profile --project src/CsCom -- list Scripting.Dictionary`. Without
  `--no-launch-profile` and without arguments, `dotnet run` takes the arguments of the first profile of
  `Properties/launchSettings.json`, which creates a project on another drive. `assembly build` and `assembly pack` restore
  CsCom.Runtime of the version of the tool, so from source they need `--package-source artifacts\packages` after a
  `dotnet pack`.
- Benchmarks: `dotnet run -c Release -f net11.0-windows --project src/CsCom.Benchmarks -- --filter *StaticDispatch*`.
- The test type libraries are rebuilt with `src/CsCom.Tests/TestAssets/build-tlb.ps1` after a change of an `.idl` file
  (needs Visual Studio with the C++ tools and TlbImp of the Windows SDK). The `.tlb` files and `Interop.CsComTest.dll` are
  committed.

## Architecture

### The pipeline (`src/CsCom.Core`)

`InteropPipeline` runs a source through these steps:

1. **Read** into the model (`CsCom.Model`): `TypeLibraryReader` reads a type library with source-generated
   `ITypeLib`/`ITypeInfo` bindings; `InteropAssemblyReader` rebuilds the same model from the metadata of an interop assembly
   with `MetadataLoadContext`. `TypeLibrarySource` resolves files, LIBIDs and ProgIDs.
2. **Model transformers** (`IModelTransformer`, `Transformations/`), in a fixed order: `AliasResolver`, the transformers of
   `InteropPipeline.ModelTransformers`, those of the configuration file, `UnidentifiedInterfaceReplacer`, `NameSanitizer`.
   So the configurable transformers see resolved aliases and the names of the type library, and the names they produce are
   made valid and unique afterwards.
3. **Emit** Roslyn syntax trees: `CSharpEmitter` dispatches by type kind to `InterfaceEmitter` (vtable and dual interfaces
   plus their `<Interface>Extensions` class), `DispatchWrapperEmitter` (dispinterfaces as late-bound wrapper classes),
   `EventEmitter` and `TypeEmitters` (enums, records, coclasses, modules). `TypeMapper` and `SignatureBuilder` decide the C#
   types and marshalling.
4. **Syntax transformers** (`ISyntaxTransformer`): the emitted declarations carry annotations (`ModelAnnotations`) that map
   nodes back to model elements and a `SyntaxRole`, which `TransformationContext.Annotations` resolves.
5. **Output** (`Output/`): `ProjectGenerator` writes a project with a `cscom.json` manifest; `AssemblyGenerator` builds or
   packs a temporary project with `dotnet`.

`LibrarySetGenerator` (`LibrarySets/`) is the entry point that everything actually uses, the commands as well as CsCom.Build:
it reads several references, merges the types they import from other libraries into one model per library
(`LibrarySetMerger`), transforms each with its own options and configuration, and emits each with an `EmitScope` that knows
the namespaces of all the libraries. The support code is emitted once, with the first library.

### The tool (`src/CsCom`)

`CommandLine.cs` defines all commands with System.CommandLine: `project create|update`, `assembly build|pack`, `list`,
`config create`, `reference add|list|remove`, and the hidden `build-generate`, which is the generator of CsCom.Build.
Invalid input is a `CsComException`, which is reported without a stack trace; anything else is logged as an unexpected error.

### CsCom.Build (`src/CsCom.Build`)

The `CsComGenerate` task targets netstandard2.0 so that it loads into the MSBuild of both the .NET SDK and Visual Studio; it
cannot reference CsCom.Core. It writes the `CsComReference` items and the `CsCom*` properties into `cscom.request.json` and
runs the bundled `cscom.dll build-generate` as a separate process (`LibrarySetBuild` in CsCom.Core executes the request).
`cscom.state` in the intermediate output records the hash of the request, the files read and the files generated, which
makes the generation incremental. The package is described by `CsCom.Build.nuspec`; packing it publishes the tool into its
`tools` folder (`PrepareCsComPackage`).

### The support code (`src/CsCom.Runtime`, `src/CsCom.Runtime.Dynamic`)

These are the types that the generated code calls: `IDispatch`, the `VARIANT`/`SAFEARRAY`/`BSTR` marshallers, `DispatchObject`
(the base of the late-bound wrappers), the event sinks, and the `dynamic` support. Their sources have two consumers: they are
compiled into the packages, and they are embedded as resources into CsCom.Core, from which `SupportCodeProvider` emits them
into generated projects in the `public` and `internal` support code modes, rewriting the namespace and the accessibility.
The sources must therefore stay self-contained:

- no implicit usings and no assembly-level attributes, and only the file-scoped namespace `CsCom.Runtime` (both projects);
- compatible with C# 14: the `net10.0` build uses the default C# version to enforce it;
- CsCom.Runtime.Dynamic only uses the public API of CsCom.Runtime;
- AOT compatible (`IsAotCompatible`).

Both target `net10.0` and `net11.0`, because a generated interface that derives from `IDispatch` must be compiled for the
same target framework as the assembly that declares it (SYSLIB1230).

## Things that span several files

- **A code generation option** flows through every layer; `NamedIndexers` is a complete example to grep for:
  `CodeGenerationOptions`/`MarshallingOptions` → `LibrarySetOptions`/`LibrarySetReference` → the options of `CommandLine.cs`
  → `CsComManifest` (`cscom.json`) and `Output/Requests.cs` → `BuildRequest`/`LibrarySetBuild` → the `CsComGenerate` task
  (a property, and `ReferenceMetadata` for item metadata) → `build/CsCom.Build.props` and `.targets` →
  `CsComReferenceEditor` → the option tables of the README.
- **A configurable transformer** is registered in `TransformerRegistry.CreateDefault`, declared in
  `Configuration/cscom.xsd` (a test checks that the schema declares every registered transformer), given an example in
  `Configuration/cscom.template.xml` and described in the README.
- **The C# version of the generated code** depends on the target framework: C# 14 for .NET 10, C# 15 for .NET 11. Features
  of C# 15 such as extension indexers are emitted only behind checks like `CodeGenerationOptions.SupportsExtensionIndexers`.
- **A change of the generated code or of CsCom.Runtime** usually needs: the Verify snapshots, the integration tests (which
  compile and run the generated code against real COM objects), the README section and a CHANGELOG entry.

## Tests (`src/CsCom.Tests`)

- MSTest with Verify snapshots, for `net10.0-windows` and `net11.0-windows`; test classes run in parallel.
- **Snapshots** are in `Snapshots/` as `<Class>.<Method>[_<parameters>][#<file>].verified.cs|txt`. A mismatch writes a
  `.received.` file next to it (ignored by git): review the difference, then replace the `.verified.` file with it. The
  headers of the generated files use `TestEnvironment.ToolVersion`, so that the snapshots do not depend on the version.
- **Integration tests** (`[TestCategory("Integration")]`: `IntegrationTests`, `BuildPackageTests`) generate projects, build
  them with the .NET SDK (and the MSBuild of Visual Studio when installed) and run them. They restore CsCom.Runtime,
  CsCom.Runtime.Dynamic and CsCom.Build from `artifacts/packages`, which only `dotnet pack` updates: **pack again after
  changing the runtime, the task or the tool**, or the tests run against the old packages. They are slow; filter them.
- **Fixtures:** `TestAssets/CsComTest.idl` is the type library that most tests generate from; add a construct there (and
  rebuild the `.tlb`) to cover it. Tests against real objects use `scrrun.dll` and `stdole2.tlb` of Windows.
  `TestCompiler` compiles hand-written `[ComImport]` fixtures in memory.
- `MemoryLeakTests` use `LeakDetector.AssertNoLeaks` (an `IMallocSpy`) to check that the runtime support frees `BSTR`s,
  `SAFEARRAY`s and COM references.

## Conventions

- **Packages:** central package management, versions in `src/Directory.Packages.props`. Verify.MSTest is pinned to 32.0.0
  (later versions require a sponsorship property) and BenchmarkDotNet to a 0.16 preview (0.15 does not run on .NET 11); do
  not update them casually. `nuget.config` only allows nuget.org.
- **Branches:** `dev` is the integration branch: create feature branches from `dev`, and merge them (and target pull
  requests) into `dev`, not `main`. `main` is only updated by merging `dev` into it for a stable release, so never commit
  or merge feature work directly into `main`.
- **Versions and releases:** local builds have the version 0.1.0 (`VersionPrefix`). Pushing a tag with a semantic version,
  e.g. `3.0.1` or `3.1.0-beta.1`, makes CI build, test and publish all four packages to nuget.org, so never push a tag
  unless asked to release.
- **Changelog:** user-visible changes go into `CHANGELOG.md` under the version, in `Changed`, `Added` and `Fixed` sections,
  written from the point of view of the user, in the style of the existing entries.
- **Commits:** the subject is an imperative sentence that ends with a period, e.g. `Keep a void pointer retval as a
  parameter.`; a body explains what was wrong when it is not obvious.
- **Code:** public API has XML documentation, comments explain why rather than what, log messages are `[LoggerMessage]`
  partial methods, and the emitters build syntax from text with the helpers of `CodeGeneration/Syntax.cs`.
