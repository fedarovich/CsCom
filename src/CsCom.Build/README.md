# CsCom.Build

CsCom.Build generates COM interop code during the build of a project, from the type libraries, ProgIDs, LIBIDs and .NET
interop assemblies that the project lists as `CsComReference` items. The generated C# uses the
[source-generated COM interop](https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation)
of .NET, so it can be trimmed and compiled with Native AOT. The package bundles the
[cscom](https://www.nuget.org/packages/CsCom) tool, which generates the code.

It needs Windows, .NET 10 or later and C# 14 or later, and a Windows-specific target framework such as `net10.0-windows`.

## Usage

`cscom reference add Scripting.Dictionary` adds the package and the reference to a project, or add them yourself:

```xml
<ItemGroup>
  <CsComReference Include="Scripting.Dictionary" />
  <CsComReference Include="C:\Libs\Legacy.tlb" Namespace="Legacy.Interop" />
</ItemGroup>
```

The code is generated into the intermediate output before the compilation, again only when the references, the options or
the type libraries change. Each reference gets a namespace of its own: its `Namespace` metadata, or `Interop.<Library>`,
e.g. `Interop.Scripting`:

```csharp
using Interop.Scripting;

IDictionary dictionary = Dictionary.Create();
dictionary.Add("apple", 3);
Console.WriteLine(dictionary.Count);
```

The options, such as the events, the marshalling and the support code, are item metadata and `CsCom*` properties.

With `<CsComExportTypeLibrary>true</CsComExportTypeLibrary>`, the package also exports the type library (`.tlb`) of the
`[GeneratedComInterface]` and `[GeneratedComClass]` types of the project after the compilation, like TlbExp.

## Documentation

See [cscom.fedarovich.com](https://cscom.fedarovich.com/), starting with
[Getting started](https://cscom.fedarovich.com/articles/getting-started.html),
[MSBuild integration](https://cscom.fedarovich.com/articles/msbuild.html) and
[Exporting type libraries](https://cscom.fedarovich.com/articles/exporting-type-libraries.html).
