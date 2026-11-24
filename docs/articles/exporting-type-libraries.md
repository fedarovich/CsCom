# Exporting type libraries

CsCom also goes the other way, like TlbExp: it exports the type library (`.tlb`) of a .NET assembly whose COM types are
declared for source-generated COM interop, with `[GeneratedComInterface]` and `[GeneratedComClass]`. TlbExp only
understands the types of the built-in COM interop, which trimming and Native AOT do not support.

```csharp
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

[assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A6000")]

[GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
[Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A6001")]
public partial interface IGreeter
{
    string Greet(string name);

    int get_Count();
}

[GeneratedComClass]
[Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A6010")]
public partial class Greeter : IGreeter
{
    public string Greet(string name) => $"Hello, {name}";

    public int get_Count() => 1;
}
```

```
> cscom typelib export bin\Release\net10.0-windows\Greeter.dll
> cscom list bin\Release\net10.0-windows\Greeter.tlb
// Greeter 1.0: Interop.Greeter
interface IGreeter : IUnknown
    method Greet([in] BSTR name, [out, retval] BSTR* pRetVal)
    propget Count([out, retval] long* pRetVal)
coclass Greeter
    [default] IGreeter
```

The type library describes the types; CsCom does not register it, and does not make the classes creatable by COM: a
class factory and the registration of the classes are up to the program.

## The command

`cscom typelib export <Assembly>` reads the assembly, without loading it for execution, and writes the type library:

