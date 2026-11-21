# Interface pointers

The interface pointers of parameters and return values are converted to shared managed objects: one per COM object, which
all the references to it share, like `ComInterfaceMarshaller<T>` of .NET. Its COM references are released when it is
garbage collected. The marshaller is `ComObjectMarshaller<T>` of CsCom.Runtime.

`SetMarshalling` selects unique objects for single members and parameters instead, with `UniqueComObjectMarshaller<T>`:
an object per interface pointer, like `UniqueComInterfaceMarshaller<T>` of .NET, which belongs to whoever receives it and
can be released at once. The pointer of a managed object that is exposed to COM, such as your implementation of a callback
interface, is converted to that object itself, as with shared objects, also with your own `ComWrappers`; releasing it does
nothing. As with shared objects, that is the pointer that the instance of the support code has created: the pointer that
another `ComWrappers` has created for a managed object is called through COM, like a COM object:

```xml
<SetMarshalling TypeName="IWorkbooks" MemberName="Open" Interfaces="unique" />
```

To tell the pointer of a managed object that the instance of the support code has created from one of another
`ComWrappers`, a unique conversion compares it with the interface that the instance has for the object, and the instance
creates that interface when it has none yet, e.g. when only another `ComWrappers` has exposed the object: the object
then keeps that unused interface for as long as it lives, since .NET cannot ask an instance for its interface without
creating it. An instance whose strategy cannot expose the object leaves the pointer to COM, as one of another
`ComWrappers`. This costs nothing for the pointers of COM objects and for shared objects, which `ComWrappers` unwraps
itself.

It applies to the parameters and return values of vtable and dual interfaces and of the functions of modules, whether
their type is an interface, a late-bound dispinterface wrapper, `IDispatch` or `IUnknown`. It does not apply to:

- a pointer that is passed in and out (`[in, out]`, a `ref` parameter), which is passed back to its caller;
- dispinterfaces and structures: the arguments and results of late-bound wrappers and of `dynamic` calls, and the objects
  in `VARIANT`s and `SAFEARRAY`s are always shared objects, and the pointers in the fields of structures are not converted.

A handler of a dual event interface gets a unique object whichever way the event is raised: through the vtable, or through
`IDispatch::Invoke`, whose `VARIANT` holds the object. A hand-written `IDispatchEventHandler` reads such an argument with
`EventArguments.GetUnique<T>` or `GetUniqueWrapper<T>`.

Nothing releases a unique object for you: the code that receives it, such as your implementation of a callback interface,
releases it, or leaves it to the garbage collector. The handlers of an event share its unique arguments: every handler
of the event of a generated sink, and every observer of an observable, gets the same object, so release it only where
no other handler uses it, or leave it to the garbage collector; `EventArguments.GetUnique<T>` of a hand-written
`IDispatchEventHandler` creates an object per call. A closed event sink does not release the unique arguments of the
events that it no longer passes on, because it cannot tell them from objects that a managed source in the same process
passes to it directly: they are left to the garbage collector.

**Unique objects** can be released without waiting for the garbage collector, with `ComMarshalling.Release` of the
support code or `ComObject.FinalRelease()` of .NET; a late-bound wrapper has its object in `Dispatch`, which
`ComMarshalling.Release` releases for a wrapper, also one of your own that implements `IDispatchWrapper<T>`, which is an
`IDispatchProvider`. It leaves a managed object that is exposed to COM (`[GeneratedComClass]`) alone, also when it
implements `IDispatchProvider`; `ComMarshalling.IsExposedToCom` tells whether an object is one. It releases the object
of an `UnknownWrapper`, and rejects a `DispatchWrapper`, as where an object is passed to COM. Release an object only
when no other thread uses it: a call that passes it to a COM object at the same time can use its interface pointer after
the release.

```csharp
IDictionary dictionary = Dictionary.Create(unique: true);  // or ComActivator.CreateInstance<IDictionary>(clsid, unique: true)
ComMarshalling.Release(dictionary);                        // the object must not be used afterwards
((ComObject)(object)dictionary).FinalRelease();            // the same
```

All the activation methods of `ComActivator` have an overload with the `unique` parameter: `CreateInstance<T>`,
`CreateDispatchInstance<T>`, `GetActiveInstance<T>`, `TryGetActiveInstance<T>` and `BindToMoniker<T>`; without it the
object is shared. Only the object itself is
unique: the objects that its members return are shared ones, unless `SetMarshalling` makes them unique too. A unique
object does not compare equal to another object of the same COM object, and releasing does nothing for shared objects.
The support code does not keep a unique object alive: when `SetMarshalling` makes `_NewEnum` unique, `foreach` over a
COM object of the generated interface releases the enumerator when it ends, also the object of a late-bound wrapper when
`_NewEnum` returns a dispinterface (one that your own implementation of the interface returns stays yours, and so does a
wrapper of yours that is exposed to COM, whose pointer the collection returns), and a disconnected event sink holds no
reference to its source: one whose disconnection failed with a transient error keeps it until another disconnection
succeeds or gives up. `IConnectionPointContainer.FindConnectionPoint` of the support code returns the connection point
of a COM object in a unique object too, which belongs to the caller: release it with `ComMarshalling.Release`. To
enumerate a unique enumerator yourself, use `new ComEnumerator(enumerator, ownsEnumerator: true)` or
`ComEnumerator.FromObject(value, ownsEnumerator: true)`: they release it when they are disposed, and `FromObject` also
when it does not implement `IEnumVARIANT`. `FromObject` takes what the value stands for, as `ComMarshalling.Release`
does: the COM object of a late-bound wrapper, which it releases, and an object of yours that is exposed to COM, also in
a late-bound wrapper, itself or, when it is no enumerator, the `Dispatch` that it provides, which it never releases.
Without `ownsEnumerator`, disposing them releases nothing.

