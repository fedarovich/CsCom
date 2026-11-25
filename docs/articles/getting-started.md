# Getting started

CsCom generates C# interop code for COM objects from their type libraries. This article installs the tool, generates the
interop code of `Scripting.Dictionary`, a COM class of every Windows installation, and calls it from a console
application.

## Requirements

- **Windows:** type libraries are read with the OLE Automation APIs, and COM interop runs on Windows only.
- **.NET 10 SDK or later.** The generated code targets .NET 10 or later, with a Windows-specific target framework such as
  `net10.0-windows`, and C# 14 or later.
- **Native AOT** (optional) needs what any Native AOT application on Windows needs: the C++ build tools of Visual Studio
  ("Desktop development with C++").

## Installing the tool

The `cscom` tool is the .NET tool of the **CsCom** package
([![CsCom](https://img.shields.io/nuget/v/CsCom.svg?label=nuget)](https://www.nuget.org/packages/CsCom)):

```powershell
dotnet tool install --global CsCom
```

Or install it as a local tool of a repository, so that everyone who works on it uses the same version:

```powershell
dotnet new tool-manifest   # once per repository
dotnet tool install CsCom
```

A local tool runs as `dotnet cscom`. Add `--prerelease` to install a preview version. Pin the version when you commit
projects created with `cscom project create`: update them with the version of the tool that created them or a later one,
see [Commands](commands.md#project-create-and-project-update).

## Choosing how to use the generated code

| Approach | How | When |
| --- | --- | --- |
| **During the build** | The [CsCom.Build](msbuild.md) package and `CsComReference` items, which `cscom reference add` adds. | The code is generated into the intermediate output of your project whenever its sources change, without a separate project. |
| **A generated project** | `cscom project create`, and `cscom project update` to regenerate it. | You keep the generated C# in source control, to read and review it, or to extend it with partial classes. |
| **An assembly or a package** | `cscom assembly build` or `cscom assembly pack`. | You want an interop assembly to reference, or a NuGet package to share. |

All three generate the same code. By default it uses the support code of the
**CsCom.Runtime** package, which the generated projects reference and CsCom.Build depends on; it can also be emitted into the
project, see [Support code](support-code.md).

## Generating the code during the build

Create a console application, and make its target framework Windows-specific, e.g. `net10.0-windows`. CsCom.Build warns
(`CSCOM2000`) when the target framework is not Windows-specific:

```powershell
dotnet new console -o DictionaryDemo
cd DictionaryDemo
```

```xml
<TargetFramework>net10.0-windows</TargetFramework>
```

Add the type library of `Scripting.Dictionary`, by the ProgID of the class:

```powershell
cscom reference add Scripting.Dictionary
```

The command adds a `CsComReference` item to the project file, and a reference to the CsCom.Build package of the version of
the tool:

```xml
<ItemGroup>
  <PackageReference Include="CsCom.Build" Version="..." />
</ItemGroup>

<ItemGroup>
  <CsComReference Include="Scripting.Dictionary" />
</ItemGroup>
```

Replace `Program.cs` with:

```csharp
using Interop.Scripting;

IDictionary dictionary = Dictionary.Create();       // creates the COM object (CoCreateInstance)
dictionary.CompareMode = CompareMethod.TextCompare; // properties are C# 14 extension properties
dictionary.Add("apple", 3);                         // VARIANT parameters are object?
dictionary.Add("pear", 5);
Console.WriteLine($"{dictionary.Count} items, apple: {dictionary.Exists("APPLE")}");

foreach (object? key in dictionary)                 // _NewEnum, through IEnumVARIANT
{
    Console.WriteLine($"{key} = {dictionary.get_Item(key)}");
}
```

```powershell
dotnet run
```

```
2 items, apple: True
apple = 3
pear = 5
```

What happened:

- **The build generated the code** into `obj\Debug\net10.0-windows\CsCom\Interop.Scripting\`, before the compilation. It
  is generated again only when the references, the options or the type library change. Visual Studio shows the files under
  the virtual folder `CsCom` of Solution Explorer.
- **The namespace** is `Interop.Scripting`, after the name of the type library of `scrrun.dll`. The `Namespace` metadata of
  the item, or `--namespace` of `cscom reference add`, sets another one.
- **The coclass** `Dictionary` is a static class whose `Create()` method creates the COM object and returns its default
  interface, `IDictionary`.
- **Properties** such as `Count` and `CompareMode` are accessor methods (`get_Count`) of the generated interface, with C# 14
  extension properties over them. `Item`, which has a parameter, is called through its accessor `get_Item`. With .NET 11,
  i.e. C# 15, the default member becomes an extension indexer: `dictionary[key]`. The other parameterized properties can
  get indexer syntax too, see [Named indexers](named-indexers.md).

See [Generated code](generated-code.md) for the rest of the mapping.

## Publishing with Native AOT

The generated code uses source-generated COM interop, so the application can be trimmed and compiled ahead of time:

```powershell
dotnet publish -c Release -r win-x64 -p:PublishAot=true
```

The exception is late binding with C# `dynamic` (CsCom.Runtime.Dynamic), which requires dynamic code; its API is annotated
with `[RequiresDynamicCode]`, so the compiler warns where an AOT application uses it. See
[Late binding with `dynamic`](dynamic.md).

## Generating a project instead

`cscom project create` writes the same code into a class library of its own, `Interop.Scripting` here, which applications
reference like any other project:

```powershell
cscom project create Scripting.Dictionary
dotnet add DictionaryDemo reference Interop.Scripting
```

The project records its sources and options in `cscom.json`, and `cscom project update -o Interop.Scripting` regenerates
it, e.g. after an update of the type library. `cscom assembly build Scripting.Dictionary -o bin` builds the assembly
instead, and `cscom assembly pack` packs it into a NuGet package. See [Commands](commands.md).

## Next steps

- [Sources](sources.md): type library files, LIBIDs, ProgIDs, interop assemblies, and several libraries at once.
- [MSBuild integration](msbuild.md): the options of CsCom.Build and of the `reference` commands.
- [Configuration file](configuration.md): renaming types and members, marshalling options of single members, and more.
- [Generated code](generated-code.md): what the generated C# looks like and how COM types are mapped.
- [Migrating from TlbImp](migrating-from-tlbimp.md): what changes for code that used the built-in COM interop.
- [Exporting type libraries](exporting-type-libraries.md): the type library of your own COM types, like TlbExp.