| Option | Description |
| --- | --- |
| `-o`, `--output <PATH>` | The type library to write. The path of the assembly with the extension `.tlb` by default. |
| `-n`, `--name <NAME>` | The name of the library. The name of the assembly by default, with `_` for the characters that a name cannot have. |
| `--guid <GUID>` | The identifier (LIBID) of the library. The `[Guid]` of the assembly by default; one of them is required. |
| `--library-version <MAJOR.MINOR>` | The version of the library. `[TypeLibVersion]` of the assembly by default, otherwise the version of the assembly, where 0.0 is 1.0. |
| `--platform <win32\|win64>` | The platform that the layouts of the structures and the interfaces are written for. `win64` by default. |
| `--ref`, `--reference-path <PATH>` | An assembly that the assembly references, or a directory of such assemblies; can be specified multiple times. The directory of the assembly and the .NET runtime of the tool are searched as well. |
| `--tlb`, `--type-library <PATH>` | The file of a type library whose generated code the assembly uses, see [Types of generated code](#types-of-generated-code); can be specified multiple times. |

## During the build

The [CsCom.Build](msbuild.md) package exports the type library of a project after the compilation when
`CsComExportTypeLibrary` is `true`:

```xml
<PropertyGroup>
  <CsComExportTypeLibrary>true</CsComExportTypeLibrary>
</PropertyGroup>
```

| Property | Description |
| --- | --- |
| `CsComExportTypeLibrary` | `false` (default) or `true`. |
| `CsComTypeLibraryName`, `CsComTypeLibraryGuid`, `CsComTypeLibraryVersion` | The name, the identifier and the version (`major.minor`) of the library, as the options of the command. |
| `CsComTypeLibraryPlatform` | `win32` or `win64`. `win32` by default when `PlatformTarget` is `x86`, otherwise `win64`. |
| `CsComTypeLibraryPath` | The file. `$(IntermediateOutputPath)$(TargetName).tlb` by default. |

- **Output:** the type library is written into the intermediate output and copied to the output directory with the
  assembly, also to the publish directory and to the output of the projects that reference the project.
- **Incremental:** the type library is only exported again when the assembly was compiled again, or when the properties,
  the package, a referenced assembly whose types it uses or a type library that it refers to changed. The warnings of the
  export are reported by every build.
- **References:** the assemblies that the project references are passed to the export. The type libraries of the
  `CsComReference` items of the project that are files are those of `--type-library`; the type library of generated code
  of a referenced assembly that is not registered is a `CsComTypeLibraryReference` item:
  `<CsComTypeLibraryReference Include="..\lib\Device.tlb" />`.
- **Several target frameworks:** each target framework exports its own type library, so a `CsComTypeLibraryPath`
  that does not depend on the target framework is written by all of them.

## What is exported

- **Interfaces:** the public `[GeneratedComInterface]` interfaces. An interface needs a `[Guid]`.
- **Classes:** the public `[GeneratedComClass]` classes with a `[Guid]`, as coclasses. A class without a `[Guid]` is not
  exported.
- **Enumerations and structures:** those that the members of the exported interfaces and the fields of the exported
  structures use; others are not exported.
- **Visibility:** `[ComVisible(false)]` on a type, or on the assembly unless the type has `[ComVisible(true)]`, leaves the
  type out. Types that are not public are left out. Older project templates put `[assembly: ComVisible(false)]` into
  `AssemblyInfo.cs`, which hides all the types; the export tells when that leaves the library empty.
- **Names:** the types have their names without their namespaces. Names are not case-sensitive in a type library, so two
  types whose names only differ in case, or in their namespaces, are an error. For the same reason a type library
  spells a name the way it was first written: a parameter `size` of a structure `Size` is named `Size`.
- **Help strings:** `[Description]` on a type, a member, an enumeration member or a field, and `[AssemblyDescription]`
  for the library.

### Interfaces

An interface that derives from `IDispatch`, of CsCom.Runtime or any `[GeneratedComInterface]` declaration with its
identifier, is exported as a dual interface; any other one as an interface that derives from its base interface or
from `IUnknown`.

- **Members:** the methods in the order of their declarations. A method that the COM generator adds to an interface
  for a member of its base interface is not a member of the interface in the library.
- **Return values:** a method returns an `HRESULT`, and its return value in a last `[out, retval]` parameter. A
  `[PreserveSig]` method has its own return type; `[return: MarshalAs(UnmanagedType.Error)]` makes an `int` an `HRESULT`.
- **Parameters:** a parameter passed by value is `[in]`; `in` is an `[in]` pointer, `ref` an `[in, out]` pointer and
  `out` an `[out]` pointer.
- **Optional parameters:** a default value is the `[defaultvalue]` of an `[optional]` parameter. A parameter whose
  default value is `null`, or that only has `[Optional]`, is `[optional]` without a default value, whatever its type.
- **Properties:** source-generated COM interfaces have no properties, so the accessors of a property are the methods
  named `get_Name` (`propget`), `set_Name`, `put_Name` or `let_Name` (`propput`) and `putref_Name` (`propputref`), as
  in the code that CsCom generates. A getter returns a value and a setter returns none; a method with such a name that
  is not an accessor, e.g. a `[PreserveSig]` one, is exported as a method, with a warning.
- **Identifiers:** `[DispId]` sets the identifier of a member (its DISPID in a dual interface); the accessors of a
  property share the identifier that one of them declares. Without it, a member gets an identifier that follows those
  of the base interfaces, and `get__NewEnum` gets `DISPID_NEWENUM`. Two members with the same identifier are an error.
- **Flags:** `[TypeLibFunc]` sets the flags of a member and `[TypeLibType]` those of a type. Without `[TypeLibType]`, a
  class with a public parameterless constructor can be created.
- **`oleautomation`:** when a type library is registered, the type library marshaller of OLE Automation becomes the
  proxy of its interfaces that are marked `oleautomation`. An interface without `[TypeLibType]` is therefore only
  marked when that marshaller can marshal it: all its members return an `HRESULT`, their parameters have Automation
  types (the numeric types, `BSTR`, `VARIANT_BOOL`, `DATE`, `CURRENCY`, `DECIMAL`, `SCODE`, `VARIANT`, interface
  pointers, enumerations, structures, `GUID` and `SAFEARRAY`s of these, also by reference), and the same holds for
  the interfaces of the library that it derives from. A `[PreserveSig]` member with another return type, an `LPWSTR`
  or a `void*` parameter leaves the interface unmarked. A dual interface is always marked, as OLE Automation requires;
  the export warns about a member of one that is not compatible.

### Classes

The interfaces of a coclass are the exported interfaces that the class implements, without those that another one of
them derives from. `[ComDefaultInterface]` names the default interface; the first interface is the default otherwise.
`[ComSourceInterfaces]` names the event interfaces, the first of which is the default one.

### Types

Source-generated COM interop marshals a value as its marshaller tells, so the marshallers and `[MarshalAs]` determine
the types of the library. The marshallers of the CsCom support code are recognized whether they come from CsCom.Runtime
or were generated into a project.

| C# | Type library |
| --- | --- |
| `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double` | `char`, `unsigned char`, `short`, `unsigned short`, `long`, `unsigned long`, `hyper`, `unsigned hyper`, `float`, `double` |
| `char` | `unsigned short` |
| `bool` | `VARIANT_BOOL` with `[MarshalAs(UnmanagedType.VariantBool)]`, `long` with `UnmanagedType.Bool`, otherwise `unsigned char` |
| `string` | `BSTR` with `BStrStringMarshaller` or `UnmanagedType.BStr`, `LPWSTR` with `StringMarshalling.Utf16` or `UnmanagedType.LPWStr`, `LPSTR` with `StringMarshalling.Utf8` or `UnmanagedType.LPStr`; on the parameter, or on the interface |
| `decimal` | `DECIMAL`; `CURRENCY` with `CurrencyMarshaller` |
| `DateTime` | `DATE` |
| `Guid` | `GUID` |
| `object` | `VARIANT` with `VariantMarshaller`, `OptionalVariantMarshaller` or `ComVariantMarshaller`; `IUnknown*` with `ComObjectMarshaller<object>` or `ComInterfaceMarshaller<object>` |
| `ComVariant` | `VARIANT` |
| A `[GeneratedComInterface]` interface | A pointer to the interface; `IDispatch*`, `IUnknown*` and `IEnumVARIANT*` for the interfaces with their identifiers |
| `T[]` with `SafeArrayMarshaller<T>`, `ComSafeArray<T>` | `SAFEARRAY(T)` |
| `object[]` with `UnknownSafeArrayMarshaller` | `SAFEARRAY(IUnknown*)` |
| `ComBool`, `ComDate`, `ComCurrency`, `ComBstr`, `ComBstrValue` | `VARIANT_BOOL`, `DATE`, `CURRENCY`, `BSTR` |
| An enumeration based on `int` or `uint` | The enumeration |
| A structure | The structure, with all its instance fields; the field of an auto-property has the name of the property. A union when all its fields have the offset 0. A fixed buffer or an `[InlineArray]` field is an array. |
| `nint`, `nuint`, `void*`, `T*` | `void*`, `T*` |

What a type library cannot describe is exported as the closest type, with a warning, so that the layout of the
interface stays the same: an array that is not marshalled as a `SAFEARRAY` as a pointer to its first element, an
enumeration of another size as its underlying type, and an interface or a class that is not a COM type of a library
as `IUnknown*`. A structure of .NET, such as `TimeSpan`, is an error.

A type library has the fields of a structure and its packing (`Pack`, 8 by default), from which the offsets and the
size follow for each platform. It cannot describe other offsets or a size, so a structure with an explicit layout
that is not a union, or with a `Size`, is exported with a sequential layout, with a warning.

## Types of generated code

An assembly often contains, or references, the code that CsCom generated from type libraries: a class of the project
implements an interface of a type library, or a method takes one of its types. These types are not exported again.
The exported library refers to them in their type libraries, as an `importlib` statement of IDL does.

```csharp
[GeneratedComClass]
[Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A3010")]
[ComSourceInterfaces(typeof(DShapeEvents))]
public partial class Renderer : IShapeEventsVt // An interface and a dispinterface of Interop.CsComTest
{
    // ...
}
```

- **Metadata:** the generated code names its type libraries in `[assembly: AssemblyMetadata("CsCom.TypeLibrary", …)]`
  attributes, in `CsComAssemblyAttributes.cs` or in the single file. Code that an earlier version of CsCom generated
  does not have them and must be generated again.
- **Finding the type libraries:** the export reads the type libraries to refer to their types. A type library is found
  in the registry by its identifier and version, unless its file is specified with `--type-library`, a
  `CsComTypeLibraryReference` item or a `CsComReference` item that is a file.
- **Loading the exported library:** a type library names the libraries that it refers to by their identifiers and file
  names, without their directories. A program therefore only loads the types of a library that is not registered when
  the file of that library is in the current directory or on the path; the export tells when a library is not
  registered. Register the type libraries that the exported one refers to where it is used.
- **Your types in a generated namespace:** a type that you declare in a namespace of generated code, which the type
  library of that code does not have, is a type of your library. When the type library is not found, every type of
  such a namespace is taken for a type of it.
- **Dispinterfaces:** the late-bound wrapper class of a dispinterface stands for the dispinterface, e.g. in
  `[ComSourceInterfaces]`.
- **Support code:** the interfaces of the support code, such as `IConnectionPoint`, are in no type library; only
  `IDispatch` and `IEnumVARIANT` are known.
- **Other assemblies:** a referenced assembly that was not generated by CsCom has a type library of its own when the
  library that the export of that assembly writes is found: the one with the identifier of its `[assembly: Guid]`,
  among the `--type-library` files or in the registry. The exported library then refers to it. Many assemblies have
  an `[assembly: Guid]` that identifies no type library; their types, like those of an assembly without one, are
  exported as the closest type, with a warning.
