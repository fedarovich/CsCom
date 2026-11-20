# Known issues

The issues of CsCom that are known and not fixed, e.g. from code reviews, with the reasons why, what a fix would take
and where the behavior is documented, and the behaviors that were reported as issues but are intended, with the reasons
why: check this list before reporting or fixing one. The code is named by its symbols.

## Support code

### The layout check of the support code misses some conflicts

`SupportCodeProvider` moves the using directives of every support code file into the namespace of the file, which it
renames, and the single-file mode merges those of all files into one namespace (`CSharpEmitter.BuildSingleFile`),
without the directives of the same text. `SupportCodeLayout` rejects what the rewriter cannot adapt, also a name of the
namespace `CsCom.Runtime`, which would name nothing in a generated project, or the types of the CsCom.Runtime package in
one that references it. It does not catch these, which only show in a generated project:

- An alias named like a type of the support code: in CsCom.Runtime the type hides the alias, in the namespace they
  conflict (CS0576).
- In the single-file mode: the same alias for two types in two files, or spelled differently (CS1537); a type that two
  namespaces declare which two files import (CS0104); the same using directive spelled differently (CS0105, a warning).
- In the single-file mode, a member of a `using static` that one file imports can be a better match for a call in
  another file than the one that this file imports itself, and is called instead, without an error.

`SupportCodeLayout` rejects every preprocessor directive, which the single-file mode could not keep in its file and
whose excluded code the rewriter would not adapt, nor `SupportCodeQualifier` qualify. `SupportCodeQualifier` calls the
extension methods through their classes, which a static class in the namespace, also one of another file, cannot
compete with.

**Why it is not fixed:** the aliases need the types and using directives of all files, and the static imports the
binding of the calls. All but the last fail the build of a generated project, which the integration tests build in
every support code mode, also in the single-file mode, with warnings as errors. The support code has no alias and no
static import, and its files import only namespaces of .NET.

**A fix** would check the aliases of all files in the `QualifySupportCode` task, against each other and against the
names of the types; `SupportCodeQualifier` would call the members of static imports through their classes too.

### The support code calls the extension methods of .NET 10 also for .NET 11 (intended)

The `QualifySupportCode` task of CsCom.SupportCode binds the support code as C# 14 against the reference assemblies of
.NET 10 when CsCom.Core is built, and calls each extension method through the class that overload resolution finds
there. A generated project for .NET 11 compiles the same calls, while the net11.0 build of the CsCom.Runtime package
binds the calls of its sources against .NET 11, where an extension method that .NET 11 adds to another class can be a
better match. The support code of a generated project then calls another, equivalent overload than the package.

**Why it is intended:** one text of the support code serves every target framework of a generated project, which can
target several, and the support code has no preprocessor directives. .NET 10 is the lowest .NET of the generated
projects, and C# 14 their lowest version: later ones only add methods and conversions, so the calls compile on all of
them. The names of types are the same on every .NET.

**Documented** at `SupportCodeQualifier`.

## Marshalling

### The `ArgumentException` of a `DispatchWrapper` names an internal parameter

`EventConnection.Advise` and `AdviseDispatch` reject a source that is, or wraps, a `DispatchWrapper` with an
`ArgumentException` whose `ParamName` is `value`, the parameter of `ComMarshalling.QueryInterface`, which
`FindConnectionPoint` calls, rather than `source`. In a `SAFEARRAY`, the exception names `value` or `managed`, the
parameters of the internal methods that convert its elements.

**Why it is not fixed:** only the `ParamName` is wrong; the message says what is wrong and what to pass instead. The
exceptions of `Advise` and `AdviseDispatch`, also the `ObjectDisposedException` of a source that has been released, are
documented on the methods.

**A fix** would pass the name of the caller's parameter to `QueryInterface` and `ComObjects.GetDispatch`, as
`ComEnumerator.FromObject` passes `enumerator` to `ComMarshalling.UnwrapUnknownWrapper`.

