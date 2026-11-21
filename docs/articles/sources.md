# Sources

The commands of the cscom tool and the `CsComReference` items of [CsCom.Build](msbuild.md) take one or more
sources, the type libraries to generate the interop code from.

A source (`Source`, which can be repeated, see [Several sources](#several-sources)) can be:
- **A file:** a `.tlb` file, or a `.dll`, `.ocx` or `.exe` file with an embedded type library. A resource index can be appended,
  e.g. `foo.dll\2`.
- **A registered LIBID:** `{00020813-0000-0000-C000-000000000046}`, optionally with the version as in the registry
  (`{00020813-0000-0000-C000-000000000046}:1.9`). By default, the highest registered version whose file exists is used,
  whether it is registered for 64-bit or for 32-bit applications.
- **The ProgID of a registered class,** e.g. `Excel.Application` or `Scripting.Dictionary`, whose type library is used.
- **A .NET interop assembly,** such as a primary interop assembly or the output of TlbImp, see
  [Interop assemblies](interop-assemblies.md).

Registered type libraries are found in the registry, so Office type libraries don't have to be looked up inside the Office
installation. `cscom.json` records the LIBID or the ProgID rather than the path, and `project update` looks it up again, e.g.
after an Office update.

## Several sources

The commands generate the sources together into one project, assembly or package, as [CsCom.Build](msbuild.md) does:

```powershell
cscom project create Excel.Application Scripting.Dictionary -n Office.Interop --ns Excel=Office.Excel -c Excel=excel.cscom.xml
```

- **Namespaces:** each library is generated into `Interop.<Library>`, after the name of its type library (e.g. `Interop.Scripting`
  for `scrrun.dll`), with the characters that a namespace cannot have replaced by underscores and an underscore after a C# keyword
  (e.g. `Interop.event_`), into a folder of the same name in `Generated`, e.g. `Generated/Interop.Scripting/IDictionary.cs`. The file
  of a type named like a device, which Windows cannot create, gets a trailing underscore, e.g. `Aux_.cs`.
- **Used libraries:** the types that the sources use from other type libraries are generated into the namespace of their library,
  e.g. `Interop.stdole`, or `Interop.Office` and `Interop.VBIDE` for Excel: only the used types, once for all the sources that use
  them. When the other library is a source too, all its types are generated and the other sources use them.
- **Options by library:** `--namespace` and `--config` take the name of the library: `--ns Excel=Office.Excel` or
  `-c stdole=stdole.cscom.xml`, also for the libraries that are only used. Without a library name, they apply to a single source,
  except that `--namespace` is the root namespace of several sources. Two libraries with the same namespace are an error.
- **Name:** `--name` is required with several sources; with a single source, it is `Interop.<Library>` by default.
- **Root namespace:** `--ns` without a library name sets the root namespace of several sources: the `RootNamespace` of the
  project, and the namespace under which the support code of `--support-code public` or `internal` is generated, once, into
  `<RootNamespace>.ComSupport`. The libraries keep their own namespaces. It is `Interop.<Name>` by default, e.g. `Interop.Office`
  for `-n Office`, with the characters of the name that a namespace cannot have replaced by underscores and an underscore after a
  C# keyword (e.g. `Interop.Set.class_` for `-n Set.class`); a name that is `Interop` or starts with `Interop.`, in this case, gets
  no second prefix. The root namespace of a single source is its namespace. A namespace of the
  generated code cannot be the name of a generated type: with `--support-code public` or `internal`, `-n Scripting.Dictionary`
  is an error, because the support code would be in `Interop.Scripting.Dictionary.ComSupport` next to the coclass
  `Interop.Scripting.Dictionary`; choose another root namespace with `--ns`.
- **Single file:** `--single-file` generates one file with a namespace block per library.

A single source is generated the same way. The projects of earlier versions, which generated the types used from other libraries
into nested namespaces (`<Namespace>.<Library>`), are converted by `project update`.
