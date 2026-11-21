# Changelog

The notable changes of CsCom, CsCom.Build, CsCom.Runtime and CsCom.Runtime.Dynamic, which are released together with the
same version. The versions up to 2.0.0 are not described here.

## 4.0.0 - 2026-11-20

### Added

- **Documentation:** the documentation is a site, [cscom.fedarovich.com](https://cscom.fedarovich.com/), with the API
  reference of CsCom.Runtime and CsCom.Runtime.Dynamic and new articles on getting started and on migrating from
  TlbImp; the README links to it.

## 4.0.0-rc.2 - 2026-11-20

### Changed

- **Root namespace of several sources:** `project create`, `assembly build` and `assembly pack` give a project of several
  sources the root namespace `Interop.<Name>`, e.g. `Interop.Office` for `-n Office` (and `Interop.Office` for
  `-n Interop.Office` too), and generate the support code of `--support-code public` or `internal` into
  `<RootNamespace>.ComSupport`; both were the namespace of the first source, e.g. `Interop.Excel` and
  `Interop.Excel.ComSupport`. A single source is unchanged: its namespace is the root namespace.
- **The support code of several sources no longer moves:** `cscom.json` records the root namespace, which `project update`
  keeps when the order or the number of the sources changes, also down to a single source. The support code followed the
  first source, also when the sources were only listed in another order. The projects of several sources of earlier
  versions, and the projects of a single source that get more sources, keep the namespace of their first source, which
  the update records.

### Added

- **`--namespace` without a library name with several sources** sets their root namespace, which was an error. `project
  update --namespace` without a library name also sets the `RootNamespace` of the project file, when it has one, with a
  single source too, whose namespace it is; `LIBRARY=NAMESPACE` leaves the project file alone.

### Fixed

- **A library named like a C# keyword**, e.g. `event`, gets the namespace `Interop.event_`: its namespace was
  `Interop.event`, which does not compile.
- **A namespace that is the name of a generated type** is reported as an error, e.g. the support code in
  `Interop.Scripting.Dictionary.ComSupport` next to the coclass `Interop.Scripting.Dictionary`, or the namespace of a
  library named like a type of another library: the generated code did not compile (CS0101).

## 4.0.0-rc.1 - 2026-11-20

### Changed

- **Interface pointers:** the interface pointers in the parameters and return values of the generated interfaces are
  marshalled by `ComObjectMarshaller<T>` of CsCom.Runtime, which behaves like `ComInterfaceMarshaller<T>` of .NET unless
  `ComMarshalling.ComWrappers` is set. `IUnknown` pointers had `ComInterfaceMarshaller<object>`, the other pointers no
  marshaller.
- **Managed objects in `VARIANT`s:** `VariantMarshaller` exposes a managed object whose class is a `[GeneratedComClass]`
  itself, as `VT_DISPATCH` when it implements `IDispatch`, and the object of an `UnknownWrapper` as `VT_UNKNOWN`; they
  were left to `ComVariantMarshaller` of .NET. `dynamic` assigns both to a property with `PROPERTYPUTREF`, like a COM
  object; it assigned them by value. An `UnknownWrapper` stands for its object in an `IUnknown` parameter, a `SAFEARRAY`
  and as the source of events too, which used a COM interface of the wrapper itself, and for `ComDynamic` and
  `ComEnumerator.FromObject`, which threw an `InvalidCastException`; a `DispatchWrapper` is rejected there with an
  `ArgumentException`, like in a `VARIANT`.
- **Events:** `IConnectionPointContainer.FindConnectionPoint` of CsCom.Runtime returns the connection point in a unique
  object, and `EventConnection` releases it when the sink is disconnected, so that a disconnected sink does not keep its
  source alive until the garbage collection. `EventConnection.Disconnect` returns `S_FALSE` while another call is
  disconnecting the sink, without waiting for it; it returned `S_OK`, also when that call then left the sink connected.
  It returns any success of `IConnectionPoint::Unadvise` as `S_OK`, so that `S_FALSE` means nothing else.
  A connection that a busy server refuses to disconnect, e.g. with `RPC_E_CALL_REJECTED` or
  `RPC_E_SERVERCALL_RETRYLATER`, stays connected like one that is disconnected from the wrong thread, and can be
  disconnected by another call; it was given up. `EventConnection.IsConnected` tells whether the sink is still
  connected. `Disconnect` never throws: the exception of a connection point that is a managed object is returned as its
  `HRESULT`, as through COM. A `Disconnect` after the connection has been given up returns the error that ended it; it
  returned `S_OK`. `EventConnection` no longer releases a connection point that a managed source returned. A source
  that fails with another error than one that says that it does not raise the events, e.g. a server that has ended or
  is busy, throws the exception of its `HRESULT`, a `COMException`, which can be tried again; it threw an
  `InvalidOperationException` that said that the object does not raise the events, also for `dynamic` objects and the
  generated sinks.
- **Events stop at once:** every connection closes its gate first when it is disconnected or cannot be established; once
  it is closed, no handler call of the generated sinks and of the sinks of `AdviseDispatch` starts, also when the source
  could not be told and still calls the sink.
- **Generated sink classes** implement `IAsyncDisposable`, so a `DisposeAsync` of your own in a partial part of one
  conflicts with theirs, and an event named `DisposeAsync` is renamed like the other reserved names. `Dispose` of the
  observable style completes the observables once no handler runs any more: at once, or on the thread of a running
  handler when it returns, where the exception of an observer fails that event call like one of `OnNext`. The exception
  of an observer no longer stops the completion of the other observers and observables: once all of them are completed,
  it is thrown as it is, or in an `AggregateException` when several observers threw; so does
  `EventSubject<T>.OnCompleted`.
- **Events of `dynamic` objects:** `+=` returns only when the object is connected, and throws without adding the handler
  when its connection fails, also when another thread adds or removes handlers at the same time; a `+=` that fails
  removes only the handler that it added, not an equal one that another thread added. `-=` and `RemoveEventHandlers`
  never fail, stop the events at once, and retry a disconnection that the server refused with the next change of the
  handlers; any other error gives the connection up, also the exception of a connection point that is a managed object,
  which counts as its `HRESULT`. A running event does not call a handler that was removed before the event reached it;
  one that it had reached can still be called after `-=` returns. When several threads connect the object at the same
  time, only one connection passes the events on: the first that receives an event, or else the first that is
  established; the README describes the events that can still be missed or doubled then.
- **Interfaces that are no COM interfaces:** `ComObjectMarshaller<T>`, `UniqueComObjectMarshaller<T>` and
  `EventArguments.GetUnique<T>` fail with a `NotSupportedException` for an interface that is not a
  `[GeneratedComInterface]`, also for `null`, and the marshallers with an `ObjectDisposedException` for a COM object
  that has been released. `ComActivator` fails with the `NotSupportedException` too, also for a class other than
  `object` and `ComObject`, e.g. a late-bound wrapper, before anything else: before it looks up a ProgID, creates or
  gets the object; it threw an `InvalidCastException` after starting the server.
- **Enumerators:** when `SetMarshalling` makes `_NewEnum` unique, the `GetEnumerator()` of a generated interface
  releases the enumerator of a COM object when it is disposed, e.g. at the end of `foreach`, also the COM object of a
  late-bound wrapper when `_NewEnum` returns a dispinterface; one that a managed implementation of the interface returns
  is not released, nor is a wrapper of yours that is exposed to COM. `ComEnumerator` has a constructor and a
  `FromObject` overload with `ownsEnumerator` for it. Without it, `Dispose()` still does nothing. `FromObject` takes
  what a value stands for, like `ComMarshalling.Release`: the COM object of a late-bound or `dynamic` wrapper, which it
  releases with `ownsEnumerator`, and an object that is exposed to COM itself or, when it is no enumerator, through the
  `Dispatch` that it provides, which it never releases. When asking a COM object for `IEnumVARIANT` fails with another
  error than `E_NOINTERFACE`, e.g. of a server that has ended, `FromObject` throws the exception of its `HRESULT`; it
  threw an `InvalidCastException` that said that the object does not implement `IEnumVARIANT`.
- **Late-bound wrappers and `dynamic` objects** passed to an `IUnknown` parameter of a generated interface are passed as
  their COM objects, as in a `VARIANT`; such a parameter got a COM interface of the managed wrapper. So is a late-bound
  wrapper of yours in a parameter of a COM interface that it implements itself, e.g. `IDispatch` by forwarding to its
  `Dispatch`, whose COM object is asked for the interface, or the COM object that an object of yours in its `Dispatch`
  provides; it failed with an `InvalidCastException`. A COM object that fails `QueryInterface` with another error than
  `E_NOINTERFACE` there, e.g. the proxy of a server that has ended, throws the exception of its `HRESULT`; it threw an
  `InvalidCastException` that said that the object does not implement the interface. A managed object
  that is exposed to COM (`[GeneratedComClass]`) is passed itself, also when it implements `IDispatchProvider`, in a
  `VARIANT`, a `SAFEARRAY` and an `IUnknown` parameter alike, so that its pointer converts back to it; a `VARIANT` held
  its `Dispatch`. A `SAFEARRAY` of `IDispatch` still gets its `Dispatch` when it does not implement `IDispatch` itself.
  As the source of events, such an object is connected itself when it implements `IConnectionPointContainer`, and its
  `Dispatch` otherwise, as it always was. A late-bound wrapper converts it back to an interface that it implements, and
  otherwise asks the COM object of its `Dispatch`; a dispinterface wrapper, `ComDynamic` and the results of `dynamic`
  calls wrap it itself when it implements `IDispatch`, events included, and its `Dispatch` otherwise. Such a wrapper
  stands for it with the same rule, also as the source of events, in `ComEnumerator.FromObject` and in the conversions
  of late-bound wrappers to interfaces. A late-bound wrapper of your own that is such an object is passed itself as a
  parameter of its type too when it implements `IDispatch`, and converts back to itself. The pointer of such an object
  converts back to it only for an interface that it implements: for another declaration of the interface, e.g. of
  another interop assembly, it gets a COM object of its own, which calls it through COM: a new one for each conversion,
  also of a shared parameter, which `ComMarshalling.Release` releases. It failed with an `InvalidCastException`, as with
  `ComInterfaceMarshaller<T>` of .NET. Such an object that comes back from a `VARIANT` or a `SAFEARRAY` itself gets a
  COM object of its own too where a late-bound wrapper, `EventArguments.Get<T>` or an array converts it to another
  declaration of the interface, e.g. an argument of a dual event that the source raises through `IDispatch::Invoke`;
  it failed with an `InvalidCastException`.
- **`IDispatchWrapper<T>`** derives from `IDispatchProvider`, which declares its `Dispatch` property, so that a
  late-bound wrapper of your own is passed as its COM object and released by `ComMarshalling.Release` like a generated
  one; a `VARIANT` rejected it, and an `IUnknown` parameter got a COM interface of the wrapper. A wrapper that implements
  `IDispatchWrapper<T>.Dispatch` explicitly implements `IDispatchProvider.Dispatch` instead.
- **`SetMarshalling`** warns that nothing matches when none of its attributes applies to the matching types: only
  `SafeArrays` applies to dispinterfaces, and only `Dates`, `Currencies`, `Booleans` and `Bstrs` to structures; `Interfaces`
  only applies to interface pointers that are not passed in and out. Such an element had no effect and no warning.

### Added

- **Unique objects:** the methods of `ComActivator` (`CreateInstance<T>`, `CreateDispatchInstance<T>`,
  `GetActiveInstance<T>`, `TryGetActiveInstance<T>` and `BindToMoniker<T>`) have overloads with a `unique` parameter, which
  return a COM object in a managed object of its own, and so has `Create` of the generated coclasses, e.g.
  `Dictionary.Create(unique: true)`. `ComMarshalling.Release` of CsCom.Runtime, like
  `ComObject.FinalRelease()` of .NET, releases such an object at once, without waiting for the garbage collector; it also
  takes a late-bound wrapper and the object of an `UnknownWrapper`, rejects a `DispatchWrapper`, and leaves a managed
  object that is exposed to COM (`[GeneratedComClass]`) alone, also when it implements `IDispatchProvider`;
  `ComMarshalling.IsExposedToCom` tells whether an object is one. A unique object whose COM object does not implement
  the requested interface is released before the `InvalidCastException` is thrown.
- **Unique interface pointers:** the `Interfaces` attribute of `SetMarshalling` selects `unique` objects for the interface
  pointers of single methods and parameters, with `UniqueComObjectMarshaller<T>` of CsCom.Runtime. Such an object belongs
  to the code that receives it. A pointer that is passed in and out (`ref`) is always a shared object, because it is
  passed back. A handler of a dual event interface gets the unique object also when the event is raised through
  `IDispatch::Invoke`, which `EventArguments.GetUnique<T>` and `GetUniqueWrapper<T>` read from its `VARIANT`.
- **Custom `ComWrappers`:** `ComMarshalling.ComWrappers` sets the `StrategyBasedComWrappers` that creates the managed
  objects of the generated code and of the support code: of the interface pointers of the generated interfaces, of
  `ComActivator`, of `VARIANT`s and `SAFEARRAY`s, of the late-bound wrappers and of the event sinks.
- **Event gates:** every `EventConnection` has an `EventGate` of its own, which a sink enters with `TryEnter` and exits
  with `Exit` around each handler call. `EventConnection.Advise(source, iid, Func<EventGate, object>)` creates a
  hand-written sink with the gate of its connection, and `DispatchEventSink.InvokeHandler(gate, handler, …)` passes an
  invocation of `IDispatch::Invoke` to a handler through the gate. `EventConnection.DisposeAsync` and `DisposeAsync` of
  the generated sink classes disconnect, and complete when no handler call that entered the gate is running any more;
  the observable style then completes the observables, in the synchronization context of the caller. Unlike `Dispose`,
  they throw a `COMException` then when the sink could not be disconnected, e.g. after an `await` that resumed on
  another thread than that of a single-threaded apartment object, and no other call has disconnected it by then; when
  an observer throws too, the observable style throws both in an `AggregateException`, the `COMException` first.
  `EventConnection.DisposeWithCompletion(Action completion)` disconnects without waiting, and calls the completion then:
  at once, or on the thread of the last running handler call when it returns. The completions run one at a time, in the
  order of the calls, and an exception of one does not stop the others. `EventSubject.Complete` and
  `EventSubject.CompleteAfterAsync`, of the new base class of `EventSubject<T>`, complete several observables that way.

### Fixed

- **Collections whose `_NewEnum` returns a dispinterface:** the `GetEnumerator()` of a generated interface and
  `ComEnumerator.FromObject` enumerate the COM object of the late-bound wrapper; they threw an `InvalidCastException`.
- **Objects and arrays passed by reference in `VARIANT`s:** an object passed by reference whose pointer is null, like a
  `ByRef` object variable of Visual Basic that is `Nothing`, is `null`, and so is an object, an array or a `VARIANT` passed
  by reference without a reference, also as the argument of an event; they failed.
- **The `public` and `internal` support code** compiles when a type of the library or of the application in or around
  its namespace has the name of a type of .NET that it uses, e.g. `Action` of Excel, `Exception` of Outlook or `Task`
  of Word, also with a `CsComSupportNamespace` of your own, and when a namespace around it is named `System`: it names
  the types of .NET with `global::` and calls their extension methods through their classes, and imports them in its
  own namespace.
- **`EventConnection.AdviseDispatch` with the identifier of `IDispatch`,** e.g. for a source whose generic connection
  point raises the events through `IDispatch` itself, overflowed the stack: the sink asked itself for `IDispatch`
  without end.
- **Exceptions of the handlers of dispinterface events** (`DispatchEventSink.InvokeHandler`): one whose `HResult` is no
  error code, e.g. a `COMException` of `S_FALSE`, is returned to the source as `E_FAIL`, also in the error information;
  the source took it for a success. One whose `Message` throws is returned without its description; the event call
  failed with the exception of the message instead, and leaked the string of its source.

## 3.0.0 - 2026-11-13

### Changed

- **Named indexers of late-bound wrappers:** the indexer of the proxy of a property that only has a setter declares the
  default values of its optional indexes, e.g. `sheet.Note["A1"] = text` with an optional second index.

### Fixed

- **Null objects in `VARIANT`s:** a `VARIANT` of the type of an object whose pointer is null, like `Nothing` of Visual
  Basic, is `null`; it failed with an `ArgumentNullException`, e.g. as the result of a late-bound call, as an `out` object
  that the callee leaves null, or as an argument of an event.
- **Late-bound calls:** the `COMException` of an error without any code is `DISP_E_EXCEPTION`; since 3.0.0-rc.1 it was
  `0x800A0000`.
- The class of the marshallers of the arrays with a known rank, `CsComSafeArrayMarshallers`, could have the name of a type
  of the library; it gets a trailing underscore then, like the other generated types.
- An interface that derives from an interface without methods whose own base interface has no GUID got no placeholders for
  the vtable slots of that base, so its methods were called at the wrong slots.
- **`assembly build` and `assembly pack`:** since 3.0.0-rc.1, `dotnet` was taken from `DOTNET_ROOT` before the directories
  of `PATH`, which fails when `DOTNET_ROOT` is an installation without the SDK; `PATH` comes first again. Ctrl+C had no
  visible effect while the code was generated; a second Ctrl+C now ends the tool at once.
- `DispatchObject.InvokeByRef` and `InvokeMethodByRef` without the types of the arguments failed with an
  `IndexOutOfRangeException` when the object rejected an argument with a type mismatch; the `COMException` is thrown again.

## 3.0.0-rc.1 - 2026-11-13

### Changed

- **Property setters with optional indexes:** a `null` optional `VARIANT` index of a setter is omitted, like that of the
  getter; it was passed as an empty `VARIANT`. This applies to the `set_` and `putref_` methods of the interfaces and the
  late-bound wrappers and to the indexers that call them. The index parameters of the setter methods stay required in C#,
  since the value follows them.
- **Late-bound calls:** the `COMException` of an error that only has a 16-bit code (`wCode`), as Basic raises them, has the
  `HRESULT` that Visual Basic gives it, e.g. `0x800A000B` for the error 11; it was `0x8004000B`.
- **`assembly build` and `assembly pack`:** Ctrl+C stops the build with the processes that it started and deletes the
  temporary project, which was left behind. The temporary projects that are kept (`--keep-temp`, or a build that failed)
  are deleted by a run of these commands a week later.

### Added

- `AssemblyGenerator.BuildAssembly` and `PackAssembly` take a `CancellationToken`.

### Fixed

- A method named like its interface made the generated interface fail to compile; it gets a trailing underscore, like the
  members of the late-bound wrappers already did.
- The types that are generated next to an interface (the extensions class, the event handler interface, the event sink and
  the delegates or the classes of the event arguments) could have the name of a type of the library, e.g. `IShapeExtensions`.
  Such a generated type gets a trailing underscore.
- An `object` field without `MarshalAs` of a structure of an interop assembly was read as a `VARIANT`, which gave the
  structure the wrong layout. The runtime marshals it as an `IUnknown` pointer, and so does the generated structure; a field
  with `UnmanagedType.Struct`, which TlbImp writes, stays a `VARIANT`.
- **CsCom.Build:** the generated files of an output folder (`CsComOutputPath`) whose path has a `%` followed by two
  hexadecimal digits, e.g. `Generated%20Code`, were passed to the compiler with the wrong paths. A project whose own
  directory has such a name cannot be built anyway: the .NET SDK fails to restore it.
- An event sink cleared the result variable of the source before it set the result of an event, which fails or frees memory
  that is not allocated when the source did not initialize the variable. The sinks initialize it now, and a handler that
  fails leaves no result behind.

## 3.0.0-beta.2 - 2026-11-12

### Changed

- **CsCom.Build:** the state of the generation (`cscom.state`, `cscom.request.json`) is always kept in the intermediate
  output. A folder of the project that the code is generated into (`CsComOutputPath`) only gets the generated files, and
  the state files that earlier versions wrote there are deleted. A rebuild now generates the code again in that mode.
- **CsCom.Build:** the code is generated before the build records the items of the compiler, so the build after the first
  generation no longer compiles the project again.
- **`cscom.json`** records the type libraries and interop assemblies of the project directory by their relative paths, so a
  project can be updated from another copy of its directory. Files outside of the directory keep their full paths.
- **`project create`** fails when a package source or a packages folder is specified and the directory already has a
  `nuget.config`, which it used to replace.
- **`SetType`** only requires its `To` type when something matches its selectors; otherwise it reports that nothing
  matches, like the other transformers. `To` can name a type of another library that is generated with the library.
- **Late-bound wrappers** pass a by-reference `SAFEARRAY` as a reference to a `VARIANT` first and, when the object rejects
  that argument, as a reference to the `SAFEARRAY` of the declared type.
- **Generated interfaces:** an `[out, retval] void*` parameter stays a parameter and is no longer returned as a `void*`.
- **Generated interfaces:** a default value that does not fit the type of its parameter is not generated; the parameter is
  required. A value stored as an unsigned number with the bits of a 32-bit value keeps its bits, e.g. `0xFFFFFFFF` is `-1`.
- **Registered type libraries:** the highest registered version whose file exists is used, whichever view of the registry
  (64-bit or 32-bit) it is in.
- **The merged models of library sets** (`LibrarySetGenerator.Transform`) have no imported types: `ImportedFrom` is cleared
  like the lists of the imported libraries and files. `TypeDefinition.ImportedFrom` can be set.

### Added

- `LibrarySetGenerator.Transform` has an overload that returns the files that were read.
- `TransformationContext.Libraries`: the models of the libraries that are generated together.
- `EventConnection.Disconnect` returns the `HRESULT` of the disconnection. A connection that could not be disconnected
  from another thread (`RPC_E_WRONG_THREAD`) stays usable and can be disconnected from the right thread.
- `BuildRequest.StateDirectory` and the `StateDirectory` parameter of the `CsComGenerate` task.

### Fixed

- The references to an interface without a GUID, which is not generated, made the generated code fail to compile. Pointers
  to it are now `IUnknown` or `IDispatch` pointers, and an interface that derives from it gets placeholders for its vtable
  slots.
- `SafeArrayMarshaller<T>` failed to convert the returned arrays of `ComDate`, `ComCurrency` and `ComBool`.
- `ComDispatch.Invoke` with arguments passed by reference and no output failed after the member was invoked; the output is
  checked before the invocation.
- A COM object implemented in .NET that replaced the `SAFEARRAY` of an `[in, out]` parameter leaked the new array when the
  array of the caller could not be destroyed.
- The tool ran `dotnet` by its name when `DOTNET_HOST_PATH` was not set, which Windows looks for in the current directory
  first. It is now run by its full path.
- The file of a type or the folder of a namespace named after a device, e.g. `Aux` or `Con`, could not be written; such
  files and folders get a trailing underscore, e.g. `Aux_.cs`.
- A help string with a Unicode line or paragraph separator or a next-line character ended its documentation comment, so
  the rest of it was generated as code.
- A dispinterface property that a configuration renamed to the former name of a renamed method got the new name of the
  method.
- A `SAFEARRAY` whose number of elements does not fit a .NET array is rejected; the number was computed without a check.
- Setting a `VARIANT` element of a `SAFEARRAY` whose old value cannot be cleared leaked the new value.
- The event sink of a source interface that derives from `IEnumVARIANT` did not implement the methods of `IEnumVARIANT`, so
  the generated code failed to compile. They return `E_NOTIMPL`.

## 3.0.0-beta.1 - 2026-11-12

Code generated by an earlier version must be generated again and used with the CsCom.Runtime of this version.

### Changed

- **Late-bound wrappers** pass `ref` and `out` arguments as references of the types of the parameters, e.g. `VT_BYREF | VT_I4`
  for a `long*`, which the objects that check the arguments with their type information require. The generated code calls
  new overloads of `DispatchObject.InvokeMethodByRef` and `InvokeByRef`.
- **Late-bound wrappers** pass a single array argument as one argument; its elements were passed as separate arguments.
- **`[lcid]` parameters** of interface methods default to `0x0400` (`LOCALE_USER_DEFAULT`), so the optional parameters
  before them keep their default values. Properties and indexers pass this value and no longer have the `[lcid]` parameter
  as an index.
- **`VARIANT` parameters with a default value** of interface methods are required: C# cannot express the value as the
  default of an `object` parameter, and nothing applies it in a call through the vtable. With
  `--overloads-for-default-values`, an overload passes the value.
- **Pointer parameters without `[in]` and `[out]`** are `ref` parameters, like those of TlbImp; they were `in` parameters
  or, in late-bound wrappers, passed by value.
- **Interfaces derived from `IEnumVARIANT`** derive from it, so that their methods have the right vtable slots.
- **`dynamic`:** with the type information of the object, a method that is read without parentheses is not invoked but
  gives a value to call, and a property whose getter takes arguments is indexed with them, also when all of them are
  optional (`range.Value[...]`). A property whose read once failed with `DISP_E_MEMBERNOTFOUND` is read again later, and an
  object is assigned by value only when the property has no setter by reference.
- **`project update`** only sets the `LangVersion` of the project file with `--csharp-version`, and keeps the text and the
  encoding of the parts of the project file that it does not change. The `reference` commands keep them too.
- **Generated files:** only the files that CsCom generated are deleted from the `CsComOutputPath` of CsCom.Build and from the
  `Generated` folder of a directory that a project is created in. `project update` still replaces the `Generated` folder.
- **`ExcludeType`** refuses to remove a structure that is the element of an array passed by pointer, e.g. of a
  `SAFEARRAY(Record)*` retval.
- **One-dimensional arrays** of `byte`, `ushort`, `uint` and `ulong` are `SAFEARRAY`s of the unsigned types; they were created
  with the signed types. A `char[]` is no longer accepted.
- **An omitted optional `VARIANT`** that an interface implemented in .NET receives is `null`, not the integer of
  `DISP_E_PARAMNOTFOUND`.
- **`InvokePutRef`** of the late-bound wrappers assigns by value when the object has no setter by reference.
- **CsCom.Build:** the state file ends with an `end` line, so the code is generated once again after the update.
- **New warnings:** a type that is renamed because another type has its name, the copy of a type of another version of a
  library, and a structure of an interop assembly whose packing is less than the alignment of its fields.

### Added

- `ComDispatch.GetMemberKinds` and `DispatchMemberKinds`: how the type information of an object describes a member.
- `--package-source` accepts the URL of a feed.
- **CsCom.Build:** the warnings of the generator are reported by every build, also by those that do not run it; the files
  that the generator read are inputs of the fast up-to-date check of Visual Studio; and the compiler runs again after a
  generation that only deleted files.

### Fixed

- Aliases of pointers to, or arrays of, other aliases left references to aliases that were removed from the model.
- The reader marked the `[lcid]` and `[retval]` parameters and the value of a property setter as optional in place of the
  optional parameters before them.
- A pointer to an interface that a type library declares with the identifier of `IUnknown` or `IDispatch` was read as a
  pointer to an interface pointer.
- A type library that uses a library that is not registered failed with an unexpected error that named no type.
- An interop assembly with a parameter or a source interface of another kind of interface, e.g. an `IInspectable` one,
  failed to be read.
- Rename patterns that can match nothing, e.g. `(.*)`, added the new name twice.
- The copies of the types that another version of a library imported could get the name of a type of the library.
- A handler that left a by-reference string, object or array argument of an event `null` made the event fail before the
  arguments that follow were written back.
- The finalizer of a `ComSafeArray<T>` or a `ComBstr` could release it while one of its members was still using it.
- An input file that changed while the generator of CsCom.Build ran was recorded as up to date, and a state file whose
  writing was interrupted was accepted.
- Empty values of `--config`, `--reference-path` and `--output`, a regular expression that takes too long, and a generated
  file outside of its folder were reported as unexpected errors.

## 3.0.0-alpha1 - 2026-11-12

The changes since 2.0.0.

### Changed

- **Late-bound wrappers and dispinterface events:** a one-dimensional `SAFEARRAY` of an enum is an array of the enum, e.g.
  `Color[]`, like in the early-bound interfaces; it was a `System.Array`. This changes the types of the generated members.
  Enums referenced through aliases are recognized too.
- **Late-bound wrappers:** the one-dimensional typed `SAFEARRAY`s that a server returns are converted element by element,
  so a `SAFEARRAY` of `VARIANT`s that VB or a script returns for a `SAFEARRAY(BSTR)` no longer fails.
- **Arrays:** an array is only returned as it is when it has exactly the requested type; an `int[]` was returned as an
  array of an enum and a `string[]` as an `object[]`, because the runtime considers them compatible. The same applies to
  the `ref` and `out` variables of `dynamic` calls and to the arguments of `dynamic` event handlers.
- **`dynamic`:** the named arguments of indexes (`d[...]`, `d.Name[...]` and their assignments) and of invocations of the
  default member (`d(...)`) are resolved by their names; they were passed in the order of the call. Named arguments of
  the default member of an object without type information throw `NotSupportedException`.
- **Modules:** the unsigned `VARIANT` constants get the first of `int`, `uint`, `long` and `ulong` that can hold them, like
  C# integer literals; a value above `int.MaxValue` did not compile and one above `long.MaxValue` was dropped.
- **The merged models of library sets** (`LibrarySetGenerator.Transform`) no longer list the files of the libraries that
  their types were imported from.

### Added

- `DispatchObject.ToArray<T>`, which converts the elements of a returned array.
- `ComDispatch.GetMemberName`, which reads the name of a member from the type information of an object.
- `TypeLibrary.ImportedFiles`: the files of the libraries that a model imports types from.

### Fixed

- The overloads for default values passed `Missing.Value` for every `VARIANT` parameter: the code of by-reference `VARIANT`s
  did not compile, and a `null` required `VARIANT` was sent as an omitted argument.
- Reading a `decimal` default value of an interop assembly whose parts are 2^31 or more failed.
- The property accessors of dispinterfaces with by-reference parameters were invoked as methods, which objects reject,
  and their setters did not pass the value as the `DISPID_PROPERTYPUT` argument.
- **CsCom.Build** did not generate the code again when a library that types are imported from changed, e.g. the type
  library of Office for Excel or an assembly of the reference path.
- An event named `connection` or `__connection` made the generated event sink fail to compile.
- Index parameters named `value` next to `value_` got the same name in the generated indexers.
- `dynamic` event handlers and the default member with named arguments read signatures and type information on every
  call; they are read once.
