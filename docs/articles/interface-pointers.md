# Interface pointers

The interface pointers of parameters and return values are converted to shared managed objects: one per COM object, which
all the references to it share, like `ComInterfaceMarshaller<T>` of .NET. Its COM references are released when it is
garbage collected. The marshaller is `ComObjectMarshaller<T>` of CsCom.Runtime.

## Unique objects

`SetMarshalling` selects unique objects for single members and parameters instead, with `UniqueComObjectMarshaller<T>`:
an object per interface pointer, like `UniqueComInterfaceMarshaller<T>` of .NET, which belongs to whoever receives it and
can be released at once:

```xml
<SetMarshalling TypeName="IWorkbooks" MemberName="Open" Interfaces="unique" />
```

It applies to the parameters and return values of vtable and dual interfaces and of the functions of modules, whether
their type is an interface, a late-bound dispinterface wrapper, `IDispatch` or `IUnknown`. It does not apply to:

- a pointer that is passed in and out (`[in, out]`, a `ref` parameter), which is passed back to its caller;
- dispinterfaces and structures: the arguments and results of late-bound wrappers and of `dynamic` calls, and the objects
  in `VARIANT`s and `SAFEARRAY`s are always shared objects, and the pointers in the fields of structures are not converted.

### Creating unique objects

All the activation methods of `ComActivator` have an overload with the `unique` parameter; without it the object is
shared:

- `CreateInstance<T>` and `CreateDispatchInstance<T>`;
- `GetActiveInstance<T>` and `TryGetActiveInstance<T>`;
- `BindToMoniker<T>`.

`Create(unique: true)` of a coclass creates a unique object too, as in the example below.

Only the object itself is unique: the objects that its members return are shared ones, unless `SetMarshalling` makes
them unique too. A unique object does not compare equal to another object of the same COM object.

### Releasing unique objects

Nothing releases a unique object for you: the code that receives it, such as your implementation of a callback interface,
releases it, or leaves it to the garbage collector. It can be released without waiting for the garbage collector, with
`ComMarshalling.Release` of the support code or `ComObject.FinalRelease()` of .NET:

```csharp
IDictionary dictionary = Dictionary.Create(unique: true);  // or ComActivator.CreateInstance<IDictionary>(clsid, unique: true)
ComMarshalling.Release(dictionary);                        // the object must not be used afterwards
((ComObject)(object)dictionary).FinalRelease();            // the same
```

Releasing does nothing for shared objects. Besides a unique object, `ComMarshalling.Release` takes:

- a late-bound wrapper, which has its object in `Dispatch`: it releases that object, also for a wrapper of your own that
  implements `IDispatchWrapper<T>`, which is an `IDispatchProvider`;
- a managed object that is exposed to COM (`[GeneratedComClass]`), which it leaves alone, also when it implements
  `IDispatchProvider`; `ComMarshalling.IsExposedToCom` tells whether an object is one;
- an `UnknownWrapper`, whose object it releases, and a `DispatchWrapper`, which it rejects, as where an object is passed
  to COM.

Release an object only when no other thread uses it: a call that passes it to a COM object at the same time can use its
interface pointer after the release.

### Unique objects of the support code

The support code does not keep a unique object alive:

- When `SetMarshalling` makes `_NewEnum` unique, `foreach` over a COM object of the generated interface releases the
  enumerator when it ends, also the object of a late-bound wrapper when `_NewEnum` returns a dispinterface. An enumerator
  that your own implementation of the interface returns stays yours, and so does a wrapper of yours that is exposed to
  COM, whose pointer the collection returns.
- A disconnected event sink holds no reference to its source. One whose disconnection failed with a transient error keeps
  it until another disconnection succeeds or gives up.

`IConnectionPointContainer.FindConnectionPoint` of the support code returns the connection point of a COM object in a
unique object too, which belongs to the caller: release it with `ComMarshalling.Release`.

### Enumerating a unique enumerator

