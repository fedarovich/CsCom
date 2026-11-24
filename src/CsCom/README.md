# CsCom

The `cscom` .NET tool generates COM interop code from type libraries or existing .NET interop assemblies, similar to
TlbImp. Unlike TlbImp, it does not rely on the built-in COM interop of .NET: the generated C# uses the
[source-generated COM interop](https://learn.microsoft.com/dotnet/standard/native-interop/comwrappers-source-generation)
of .NET, so it can be trimmed and compiled with Native AOT.

It needs Windows and the .NET 10 SDK or later.

## Usage

```powershell
dotnet tool install --global CsCom

# Generate a C# project with the interop code of a type library, by a file, a LIBID or a ProgID
cscom project create Scripting.Dictionary

# Or generate it during the build of an existing project, with the CsCom.Build package
cscom reference add Scripting.Dictionary
```

The generated code:

```csharp
using Interop.Scripting;

IDictionary dictionary = Dictionary.Create();       // creates the COM object (CoCreateInstance)
dictionary.CompareMode = CompareMethod.TextCompare; // properties are C# 14 extension properties
dictionary.Add("apple", 3);                         // VARIANT parameters are object?
foreach (object? key in dictionary)                 // _NewEnum, through IEnumVARIANT
{
    Console.WriteLine(key);
}
```

`cscom assembly build` and `cscom assembly pack` build a ready-to-use interop assembly or NuGet package instead, and
`cscom list` lists the types and members of a type library. `cscom typelib export` goes the other way, like TlbExp: it
writes the type library of an assembly with `[GeneratedComInterface]` and `[GeneratedComClass]` types.

## Documentation

See [cscom.fedarovich.com](https://cscom.fedarovich.com/), starting with
[Getting started](https://cscom.fedarovich.com/articles/getting-started.html) and
[Commands](https://cscom.fedarovich.com/articles/commands.html).
