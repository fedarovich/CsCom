# Changelog

The notable changes of CsCom, CsCom.Build, CsCom.Runtime and CsCom.Runtime.Dynamic, which are released together with the
same version. The versions up to 2.0.0 are not described here.

## Unreleased

### Changed

- **Property setters with optional indexes:** a `null` optional `VARIANT` index of a setter is omitted, like that of the
  getter; it was passed as an empty `VARIANT`. This applies to the `set_` and `putref_` methods of the interfaces and the
  late-bound wrappers and to the indexers that call them. The index parameters of the setter methods stay required in C#,
  since the value follows them.

### Fixed

- A method named like its interface made the generated interface fail to compile; it gets a trailing underscore, like the
  members of the late-bound wrappers already did.
- The types that are generated next to an interface (the extensions class, the event handler interface, the event sink and
  the delegates or the classes of the event arguments) could have the name of a type of the library, e.g. `IShapeExtensions`.
  Such a generated type gets a trailing underscore.
- An `object` field without `MarshalAs` of a structure of an interop assembly was read as a `VARIANT`, which gave the
  structure the wrong layout. The runtime marshals it as an `IUnknown` pointer, and so does the generated structure; a field
  with `UnmanagedType.Struct`, which TlbImp writes, stays a `VARIANT`.

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
