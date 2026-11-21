# Interop assemblies

The source can also be a .NET interop assembly: a primary interop assembly (PIA), such as an Office PIA from NuGet or from the
global assembly cache, or an assembly that TlbImp imported from a type library. The type library does not have to be installed,
so projects can be generated on build machines:

```powershell
cscom project create Microsoft.Office.Interop.Excel.dll --reference-path C:\packages\office -o Interop.Excel
```

The model is rebuilt from the metadata of the assembly, which is not loaded for execution, following the conventions of TlbImp:
`[retval]` parameters from the return values of methods, `[lcid]` parameters from `LCIDConversion`, coclasses from the `XClass`
classes and their `ComSourceInterfaces`, `propputref` accessors from `let_X`/`set_X` pairs, the properties of dispinterfaces from
their accessors, and the native types from the marshalling information. The default interfaces that TlbImp replaced with coclass
interfaces are restored. The generated code is then the same as from the type library, except for what interop assemblies do not
keep:

- **Documentation:** help strings are not stored, so the generated code has no documentation comments from the library.
- **Modules and aliases:** TlbImp imports only the constants of modules, not their functions, and resolves typedefs.
- **Structure fields:** TlbImp imports `VARIANT_BOOL` fields as `short` and pointers to structures as `IntPtr` (`void*`).
- **Setters:** a property with only a setter is assumed to be a `propput`, or a `propputref` for interfaces, as TlbImp does not
  record which one it was; this matters only for late-bound wrappers.
- **Types:** minor distinctions are lost, e.g. `int` and `long`, or `SCODE` and `HRESULT`. Types are named and ordered as in the
  interop assemblies, e.g. `IFontDisp` of stdole for its `Font` alias, and the coclasses of referenced libraries that are only
  used as parameter types are not generated.

The assemblies that the interop assembly references, e.g. `office.dll` and `stdole.dll` for Excel, are searched next to it, in the
`--reference-path` paths, among the assemblies of the .NET runtime and in the global assembly caches of .NET Framework; `cscom.json`
records the reference paths. When the type library that the assembly was imported from is registered with the same version, CsCom
logs that generating from it keeps the documentation and the functions of modules. Assemblies with embedded interop types
("Embed Interop Types"), which contain only the members their code uses, are rejected: generate from the primary interop assembly
or the type library instead.

**Hand-written declarations.** Assemblies with `[ComImport]` interfaces and classes written for the built-in COM interop are read
too, which migrates them to source-generated COM interop. They can be internal and nested, e.g. in a `NativeMethods` class; only
they and the types they use are imported. The defaults of the built-in COM interop apply where there is no `MarshalAs`:

- **Interfaces** are dual unless `[InterfaceType]` says otherwise, and a `[ComImport]` class gets its default interface from the
  interface that names it in `[CoClass]`.
- **Parameters:** `string`, `object` and `bool` are `BSTR`, `VARIANT` and `VARIANT_BOOL`, arrays are `SAFEARRAY`s, and
  `[MarshalAs(UnmanagedType.LPStruct)] Guid` is a `GUID*`.
- **Structure fields:** `bool` is a 4-byte `BOOL`, `string` and `char` follow the `CharSet` of the structure, ANSI by default
  (`LPSTR` and a 1-byte character), and `object` is an `IUnknown*`, or a `VARIANT` with `UnmanagedType.Struct`.

What type libraries cannot describe is imported as the closest native type, with a warning: C-style arrays (`LPArray`) and
`StringBuilder` buffers become pointers to their first elements, delegates, managed classes, generic types and custom marshalers
become `IUnknown*` (or `void*` for delegates marshalled as `FunctionPtr`), enums that are not 32-bit become their underlying type,
other value types become `void*`, and explicit layouts other than unions become sequential layouts. `[Flags]` is not imported; the
`AddFlagsAttribute` element of the [configuration file](configuration.md) adds it.
