# CsCom

[![Build](https://github.com/fedarovich/CsCom/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/fedarovich/CsCom/actions/workflows/ci.yml?query=branch%3Amain)
[![Documentation](https://img.shields.io/badge/docs-cscom.fedarovich.com-blue)](https://cscom.fedarovich.com/)
[![CsCom](https://img.shields.io/nuget/v/CsCom.svg?label=CsCom)](https://www.nuget.org/packages/CsCom)
[![CsCom.Build](https://img.shields.io/nuget/v/CsCom.Build.svg?label=CsCom.Build)](https://www.nuget.org/packages/CsCom.Build)
[![CsCom.Runtime](https://img.shields.io/nuget/v/CsCom.Runtime.svg?label=CsCom.Runtime)](https://www.nuget.org/packages/CsCom.Runtime)
[![CsCom.Runtime.Dynamic](https://img.shields.io/nuget/v/CsCom.Runtime.Dynamic.svg?label=CsCom.Runtime.Dynamic)](https://www.nuget.org/packages/CsCom.Runtime.Dynamic)

CsCom is a command line tool that produces COM interop code from type libraries or existing .NET interop assemblies, similar to
TlbImp, and an MSBuild package (CsCom.Build) that generates it during the build of a project. Unlike TlbImp, it does not rely
on the built-in COM interop of .NET: it generates C# code that uses the
[source-generated COM interop](https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation)
added in .NET 8, so the interop code can be trimmed and used with Native AOT.

**The documentation is at [cscom.fedarovich.com](https://cscom.fedarovich.com/)**, starting with
[Getting started](https://cscom.fedarovich.com/articles/getting-started.html).

## Requirements

- Windows: type libraries are read with the OLE Automation APIs. Interop assemblies are read from their metadata.
- .NET 10 SDK or later. The generated projects target .NET 10 or later.

## Usage

```powershell
# Install the tool
dotnet tool install --global CsCom

# Generate a C# project with the interop types
cscom project create C:\Windows\System32\scrrun.dll -o Interop.Scripting

# Generate several type libraries into one project, each into its own namespace
cscom project create Excel.Application Scripting.Dictionary -n Office.Interop --ns Excel=Office.Excel

# Build a ready-to-use interop assembly, or pack it into a NuGet package
cscom assembly build C:\Windows\System32\scrrun.dll -o bin -p Version=1.0.0
cscom assembly pack C:\Windows\System32\scrrun.dll -o packages --package-version 1.0.0 -p Authors=Contoso

# List the types and members of a type library
cscom list Scripting.Dictionary --type ^IDictionary$

# Generate the interop code during the build of an existing project, with the CsCom.Build package
cscom reference add Excel.Application --namespace Office.Excel

# Export the type library of an assembly with [GeneratedComInterface] and [GeneratedComClass] types, like TlbExp
cscom typelib export bin\Release\net10.0-windows\Server.dll
```

The generated code:

```csharp
IDictionary dictionary = Dictionary.Create();       // coclass: CoCreateInstance + ComWrappers
dictionary.CompareMode = CompareMethod.TextCompare; // C# 14 extension property over set_CompareMode
dictionary.Add("a", 1);                             // VARIANT parameters are object?
Console.WriteLine(dictionary.Count);                // C# 14 extension property over get_Count
foreach (object? key in dictionary)                 // _NewEnum via IEnumVARIANT
{
}
```

## Packages

The four packages are released together, with the same version.

| Package | Description |
| --- | --- |
| [CsCom](https://www.nuget.org/packages/CsCom) | The `cscom` .NET tool: generates projects, assemblies and packages, lists type libraries and manages the `CsComReference` items of projects. |
| [CsCom.Build](https://www.nuget.org/packages/CsCom.Build) | The MSBuild integration, which generates the interop code during the build of a project. |
| [CsCom.Runtime](https://www.nuget.org/packages/CsCom.Runtime) | The support code that the generated code uses: `IDispatch`, the marshallers, the late-bound wrappers and the events. |
| [CsCom.Runtime.Dynamic](https://www.nuget.org/packages/CsCom.Runtime.Dynamic) | Late binding with C# `dynamic`. |

## Documentation

- [Getting started](https://cscom.fedarovich.com/articles/getting-started.html) and
  [Migrating from TlbImp](https://cscom.fedarovich.com/articles/migrating-from-tlbimp.html)
- Using the tool: [Sources](https://cscom.fedarovich.com/articles/sources.html),
  [Commands](https://cscom.fedarovich.com/articles/commands.html),
  [Interop assemblies](https://cscom.fedarovich.com/articles/interop-assemblies.html),
  [MSBuild integration](https://cscom.fedarovich.com/articles/msbuild.html),
  [Exporting type libraries](https://cscom.fedarovich.com/articles/exporting-type-libraries.html),
  [Configuration file](https://cscom.fedarovich.com/articles/configuration.html)
- [Generated code](https://cscom.fedarovich.com/articles/generated-code.html): marshalling, interface pointers, events,
  late binding with `dynamic`, support code and [limitations](https://cscom.fedarovich.com/articles/limitations.html)
- [API reference](https://cscom.fedarovich.com/api/index.html) of CsCom.Runtime and CsCom.Runtime.Dynamic
- [Benchmarks](https://cscom.fedarovich.com/articles/benchmarks.html) against the built-in COM interop

The documentation is built from the [`docs`](docs) folder. The changes of each version are described in
[CHANGELOG.md](CHANGELOG.md), and the known issues that are not fixed in [docs/known-issues.md](docs/known-issues.md).

## Contributing

`dev` is the integration branch: create feature branches from it and target pull requests to it. Building, testing and
releasing are described in [Development](https://cscom.fedarovich.com/articles/development.html), and the projects in
[Architecture](https://cscom.fedarovich.com/articles/architecture.html).

```powershell
dotnet build src/CsCom.slnx
dotnet test src/CsCom.slnx --no-build --filter "TestCategory!=Integration"
```

## License

[MIT](LICENSE)
