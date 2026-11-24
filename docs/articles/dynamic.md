# Late binding with `dynamic`

The COM objects created by source-generated COM interop do not support `dynamic` by themselves. The **CsCom.Runtime.Dynamic**
package provides `DynamicDispatchObject`, which resolves members by name with `IDispatch::GetIDsOfNames` and invokes them with
`IDispatch::Invoke`. It is a separate package, because the C# runtime binder needs dynamic code generation, which the typed
interop of CsCom.Runtime avoids:

- **Opting in:** `--dynamic` (`-d`), or the `CsComDynamic` property of [CsCom.Build](msbuild.md), generates
  `CreateDynamic()` for the coclasses. With `--support-code package` the generated project references CsCom.Runtime.Dynamic,
  and with `public` or `internal` its code is emitted into the project. Without the option, neither is.
- **Using it directly:** the package can also be referenced on its own, e.g. by a project that only automates Office with
  `dynamic`. The activation methods are static extension members of `ComActivator` (C# 14), in `ComDynamic`:

```csharp
dynamic fso = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject"); // by ProgID or CLSID, no interop types needed
dynamic dictionary = Dictionary.CreateDynamic();   // generated with --dynamic for coclasses that support IDispatch
dynamic shape = typedShape.AsDynamic();            // any IDispatch-based interface or late-bound wrapper
dynamic tag = ComDynamic.ToDynamic(typedShape.Tag); // an object returned in a VARIANT
dynamic excel = ComActivator.GetActiveDynamicInstance("Excel.Application"); // a running instance
dynamic book = ComActivator.BindToDynamicMoniker(@"C:\Data\Book.xlsx");      // VB's GetObject: a document, winmgmts:, ...

dictionary.Add("a", 1);                            // methods
int count = dictionary.Count;                      // properties
object item = dictionary.Item("a");                // parameterized properties are invoked like methods
dictionary.Item["a"] = 3;                          // or indexed, also to set them, like sheet.Range["A1"].Value = 5
dictionary["b"] = 2;                               // indexers and invocations use the default member (DISPID_VALUE)
string path = fso.BuildPath(Path: @"C:\a", Name: "b"); // named arguments
calculator.Swap(ref a, ref b);                     // ref and out arguments
string drive = fso.GetFolder(@"C:\Windows").Drive.DriveLetter; // returned COM objects are dynamic too
foreach (var key in dictionary) { }                // _NewEnum
IDispatch dispatch = dictionary;                   // conversions to IDispatch, the implemented COM interfaces
DShapeEvents events = shape.GetEvents();           // and the late-bound wrappers of dispinterfaces
```

- **Returned objects:** COM objects returned by dynamic calls are dynamic as well, so calls can be chained. There is one
  dynamic object per COM object, so `==` compares COM objects (`if (sheet == excel.ActiveSheet)`), like the runtime
  callable wrappers of the built-in COM interop. The dynamic objects are attached to the managed objects of the COM
  objects and do not keep them alive. An object of yours that is exposed to COM (`[GeneratedComClass]`), which a COM
  object returns as itself, is made dynamic like by `ComDynamic.ToDynamic`, also in events and `foreach`: itself when it
  implements `IDispatch`, and otherwise the COM object of its `Dispatch`; one that has neither is returned as it is.
- **Properties:** assigning an object to a property or an indexer uses `PROPERTYPUTREF`, like VB's `Set`, and a property
  put when the object has no such setter: a COM object, a late-bound or `dynamic` wrapper, an object of yours that is
  exposed to COM (`[GeneratedComClass]`) or an `UnknownWrapper`, i.e. every value that is passed as an object. A property
  that requires arguments, or can only be set, can be indexed with its arguments: `dictionary.Item["a"]`,
  `dictionary.Key["a"] = "b"`. So can a property whose parameters are all optional, such as
  Excel's `Range.Value`, when the object has type information: `range.Value[xlRangeValueMSPersistXML]`. Without type
  information such a property is read at once and brackets index its value; pass the arguments in parentheses instead.
- **Methods:** a method that is read without parentheses is not invoked when the type information of the object describes it
  as a method: `var quit = app.Quit;` gives a value to call, `quit()`. Without type information, reading a member invokes it.
- **Named arguments:** they work in invocations and indexes, e.g. `sheet.Cells[ColumnIndex: 2, RowIndex: 1]`. Those of the
  default member (`d[index: 1]`, `d(index: 1)`) need the type information of the object, which gives the name of the member;
  without it, or when it describes no default member, they throw `NotSupportedException`.
- **`ref` and `out` arguments:** they receive the new values. A variable of a COM type is passed as a reference to its type,
  e.g. an `int` as `VT_BYREF | VT_I4`, which typed by-reference parameters such as `long*` require, so its type must match the
  parameter: `int` for `long`, `double` for `double`, `bool` for `VARIANT_BOOL`, `string` for `BSTR`. An `object` or `dynamic`
  variable is passed as a reference to a `VARIANT`, for `VARIANT*` parameters.
- **Running objects and monikers:** `ComActivator.GetActiveInstance<T>` and `GetActiveDynamicInstance` return an instance
  registered in the running object table, like `Marshal.GetActiveObject` of .NET Framework, which .NET does not have;
  `TryGetActiveInstance<T>` and `TryGetActiveDynamicInstance` return `false` instead of throwing when no instance is running
  or the ProgID is not registered.
  `BindToMoniker<T>` and `BindToDynamicMoniker` bind a moniker display name, like VB's `GetObject`: the path of a document,
  which its application opens, `winmgmts:` for WMI or `new:ProgID` for a new instance. Office applications register
  themselves only after their window has lost the focus once, so an instance that has just been started may not be found.
- **Events:** `+=` and `-=` add and remove handlers of the events of the source dispinterfaces of the object's coclass, found
  through `IProvideClassInfo` or, without it, through the first coclass of the type library whose default interface is that
  of the object. An object of yours that is exposed to COM (`[GeneratedComClass]`) is late bound itself when it implements
  `IDispatch`, its events included, and otherwise through the COM object of its `Dispatch`. A handler is a delegate with at
  most the parameters of the event, so a lambda needs a cast. Its `ref` and `out` parameters and its result are returned to
  the COM object:

  ```csharp
  excel.WorkbookOpen += (Action<dynamic>)(book => Console.WriteLine(book.Name));
  excel.WorkbookBeforeClose += new BeforeCloseHandler((dynamic book, ref bool cancel) => cancel = true);
  ComDynamic.AddEventHandler(book, "Activate", handler); // also for events named like methods, e.g. Workbook.Activate
  ComDynamic.RemoveEventHandlers(excel);                 // removes all handlers of the object

  delegate void BeforeCloseHandler(dynamic book, ref bool cancel);
  ```

  The object is connected to the event interface while it has handlers, so remove them, on the thread that added them, when
  they are no longer needed: the COM object holds the handlers, and handlers that refer to the object keep both alive. Only
  dispinterfaces are supported; dual and vtable event interfaces need the generated sinks (see [Events](events.md)).

  Adding and removing handlers is safe from several threads and from within a handler, and never waits for another
  thread:

  - `+=` returns only when the object is connected, and throws without adding the handler when it cannot be connected;
    a connection error is reported by the `+=` that caused it.
  - `-=` and `RemoveEventHandlers` never fail. A removed handler gets no more events, also when the object cannot be
    disconnected at the moment, e.g. because the server is busy or the call comes from another apartment: the connection
    then passes no events on and is disconnected again by the next `+=`, `-=` or `RemoveEventHandlers` of that object.
    Any other error than the transient ones listed in [Events](events.md#transient-errors) gives the connection up, also the exception of a
    connection point that is a managed object, which counts as its `HRESULT`: the source keeps the closed sink until it
    releases it.
  - `+=` and `-=` of the same handler on several threads take effect one after another, each at some moment between its
    call and its return, as for a .NET event: a `-=` that runs while a `+=` on another thread has not returned yet can
    come before it, and then leaves the handler of that `+=`. A `+=` that fails adds nothing, also when a `-=` removed
    its handler meanwhile.
  - Each event reaches each handler once, with the limitations below: when several threads connect the object at the same
    time, only one connection passes the events on, and the others are disconnected.

  Limitations: an event that is running on another thread can still call a handler after `-=` returns, if it had reached
  the handler before; it does not call one that was removed before it reached it. When several threads connect the first
  handlers at the same time, an event that a multi-threaded object raises meanwhile can be missed, and when a connection
  that has already received events then fails to connect, the events around the switch to another connection can be
  missed or doubled.
- **Releasing objects:** the COM references of the `dynamic` objects are released when the objects are garbage collected, and finalizers do not run
  when the process exits. An Office application exits after `Quit()` only when all its objects are released, so let them go
  out of scope and call `GC.Collect()` and `GC.WaitForPendingFinalizers()`, otherwise it keeps running for minutes. This
  applies to the objects of the generated interfaces and late-bound wrappers too, see [Limitations](limitations.md).
- **Debugger:** the dynamic view of the debugger lists the properties described by the type information of the object, and
  `ComDynamic.GetTypeName` returns the name of its type, like VB's `TypeName`.
- **Errors:** unknown members throw `RuntimeBinderException`. Errors raised by the COM object throw `COMException`, and so do
  failed invocations, with messages that name the member and the argument, e.g. "Type mismatch in argument 2 of 'BuildPath'."
  As in the late-bound wrappers, an error that only has a 16-bit code, as Basic raises them, has the `HRESULT` that Visual
  Basic gives it: `0x800A0000` plus the code, e.g. `0x800A000B` for the error 11.

The C# runtime binder requires dynamic code, so `dynamic` cannot be used with Native AOT: the entry points are annotated with
`[RequiresDynamicCode]`, and the typed interop stays trimming and AOT compatible.