### A unique conversion creates an unused interface for an object that another `ComWrappers` exposes

To tell the pointer of a managed object that the instance of the support code has created from one of another
`ComWrappers`, `ComMarshalling.TryUnwrap` compares it with the interface that the instance has for the object, and the
instance creates that interface when it has none yet. An object that only another `ComWrappers` has exposed then keeps
an unused interface of the support code's instance for as long as it lives.

**Why it is not fixed:** .NET cannot ask an instance for its interface of an object without creating it. A
`ConditionalWeakTable` of the interfaces that `ToUnmanaged` creates would avoid it, but miss the interfaces that the
instance creates elsewhere, e.g. in your own calls of `ComMarshalling.ComWrappers` or in the marshallers of .NET, whose
pointers would then be called through COM instead of converting back to the object. The pointers of COM objects and
shared conversions cost nothing, since `ComWrappers` unwraps them itself.

**Documented** in the README, "Interface pointers", and at `TryUnwrap`.

### A shared conversion of a managed object that lacks the interface creates an object for every conversion

The pointer of a managed object that the instance exposes, but that does not implement the interface, e.g. because its
class implements the declaration of the interface of another interop assembly, gets a COM object of its own in
`ComMarshalling.ToManaged`, which calls the object through COM: a new one for every conversion, also of a shared
parameter. It is not shared, so `ComMarshalling.Release` releases it, two conversions of the same pointer are different
objects, and the objects that nobody releases keep a reference to the managed object until they are garbage collected.
The same holds for such an object of yours (`[GeneratedComClass]`) that comes back from a `VARIANT` or a `SAFEARRAY`
itself, where `ComConvert.To` converts it for a late-bound wrapper, `EventArguments.Get<T>` or an array
(`ComMarshalling.TryConvertExposedObject`).

**Why it is not fixed:** a shared object cannot be created for it. The instance of .NET has no public method for it, and
another instance would keep the object for the identity of the pointer, which `ComWrappers` looks up before it unwraps,
so it would be returned instead of the managed object also for the interfaces that the object implements.

**Documented** in the README, "Interface pointers" ("a new one for each conversion, also of a shared parameter"), and in
`ComMarshalling.ToManaged`.

### A pointer and a `VARIANT` convert an object of yours that lacks the interface differently (intended)

An object of yours that is exposed to COM and implements `IDispatchProvider`, e.g. a decorator of a COM object, converts
to an interface that it does not answer through its COM interface in two ways:

- From a `VARIANT` or a `SAFEARRAY`, e.g. with `EventArguments.Get<T>`, a late-bound wrapper or an array, it stands for
  the COM object of its `Dispatch`, which is asked for the interface.
- From an interface pointer, e.g. the parameter of a dual event that the source raises through the vtable, or
  `EventArguments.GetUnique<T>`, it gets a COM object of its own, whose cast to the interface fails.

**Why it is intended:** a pointer of an interface that does not answer that interface only comes from a source that
breaks the rules of COM: a native source gets the pointer from `QueryInterface`, which the object fails, and a managed
source cannot pass an object that does not implement the interface in its type. A `VARIANT` holds an object of any
type, and its conversion to an interface is where the rule of `IDispatchProvider` applies. Another declaration of the
interface, which the COM interface of the object answers, gets a COM object of its own on both ways.

**Documented** at `IDispatchProvider` and in the README, "Interface pointers", which name the conversions where such an
object stands for its `Dispatch`.

### A `dynamic` object converts to an interface only through the managed type of its object

The conversion of a `dynamic` object to an interface, e.g. `(IFoo)d`, returns the `IDispatch` that it late binds when
that implements the interface, or when it is a COM object that answers it (`IDynamicInterfaceCastable`). An object of
yours that is exposed to COM is late bound itself when it implements `IDispatch`: its conversion fails for an interface
that its class does not implement, also when its COM interface answers it, e.g. another declaration of the interface, or
when it is a decorator whose `Dispatch` implements it. `ComConvert.To` converts the same `dynamic` object, e.g. as the
value of a late-bound call, with the rule of `IDispatchProvider`.

