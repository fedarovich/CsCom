# Migrating from TlbImp

CsCom generates the same kind of types as TlbImp, from the same type libraries, but as C# code for source-generated COM
interop instead of an assembly for the built-in COM interop of .NET. Most calling code keeps working, and this article lists
what changes. The [limitations](limitations.md) of the generated code apply as well. The counterpart of TlbExp, for the
COM types that an assembly implements, is described in [Exporting type libraries](exporting-type-libraries.md).

## Replacing the interop assembly

| Before | With CsCom |
| --- | --- |
| `COMReference` items, which TlbImp imports during the build | `CsComReference` items of [CsCom.Build](msbuild.md), which `cscom reference add <Source>` adds, e.g. by ProgID: `cscom reference add Excel.Application` |
| An interop assembly from TlbImp, or a primary interop assembly such as those of Office | Generate from the type library, or from the interop assembly itself when the type library is not installed, e.g. on a build machine, see [Interop assemblies](interop-assemblies.md) |
| Hand-written `[ComImport]` interfaces and classes | Generate from the assembly that declares them, which migrates them to source-generated COM interop, see [Interop assemblies](interop-assemblies.md) |

- **Namespaces:** TlbImp names the namespace after the type library, e.g. `Scripting`; CsCom generates it into
  `Interop.<Library>` by default, e.g. `Interop.Scripting`. To keep the using directives of your code, set the namespace,
  e.g. `cscom reference add Scripting.Dictionary --namespace Scripting`, or `Namespace="Microsoft.Office.Interop.Excel"` for
  the namespace of the Office PIA. See [Sources](sources.md#several-sources).
- **Used libraries:** the types that a library uses from other type libraries, e.g. `Interop.Office` and `Interop.VBIDE` for
  Excel, are generated with it, into their own namespaces; references to other interop assemblies that contain them are not
  supported yet.
- **Runtime marshalling:** source-generated COM interop requires runtime marshalling to be disabled in the assembly that
  contains the generated code. With CsCom.Build, that is your project: its `[DllImport]` declarations lose the built-in
  marshalling of strings and `bool`, see [MSBuild integration](msbuild.md). Use `[LibraryImport]`, or generate the
  interop code into a class library of its own with `cscom project create`.

## Creating objects

TlbImp makes a coclass an interface named after it (`Dictionary`), whose `new` creates the class `DictionaryClass`. CsCom
makes it a static class with the CLSID and a `Create()` method that returns the default interface:

```csharp
// TlbImp
Dictionary dictionary = new Dictionary();

// CsCom
IDictionary dictionary = Dictionary.Create();
```

Code that used the coclass as a type uses its default interface instead, here `IDictionary`. The other ways to get an object
are methods of `ComActivator` of CsCom.Runtime:

| Built-in COM interop | CsCom |
| --- | --- |
| `Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application"))` | `ComActivator.CreateInstance<_Application>("Excel.Application")` |
| `Marshal.GetActiveObject("Excel.Application")` (.NET Framework only) | `ComActivator.GetActiveInstance<_Application>("Excel.Application")`, or `TryGetActiveInstance` |
| `Marshal.BindToMoniker(path)`, VB's `GetObject(path)` | `ComActivator.BindToMoniker<_Workbook>(path)` |

See <xref:CsCom.Runtime.ComActivator>.

## Calling members

- **Properties:** source-generated COM interfaces cannot declare properties, so they become accessor methods (`get_Name`,
  `set_Name`), with C# 14 extension properties over them. `shape.Name` and `shape.Name = "x"` keep working, with C# 14 or
  later.
- **Parameterized properties:** C# calls them as indexers only on `[ComImport]` types, e.g. `sheet.Range["A1"]` with the
  Office PIAs. In the generated code they are accessor methods, `sheet.get_Range("A1")`, unless `--named-indexers` gives
  them indexer syntax again, see [Named indexers](named-indexers.md). With C# 15, the default member, usually `Item`, is an
  extension indexer.
- **Optional parameters:** an optional `VARIANT` parameter is an `object?` that defaults to `null`, which is passed as an
  omitted argument, so it can still be left out. Default values that C# cannot express make the parameters required,
  unless `--overloads-for-default-values` generates overloads, see [Optional parameters](optional-parameters.md).
- **Arrays:** `SAFEARRAY`s become zero-based .NET arrays, also those that Visual Basic and Excel create with a lower
  bound of 1: `Range.Value2` returns an `object[,]` that starts at `[0, 0]`, where the built-in COM interop returned an array
  that starts at `[1, 1]`. `ComSafeArray<T>` keeps the native bounds, see [SAFEARRAYs](safearrays.md).
- **Dispinterfaces** become late-bound wrapper classes that call `IDispatch::Invoke`, see [Generated code](generated-code.md).

## Events

TlbImp adds the events of a coclass to its interface, e.g. `app.WorkbookOpen += ...`. C# has no extension events, so CsCom
generates a sink class per event interface instead, with .NET events and TlbImp-style delegates by default:

```csharp
// TlbImp
app.WorkbookBeforeClose += (Workbook wb, ref bool cancel) => cancel = true;

// CsCom
using var events = new AppEventsSink(app);
events.WorkbookBeforeClose += (_Workbook? wb, ref bool cancel) => cancel = true;
```

The sink is connected while it is not disposed. See [Events](events.md) for the other styles, observables and handler
interfaces.

## Releasing objects

The objects of source-generated COM interop are not runtime callable wrappers, so `Marshal.ReleaseComObject` and
`Marshal.FinalReleaseComObject` do not apply to them. By default, the generated code shares one managed object per COM
object, whose references are released when it is garbage collected, so an Office application exits after `Quit()` only
once its objects are collected: let them go out of scope and call `GC.Collect()` and `GC.WaitForPendingFinalizers()`.
Where an object must be released at once, create it as a unique object, `Application.Create(unique: true)`, or make the
results of members unique with the `SetMarshalling` element of the configuration file, and release it with
`ComMarshalling.Release`. See [Interface pointers](interface-pointers.md).

## Late binding

The built-in COM interop gives `dynamic` access to any COM object. With source-generated COM interop, the
**CsCom.Runtime.Dynamic** package provides it: `ComActivator.CreateDynamicInstance("Excel.Application")`,
`AsDynamic()` for a generated interface, and `--dynamic` generates `CreateDynamic()` for the coclasses. `dynamic` requires
dynamic code, so it does not work with Native AOT. See [Late binding with `dynamic`](dynamic.md).
