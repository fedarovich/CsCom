---
_layout: landing
---

# CsCom

COM interop for .NET that works with trimming and Native AOT.

CsCom produces COM interop code from type libraries or existing .NET interop assemblies, similar to TlbImp. Unlike TlbImp, it
does not rely on the built-in COM interop of .NET: it generates C# code that uses the
[source-generated COM interop](https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation)
added in .NET 8, so the interop code can be trimmed and used with Native AOT.

```csharp
IDictionary dictionary = Dictionary.Create();       // a COM object of scrrun.dll
dictionary.CompareMode = CompareMethod.TextCompare; // properties are C# 14 extension properties
dictionary.Add("apple", 3);                         // VARIANT parameters are object?
foreach (object? key in dictionary)                 // _NewEnum, through IEnumVARIANT
{
    Console.WriteLine(key);
}
```

[Get started](articles/getting-started.md) · [Migrate from TlbImp](articles/migrating-from-tlbimp.md) ·
[API reference](api/index.md) · [GitHub](https://github.com/fedarovich/CsCom)

## Three ways to use it

- **During the build:** the [CsCom.Build](articles/msbuild.md) package generates the interop code of the type libraries
  that a project lists as `CsComReference` items, before the compilation.
- **As a project:** `cscom project create` writes a C# project with the interop code, which `cscom project update`
  regenerates, see [Commands](articles/commands.md).
- **As an assembly or a package:** `cscom assembly build` and `cscom assembly pack` build a ready-to-use interop assembly
  or NuGet package.

The sources can be type library files, registered LIBIDs or ProgIDs, and .NET interop assemblies, also several of them
generated together, see [Sources](articles/sources.md).

## Packages

The packages are released together, with the same version.

| Package | |
| --- | --- |
| [![CsCom](https://img.shields.io/nuget/v/CsCom.svg?label=CsCom)](https://www.nuget.org/packages/CsCom) | The `cscom` .NET tool, which generates projects, assemblies and packages, lists type libraries and manages the `CsComReference` items of projects. |
| [![CsCom.Build](https://img.shields.io/nuget/v/CsCom.Build.svg?label=CsCom.Build)](https://www.nuget.org/packages/CsCom.Build) | The [MSBuild integration](articles/msbuild.md), which generates the interop code during the build. |
| [![CsCom.Runtime](https://img.shields.io/nuget/v/CsCom.Runtime.svg?label=CsCom.Runtime)](https://www.nuget.org/packages/CsCom.Runtime) | The [support code](articles/support-code.md) that the generated code uses: `IDispatch`, the marshallers of `VARIANT`s, `SAFEARRAY`s and `BSTR`s, the late-bound wrappers and the events. |
| [![CsCom.Runtime.Dynamic](https://img.shields.io/nuget/v/CsCom.Runtime.Dynamic.svg?label=CsCom.Runtime.Dynamic)](https://www.nuget.org/packages/CsCom.Runtime.Dynamic) | [Late binding with C# `dynamic`](articles/dynamic.md). |

## What is generated

Vtable and dual interfaces become `[GeneratedComInterface]` interfaces with extension properties, dispinterfaces become
late-bound wrapper classes, coclasses get `Create()` methods and event sinks, and enums, structures and modules keep their
COM layout. `VARIANT`s, `BSTR`s, `SAFEARRAY`s, `DATE`, `CURRENCY` and `VARIANT_BOOL` are converted to .NET types, or kept
as wrappers of their own where that matters. See [Generated code](articles/generated-code.md).