**Why it is not fixed:** CsCom.Runtime.Dynamic only uses the public API of CsCom.Runtime, and asking the COM interface
for an interface that is only known as a `Type` at run time needs its identifier by reflection and a COM object of its
own. Convert the object itself, or the COM object of its `Dispatch`, instead of the `dynamic` object.

**Documented** here.

### The exposure of an `IDispatchProvider` is looked up wherever it goes to COM (intended)

`ComMarshalling.IsLateBoundWrapper` looks up whether the class of an `IDispatchProvider` is exposed to COM wherever it
goes to COM, e.g. for every late-bound wrapper that `DispatchWrapperMarshaller<T>` or a `VARIANT` passes, and
`ToUnmanaged` looks up an exposed one again after `VariantMarshaller` or `ComObjects.GetDispatch`.

**Why it is intended:** the lookup is the rule: a wrapper of the application that is exposed to COM passes itself, and
any class can be one, also one that derives from `DispatchObject`. `IsExposedToCom` keeps the result per class in a
`ConditionalWeakTable`, whose read takes no lock and costs little next to the call that the object goes to; the second
lookup keeps `ToUnmanaged` complete for its other callers.

**Documented** at `ToUnmanaged`.

### The exception of a value that stands for no object unwraps it again (intended)

`ComDynamic.NoComObject` and `EventConnection.NoObject` unwrap the `UnknownWrapper`s of a value again for the message
of the `ArgumentNullException`, after the caller has found that the value stands for no object: a source or a sink of
`EventConnection.Advise`.

**Why it is intended:** it happens only when the exception is thrown, and finds what the caller found, since an
`UnknownWrapper` does not change and a `DispatchWrapper` has thrown already. Passing the unwrapped value on would split
`ComDynamic.GetWrapper`, which `ToDynamic` needs to return `null`, and `ComMarshalling.QueryInterface` would need
another result, only for the message.

**Documented** at both.

### `VariantMarshaller` looks up the wrappers of .NET as objects (intended)

`VariantMarshaller.CreateObjectVariant` passes the values and `DBNull` at once, but asks `ComWrappers` whether
`BStrWrapper`, `ErrorWrapper`, `VariantWrapper` and `CurrencyWrapper` are COM objects, and looks up whether their
classes are exposed to COM, before `ComVariantMarshaller` converts them.

**Why it is intended:** they are rare types of the built-in COM interop, which `ComBstr` and `ComCurrency` replace, and
the two lookups cost little next to the call that the `VARIANT` goes to. Testing for them first would cost every COM
object of another `ComWrappers` and every exposed object four more type tests, and `CurrencyWrapper` is obsolete: a
test for it needs a `#pragma`, which `SupportCodeLayout` rejects.

**Documented** at `CreateObjectVariant`.

### The rule of what a value stands for is applied in several places

What a value stands for where it goes to COM (the rule of `IDispatchProvider`, late-bound wrappers, objects exposed to
COM, `UnknownWrapper` and `DispatchWrapper`) is applied by `ComMarshalling.ToUnmanaged`, `QueryInterface` and `Release`,
`ComConvert.ToWrapper` and `To`, `ComObjects.GetDispatch`, `ComEnumerator.FromObject` and
`VariantMarshaller.ConvertToUnmanaged`, each with its own order of the cases and fast path for COM objects. A change of
the rule must follow all of them, which earlier changes missed at first, e.g. in `FromObject`, `GetDispatch` and
`Release`, and in `QueryInterface`, `To` and `FromObject` for a late-bound wrapper whose `Dispatch` is an object of
yours that is exposed to COM, which stands for that object with its rule. `To` did not ask the COM interface of such an
object for another declaration of the interface, as its pointer is asked, and `ToUnmanaged` did not fall back to the
`IDispatch` that such an object in the `Dispatch` of a wrapper provides.

