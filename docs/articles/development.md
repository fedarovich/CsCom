# Development

CsCom is developed on GitHub, in [fedarovich/CsCom](https://github.com/fedarovich/CsCom). The `dev` branch is the
integration branch: create feature branches from `dev` and target pull requests to it; `main` only receives the merges of
`dev` for stable releases. The projects are described in [Architecture](architecture.md).

```powershell
dotnet build src/CsCom.slnx
dotnet test src/CsCom.slnx --no-build --filter "TestCategory!=Integration"  # without the tests that build generated projects
dotnet pack src/CsCom.slnx                                                  # the packages, in artifacts/packages
dotnet test src/CsCom.slnx --no-build                                       # all tests, which need the packages
```

- The build is Windows only, since type libraries are read with the OLE Automation APIs.
- `dotnet pack` packs the Release configuration, which `dotnet build` (Debug) does not build, so do not pass `--no-build`
  to it unless the solution was built with `-c Release`.
- Set `$env:DiffEngine_Disabled = 'true'` before running the tests, so that Verify does not open a diff tool for a snapshot
  that does not match. A mismatch writes a `.received.` file next to the `.verified.` one: review the difference and replace
  the `.verified.` file with it.
- There is no separate linter: `TreatWarningsAsErrors` and `AnalysisLevel=latest` apply to every project, and CsCom.Runtime
  and CsCom.Runtime.Dynamic generate documentation files, so a public member without an XML comment fails their build.
- The tool from source: `dotnet run --no-launch-profile --project src/CsCom -- list Scripting.Dictionary`.
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
- [Known issues](../known-issues.md) (`docs/known-issues.md`) lists the known issues that are not fixed, with the reasons
  why and what a fix would take: check it before reporting or fixing one.
- The benchmarks are described in [Benchmarks](benchmarks.md).

## Documentation

This site is built with [docfx](https://dotnet.github.io/docfx/) from the `docs` folder: the articles are in
`docs/articles`, and the API reference is generated from the XML documentation of CsCom.Runtime and CsCom.Runtime.Dynamic.
docfx is a local tool of the repository (`dotnet-tools.json`):

```powershell
dotnet tool restore
dotnet docfx docs/docfx.json --serve    # builds the site into docs/_site and serves it at http://localhost:8080
```

- **Extension blocks:** docfx does not generate the members of C# 14 extension blocks yet, so
  `docs/apidoc/ComDynamic.md` describes the `extension(ComActivator)` members of `ComDynamic`. Keep it in sync with their
  XML documentation.
- **Publishing:** the [Docs workflow](https://github.com/fedarovich/CsCom/blob/dev/.github/workflows/docs.yml) builds the
  site with warnings as errors, so that a broken link fails it, for every push and pull request that changes it, and
  deploys it to GitHub Pages from `main`, i.e. with each stable release.

## CI and releases

The [CI workflow](https://github.com/fedarovich/CsCom/blob/dev/.github/workflows/ci.yml) builds the solution in Release on Windows, runs the unit tests (without the
`Integration` category) on .NET 10 and .NET 11 and then packs the four packages from the build output for every push and pull
request; the packages are kept as the `packages` artifact of the run. The changes of each version are described in
[CHANGELOG.md](../../CHANGELOG.md).

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