To enumerate a unique enumerator yourself, use `new ComEnumerator(enumerator, ownsEnumerator: true)` or
`ComEnumerator.FromObject(value, ownsEnumerator: true)`. They release it when they are disposed, and `FromObject` also
when it does not implement `IEnumVARIANT`.

`FromObject` takes what the value stands for, as `ComMarshalling.Release` does:

- for a late-bound wrapper, its COM object, which it releases;
- for an object of yours that is exposed to COM, also in a late-bound wrapper, the object itself or, when it is no
  enumerator, the `Dispatch` that it provides; it never releases either.

Without `ownsEnumerator`, disposing them releases nothing.

### Unique event arguments

A handler of a dual event interface gets a unique object whichever way the event is raised: through the vtable, or through
`IDispatch::Invoke`, whose `VARIANT` holds the object. A hand-written `IDispatchEventHandler` reads such an argument with
`EventArguments.GetUnique<T>` or `GetUniqueWrapper<T>`.

The handlers of an event share its unique arguments: every handler of the event of a generated sink, and every observer of
an observable, gets the same object. So release it only where no other handler uses it, or leave it to the garbage
collector. `EventArguments.GetUnique<T>` of a hand-written `IDispatchEventHandler` creates an object per call.

A closed event sink does not release the unique arguments of the events that it no longer passes on, because it cannot
tell them from objects that a managed source in the same process passes to it directly: they are left to the garbage
collector.

### Pointers of managed objects

The pointer of a managed object that is exposed to COM, such as your implementation of a callback interface, is converted
to that object itself, as with shared objects, also with your own `ComWrappers`; releasing it does nothing. As with shared
objects, that is the pointer that the instance of the support code has created. The pointer that another `ComWrappers`
has created for a managed object is called through COM, like a COM object.

To tell the pointer that the instance of the support code has created from one of another `ComWrappers`, a unique
conversion compares it with the interface that the instance has for the object. The instance creates that interface when
it has none yet, e.g. when only another `ComWrappers` has exposed the object: the object then keeps that unused interface
for as long as it lives, since .NET cannot ask an instance for its interface without creating it. An instance whose
strategy cannot expose the object leaves the pointer to COM, as one of another `ComWrappers`.

This costs nothing for the pointers of COM objects and for shared objects, which `ComWrappers` unwraps itself.

## Your own StrategyBasedComWrappers

Your own `StrategyBasedComWrappers` creates the managed objects when it is set at startup, before the first COM object
is created. Once the support code has converted an interface pointer, setting another instance throws an
`InvalidOperationException`; setting the same one again does not:

```csharp
ComMarshalling.ComWrappers = new MyComWrappers();          // class MyComWrappers : StrategyBasedComWrappers
```

All the generated code and the support code use it then, for shared and for unique objects:

- the interface pointers of the generated interfaces;
- `ComActivator`;
- the `VARIANT` and `SAFEARRAY` marshallers;
- the late-bound wrappers and the `dynamic` objects;
- the event sinks.

Without it they use the instance of .NET. With the `public` and `internal` support code, every generated project has its
own `ComMarshalling`.

### Hand-written interfaces

Hand-written COM interfaces whose interface pointers are marshalled by `ComInterfaceMarshaller<T>` of .NET, which is the
default of `[GeneratedComInterface]`, do not use it. Give their parameters
`[MarshalUsing(typeof(ComObjectMarshaller<T>))]`, otherwise a COM object gets two managed objects, or
`UniqueComObjectMarshaller<T>` for unique objects. The marshallers of interface pointers in the support code are:

| Type | Shared object | Unique object |
| --- | --- | --- |
| COM interface | `ComObjectMarshaller<T>` | `UniqueComObjectMarshaller<T>` |
| Late-bound wrapper | `DispatchWrapperMarshaller<T>`, of its `[NativeMarshalling]` attribute | `UniqueDispatchWrapperMarshaller<T>` |

### Strategies for the details of interfaces

The interfaces must be `[GeneratedComInterface]`s also when your instance has a strategy of its own for the details of
interfaces. The marshallers and `ComActivator` take their identifiers from the generated attributes, and fail with a
`NotSupportedException` for another interface. `ComActivator` also fails so for a class other than `object` and
`ComObject`, e.g. a late-bound wrapper, which `CreateDispatchInstance` creates, and before anything else: before it looks
up a ProgID, creates or gets the object.