**Why it is not fixed:** the places share the helpers `IsLateBoundWrapper`, `TryGetProvidedDispatch`,
`TryConvertExposedObject` and `UnwrapUnknownWrapper` of `ComMarshalling`, but each needs another result, a pointer of an
interface, a late-bound wrapper, an `IDispatch` or the type of a `VARIANT`, and tests the cases in the order that its
fast path needs, e.g. a COM object first, which is no `IDispatchProvider`. A single resolver of what a value stands for
would still leave each place to apply its result.

**Documented** in `CLAUDE.md`, "Things that span several files", which lists the places; their tests are in
`RuntimeMarshallerTests`, `UniqueInstanceTests` and `DynamicEventTests`.

### An `IDispatchProvider` that only a custom `ComWrappers` exposes is a late-bound wrapper (intended)

`ComMarshalling.IsLateBoundWrapper` asks the default strategy of .NET whether the class of an `IDispatchProvider` is
exposed to COM (`[GeneratedComClass]`), not the instance of `ComMarshalling.ComWrappers`. An object whose class only the
strategy of a custom instance exposes is passed as the COM object of its `Dispatch` wherever it goes to COM, also in an
`UnknownWrapper`, which passes other such objects themselves, and in a parameter of a COM interface that it implements.

**Why it is intended:** the same values are passed with every instance, and the instance can still be set after an
object has been converted. Make the class a `[GeneratedComClass]` to pass the object itself.

**Documented** at `ComMarshalling.IsExposedToCom` and in the README, "Interface pointers".

### A late-bound wrapper is unwrapped once (intended)

The support code converts the `Dispatch` of a late-bound wrapper itself, without asking whether it is a late-bound
wrapper too. A wrapper whose `Dispatch` is another wrapper of yours that is not exposed to COM, e.g. one that implements
`IDispatch` by forwarding, is passed as that wrapper, whose COM interface only has `IUnknown`: it is rejected where an
`IDispatch` is required, e.g. in a `VARIANT`, although the inner wrapper alone is passed as its COM object there. The
support code never creates such a wrapper: `ComConvert.ToWrapper`, `ComDynamic` and the marshallers unwrap first, so
only a wrapper that is created by hand for another one is affected.

**Why it is intended:** the `Dispatch` of a wrapper is its COM object by the contract of `IDispatchProvider`. Unwrapping
it again would need a guard against wrappers that provide themselves or each other, in every place that applies the
rule (see above).

**Documented** at `ComMarshalling.ToUnmanaged`; `UniqueInstanceTests.AWrapperIsUnwrappedOnce` tests it.

### CsCom.Runtime.Dynamic copies rules of CsCom.Runtime

`ComDynamic.Unwrap` copies `ComMarshalling.UnwrapUnknownWrapper` with its message, `ComDynamic.TryGetDispatch` the rule
of `ComConvert.ToWrapper` with `IsLateBoundWrapper` and `TryGetProvidedDispatch`, and
`DynamicDispatchObject.IsComObject` the order of the cases of `VariantMarshaller`. `ComDynamic.ToDynamic` and
`DynamicDispatchObject.Wrap`, which makes the results of calls dynamic, share `TryGetDispatch`; `Wrap` had kept an
object of yours that is exposed to COM and implements `IDispatchProvider` as it was at first, once a `VARIANT` returned
such an object itself.

**Why it is not fixed:** CsCom.Runtime.Dynamic only uses the public API of CsCom.Runtime, and making the internal
helpers public would add API for the support of `dynamic` alone. `TheDynamicSupportFollowsTheRulesOfTheRuntime` runs the
same values through both and compares the results and the types of the exceptions, also of the values that a `VARIANT`
returns for them, through `Wrap`.

**Documented** at the copies.

## Events

### A closed event sink leaves the unique arguments of the events that it ignores to the garbage collector

