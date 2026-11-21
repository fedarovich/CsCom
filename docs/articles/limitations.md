# Limitations

- Events of the generated interfaces are connected through the sink classes rather than through events of the COM interfaces as
  with TlbImp (`app.WorkbookOpen += ...`), because C# has no extension events; `dynamic` objects support `+=` for the events
  of dispinterfaces.
- By default COM objects cannot be released manually: the generated interfaces, the late-bound wrappers and the `dynamic`
  objects share one wrapper per COM object, whose references are released when it is garbage collected, and
  `ComObject.FinalRelease()` of .NET only releases unique wrappers, which `Create(unique: true)` and the `unique`
  [marshaller of `SetMarshalling`](interface-pointers.md) create; `dynamic` objects, and the objects in `VARIANT`s and `SAFEARRAY`s
  are always shared. Finalizers do not run when the process exits, and an
  out-of-process server such as an Office application only exits after `Quit()` once all its objects are released, not only
  the application object: let them go out of scope and call `GC.Collect()` and `GC.WaitForPendingFinalizers()`.
  `ComBstr`, `ComSafeArray<T>` and the event sinks are disposable and are released at once.
- Structures keep the layout of the COM structures, so `VARIANT`, `SAFEARRAY` and interface pointer members are raw values, and
  `BSTR` members are `ComBstrValue` views that the code using the structure allocates and frees; only `DATE`, `CURRENCY` and
  `VARIANT_BOOL` members are converted.
- `SAFEARRAY`s are converted to zero-based .NET arrays of up to three dimensions, and `T[,]`/`T[,,]` require a
  [known rank](safearrays.md#known-ranks); only `ComSafeArray<T>` keeps their bounds and supports more dimensions.
- The types used from other type libraries are generated into the project, in the namespaces of their libraries; referencing
  other interop assemblies that contain them is not supported yet.
- Interop assemblies lack the documentation, the functions of modules and some type information of their type libraries, see
  [Interop assemblies](interop-assemblies.md).

The issues that are known and not fixed, with the reasons why, are listed in [Known issues](../known-issues.md).