**Your own `StrategyBasedComWrappers`** creates the managed objects when it is set at startup, before the first COM object
is created. Once the support code has converted an interface pointer, setting another instance throws an
`InvalidOperationException`; setting the same one again does not:

```csharp
ComMarshalling.ComWrappers = new MyComWrappers();          // class MyComWrappers : StrategyBasedComWrappers
```

All the generated code and the support code use it then, for shared and for unique objects: the interface pointers of the
generated interfaces, `ComActivator`, the `VARIANT` and `SAFEARRAY` marshallers, the late-bound wrappers, the `dynamic`
objects and the event sinks. Without it they use the instance of .NET. Hand-written COM interfaces whose interface pointers
are marshalled by `ComInterfaceMarshaller<T>` of .NET, which is the default of `[GeneratedComInterface]`, do not use it:
give their parameters `[MarshalUsing(typeof(ComObjectMarshaller<T>))]`, otherwise a COM object gets two managed objects,
or `UniqueComObjectMarshaller<T>` for unique objects. A late-bound wrapper is marshalled by the `DispatchWrapperMarshaller<T>`
of its `[NativeMarshalling]` attribute, and as a unique object by `UniqueDispatchWrapperMarshaller<T>`.
A managed object in a `VARIANT` is exposed to COM when its class is a `[GeneratedComClass]`, with or without your own
instance; pass an object that only your instance can expose in an `UnknownWrapper`, unless it implements
`IDispatchProvider`: such an object is a late-bound wrapper wherever it goes to COM, also in an `UnknownWrapper`. An
object that is exposed to COM is passed itself, also when it implements `IDispatchProvider`, in a `VARIANT`, a
`SAFEARRAY` and an `IUnknown` parameter alike, and as a parameter of its own type when it is a late-bound wrapper that
implements `IDispatch`, so that its pointer converts back to it; a late-bound or `dynamic` wrapper is passed as its COM
object there, and in a parameter of a COM interface too, also one that a wrapper of yours implements itself, e.g.
`IDispatch` by forwarding: its COM object is asked for the interface. The pointer converts back to the object for
an interface that it implements; for another declaration of the interface, e.g. of another interop assembly, it gets a
COM object of its own, which calls the object through COM, where `ComInterfaceMarshaller<T>` of .NET throws an
`InvalidCastException`: a new one for each conversion, also of a shared parameter, which is not shared, so that
`ComMarshalling.Release` releases it. The object comes back itself from a `VARIANT` or a `SAFEARRAY`, and where a
late-bound wrapper, `EventArguments.Get<T>` or an array converts it to an interface that its class does not implement,
it is asked through its COM interface the same way, e.g. for another declaration of the interface; when that does not
answer, the COM object of its `Dispatch` is asked for the interface, as when the `VARIANT` held that object. Only
where an interface is required that it does not implement, `IDispatch` in a `SAFEARRAY` of `IDispatch`, in a
dispinterface wrapper and for `dynamic`, and `IEnumVARIANT` in `ComEnumerator.FromObject`, it stands for the COM object
of its `Dispatch`, like the source of events. A late-bound or `dynamic` wrapper of such an object, e.g. the one that a
dispinterface wrapper makes of it when a call returns it, stands for it with this rule, also as the source of events,
in `ComEnumerator.FromObject`, where a late-bound wrapper converts it to an interface, and in a parameter of a COM
interface that the wrapper implements. A COM object that fails `QueryInterface` with another error than
`E_NOINTERFACE`, e.g. the proxy of a server that has ended, throws the exception of its `HRESULT`, also in
`ComEnumerator.FromObject`, rather than an `InvalidCastException`. An `UnknownWrapper` stands
for its object wherever an object goes to COM: in an `IUnknown` parameter, a `SAFEARRAY`, as the source of events, for
`ComDynamic` and in `ComEnumerator.FromObject` as in a `VARIANT`, where it is passed as `VT_UNKNOWN`, and
`new UnknownWrapper(null)` passes a null object, where `null` passes an empty `VARIANT`. A `DispatchWrapper` is
rejected everywhere with an `ArgumentException`, as by
`ComVariantMarshaller` of .NET in a `VARIANT`: the interface of such a wrapper itself would only have `IUnknown`. The
interfaces must be `[GeneratedComInterface]`s also when your instance has a strategy of its own for the details of
interfaces: the marshallers and `ComActivator` take their identifiers from the generated attributes, and fail with a
`NotSupportedException` for another interface, `ComActivator` also for a class other than `object` and `ComObject`,
e.g. a late-bound wrapper, which `CreateDispatchInstance` creates, and before anything else: before it looks up a
ProgID, creates or gets the object. Such a strategy must give the details of `IDispatch` of the support code too, which
the late-bound wrappers, the `dynamic` objects and the events call: a `VARIANT` trusts the strategy, so that a COM
object without `IDispatch` is asked for it only once, and passes an object whose strategy has none as `VT_UNKNOWN`;
only a `SAFEARRAY` of `IDispatch` asks the object itself.
With the `public` and `internal` support code, every generated project has its own `ComMarshalling`.