Once its connection is disconnected, the `ComSink` of a generated sink class (`EventEmitter.EmitComSink`) accepts the
events without passing them on, also when the source has not been told yet, e.g. because the server was busy. The unique
objects that the marshallers created for the arguments of such an event are not released: they keep their COM objects
until they are garbage collected, e.g. an Office server after `Quit()`.

**Why it is not fixed:** a managed source in the same process calls the sink directly, with objects that it owns, also
unique COM objects of its own, which the sink cannot tell from those that the marshallers created for it.

**A fix** would need the marshallers of the arguments to know that the sink is closed, so that they do not create the
objects at all.

**Documented** in the README, "Interface pointers", and at `EventEmitter.EmitComSink`.

### The handlers of an event share its unique arguments

An argument of an event that `SetMarshalling` makes a unique object is one object per call of the event, which every
handler of the event of a generated sink gets, and every observer of an observable. A handler that releases it, as the
README says the receiver of a unique object may, releases it for the handlers after it, which then get an
`ObjectDisposedException` from its calls.

**Why it is not fixed:** the marshaller of the event interface creates the object when the COM object calls the sink,
before the sink passes the call on: the sink passes the arguments of one call, like any .NET event. A hand-written
`IDispatchEventHandler` can create an object per handler with `EventArguments.GetUnique<T>`, which creates one per call.

**A fix** would convert the pointer for each delegate of the invocation list of the event and each observer: the sink
would take the pointers of the interface without the marshaller, and convert and release them itself.

**Documented** in the README, "Interface pointers".

### `EventConnection.Advise(source, iid, sink)` creates a gate that the sink never enters

A sink that is passed to `Advise` itself gets events until the source is told, without a gate. Its connection still
creates a gate and a closure, as `Advise` with a factory does, which only make `DisposeWithCompletion` and
`DisposeAsync` complete at once.

**Why it is not fixed:** the allocations are small next to the COM calls of a connection, and every connection is
disconnected the same way. A gate that all such connections shared would run the completions of one connection after
those of another, on its thread, since a gate runs its completions one at a time, and without a gate every path of the
disconnection would need a test for it.

**Documented** in the README, "Events", in the documentation of `DisposeWithCompletion`, and at `Advise`.

### `EventConnection.Disconnect` creates a task for every call of `Unadvise` (intended)

The call of `Disconnect` that calls `IConnectionPoint::Unadvise` publishes an `Attempt`, a `TaskCompletionSource`,
which only a concurrent `DisposeAsync` waits for: also when nobody does, for the first disconnection and for every one
that retries it after a transient error. A call after the connection is over returns its result without one.

**Why it is intended:** the task is small next to the COM call that it stands for, like the gate of a connection. A
task that the waiters created only when they need one would have to be installed into the state of the connection by
`DisposeAsync`, `WaitForAttempt` and `FailWhenIdle` and completed by the call of `Unadvise`, whatever it finds there:
the races between the callers of `Disconnect`, which the state resolves now, would get another participant.

**Documented** at `Disconnect`.

### `EventSubject.CompleteAfterAsync` copies its subjects (intended)

`CompleteAfterAsync` copies its `params` array of subjects, which the `DisposeAsync` of a generated observable sink
creates for each call, so that the call allocates two arrays.

**Why it is intended:** it happens once per sink, next to the COM call of the disconnection and its tasks. The copy
keeps the caller from replacing a subject with `null` while the disconnection runs. An overload that takes the array
over would have to be public, since the generated code uses the package, and safe only for the generated code. A
`params ReadOnlySpan<EventSubject>`, like that of `Complete`, would save the array of the call, but count a `null` array
as no subjects instead of rejecting it, which `CompleteAfterAsyncChecksTheSubjectsAtOnce` pins.

**Documented** at `CompleteAfterAsync`.

### `DynamicEventSink.TakeRetired` walks the connections three times (intended)

When a connection is retired, `TakeRetired` tests whether one is, removes those that are disconnected and copies those
that are still connected: three passes over the list and two allocations, under the lock of the sink.