Such a strategy must give the details of `IDispatch` of the support code too, which the late-bound wrappers, the `dynamic`
objects and the events call. A `VARIANT` trusts the strategy, so that a COM object without `IDispatch` is asked for it only
once, and passes an object whose strategy has none as `VT_UNKNOWN`; only a `SAFEARRAY` of `IDispatch` asks the object
itself.

## Objects passed to COM

What goes to COM, in a parameter, a `VARIANT` or a `SAFEARRAY`, as the source of events or through `dynamic`, depends on
the object:

- **A managed object that is exposed to COM** is passed itself, also when it implements `IDispatchProvider`: in a
  `VARIANT`, a `SAFEARRAY` and an `IUnknown` parameter alike, and as a parameter of its own type when it is a late-bound
  wrapper that implements `IDispatch`, so that its pointer converts back to it. A managed object in a `VARIANT` is exposed
  to COM when its class is a `[GeneratedComClass]`, with or without your own instance.

  Only where an interface is required that it does not implement, it stands for the COM object of its `Dispatch`, like
  the source of events: `IDispatch` in a `SAFEARRAY` of `IDispatch`, in a dispinterface wrapper and for `dynamic`, and
  `IEnumVARIANT` in `ComEnumerator.FromObject`.
- **A late-bound or `dynamic` wrapper** is passed as its COM object in a `VARIANT`, a `SAFEARRAY`, an `IUnknown`
  parameter and a parameter of its own type, and in a parameter of a COM interface too, also one that a wrapper of yours implements itself, e.g. `IDispatch` by forwarding: its COM object is
  asked for the interface.
- **A late-bound or `dynamic` wrapper of an object that is exposed to COM**, e.g. the one that a dispinterface wrapper
  makes of it when a call returns it, stands for that object with the rule of the first item: also as the source of
  events, in `ComEnumerator.FromObject`, where a late-bound wrapper converts it to an interface, and in a parameter of a
  COM interface that the wrapper implements.
- **An object that only your own instance can expose**: in a `VARIANT`, pass it in an `UnknownWrapper`. One that
  implements `IDispatchProvider` is the exception: it is a late-bound wrapper wherever it goes to COM, also in an
  `UnknownWrapper`.
- **An `UnknownWrapper`** stands for its object wherever an object goes to COM: in an `IUnknown` parameter, a `SAFEARRAY`,
  as the source of events, for `ComDynamic` and in `ComEnumerator.FromObject` as in a `VARIANT`, where it is passed as
  `VT_UNKNOWN`. `new UnknownWrapper(null)` passes a null object, where `null` passes an empty `VARIANT`.
- **A `DispatchWrapper`** is rejected everywhere with an `ArgumentException`, as by `ComVariantMarshaller` of .NET in a
  `VARIANT`: the interface of such a wrapper itself would only have `IUnknown`.

A COM object that fails `QueryInterface` with another error than `E_NOINTERFACE`, e.g. the proxy of a server that has
ended, throws the exception of its `HRESULT` rather than an `InvalidCastException`, also in `ComEnumerator.FromObject`.

### Pointers that come back

The pointer of an object that is exposed to COM converts back to the object for an interface that it implements. For
another declaration of the interface, e.g. of another interop assembly, it gets a COM object of its own, which calls the
object through COM, where `ComInterfaceMarshaller<T>` of .NET throws an `InvalidCastException`: a new one for each
conversion, also of a shared parameter, which is not shared, so that `ComMarshalling.Release` releases it.

The object comes back itself from a `VARIANT` or a `SAFEARRAY`. Where a late-bound wrapper, `EventArguments.Get<T>` or an
array converts it to an interface that its class does not implement, it is asked through its COM interface the same way,
e.g. for another declaration of the interface. When that does not answer, the COM object of its `Dispatch` is asked for
the interface, as when the `VARIANT` held that object.