**Why it is intended:** usually no connection is retired, which costs one pass and no allocation. A retired connection
exists only from the change that ends it until it is disconnected, and the list holds a few connections. A single loop
that compacts the list would save two passes over them and an array, in place of three steps that say what happens, in
the class whose locking took the most changes to get right.

**Documented** at `TakeRetired`.

### `EventSubject.CompleteAfterAsync` leaves the disconnection alone when a subject is null (intended)

`EventSubject.CompleteAfterAsync(connection.DisposeAsync(), a, null)` throws an `ArgumentNullException` without
awaiting the disconnection, which the caller has started, and without completing `a`, whose events have stopped. An
exception of the disconnection, e.g. the `COMException` of a sink that stays connected, is then not observed.

**Why it is intended:** a null subject is an error of the caller, which the method reports at once without doing
anything else, like `Task.WhenAll` with a null task, which neither awaits nor observes the other tasks. Awaiting the
disconnection would swallow its exception, which reaches `TaskScheduler.UnobservedTaskException` otherwise, and
completing the other subjects would leave the caller unsure what a call that failed has done. The generated sinks pass
their own subjects, which are never null.

**Documented** in the documentation of `CompleteAfterAsync`, and at it.

### A failed `dynamic` addition does not undo a removal that took its handler (intended)

`DynamicEventSink` keeps an entry for each addition of a handler, and a failed addition removes only its own entry. A
`-=` removes the last equal entry, which can be that of an addition that is still connecting and then fails: that
addition then removes nothing. E.g. when `h` is added on two threads and removed on a third while both additions are
connecting, and the later addition fails, the removal took its entry, and `h` stays subscribed by the earlier addition,
which succeeds.

**Why it is intended:** the changes take effect one after another, each at some moment between its call and its return,
as for a .NET event. The removal ran before the earlier addition returned, so it can come before it: it removed nothing,
the earlier addition added `h`, and the failed one added nothing. Removing another equal entry instead, as a review
suggested, could take the entry of an addition that started after the removal, e.g. `-=` and then `+=` on one thread,
whose handler must stay: `AFailedConnectionRemovesOnlyItsOwnHandler` fails with it. Taking only an entry that was added
before the removal would need the order of the changes, and give the other outcome of the same race, which is no more
correct.

**Documented** in the README, "Late binding with `dynamic`", in the remarks of `DynamicEventSink` and at its `Add`.

## Code generation

### The generated locals and nested types can clash with names of the type library

The generated code names its locals with a leading `__`: `__args` in the late-bound wrappers and the observable sinks,
`__a0` and `__result` in the dispatch handlers, and `__` and the name of a parameter for the temporaries of the default
values in `SignatureBuilder`, e.g. `__label` for a `ComBstr`. A parameter of the type library with the same name clashes
with such a local, and the generated project does not compile. So does an event method named like a nested type of the
sink, `ComSink` or `DispatchHandler`, or like its handler interface. The fields of the sinks give way to the names of
the type library: the fields of the observables (`Unique` in `EventEmitter.AssignNames`) and of the `ComSink`.

**Why it is not fixed:** no type library is known that names parameters or event methods like that. MIDL names the
parameters that have no names `__MIDL__…`, which never match, and renaming every name with a leading `__` in
`NameSanitizer` would rename those.

**A fix** would make every local unique against the parameters of its method, and the nested types against the methods
of the event interface, as `EventEmitter` does for the fields.


### The generator maps the signature of `_NewEnum` twice

`InterfaceEmitter` maps the signature of the `_NewEnum` member of a collection interface again with `MapComSignature` to
know whether its enumerator is unique, after `BuildComMethod` has mapped it for the interface.

**Why it is not fixed:** it costs one more mapping per collection interface when the code is generated. Reading the
marshalling of the return value directly would repeat the decision of the mapper, and reusing the mapping of
`BuildComMethod` would pass it through the emitter for no measurable gain.
