# API reference

The API of the support code that the generated code calls, from two NuGet packages:

- **CsCom.Runtime** ([![CsCom.Runtime](https://img.shields.io/nuget/v/CsCom.Runtime.svg?label=nuget)](https://www.nuget.org/packages/CsCom.Runtime)):
  the COM interfaces, the marshallers, the late-bound wrappers and the events. The generated projects reference it with
  the default `--support-code package`, and CsCom.Build depends on it.
- **CsCom.Runtime.Dynamic** ([![CsCom.Runtime.Dynamic](https://img.shields.io/nuget/v/CsCom.Runtime.Dynamic.svg?label=nuget)](https://www.nuget.org/packages/CsCom.Runtime.Dynamic)):
  the late binding with C# `dynamic`, see [Late binding with dynamic](../articles/dynamic.md). It extends the types of
  CsCom.Runtime, so it shares its namespace.

Both declare their types in the <xref:CsCom.Runtime> namespace, and both target .NET 10 and .NET 11 with the same API. With
`--support-code public` or `internal`, the same types are emitted into the generated project instead, in the
`<RootNamespace>.ComSupport` namespace, see [Support code](../articles/support-code.md).

## Activation

| Type | Description |
| --- | --- |
| <xref:CsCom.Runtime.ComActivator> | Creates COM objects by CLSID or ProgID, gets running instances and binds monikers, as shared or unique objects. |
| <xref:CsCom.Runtime.ClassContext> | The execution contexts in which a COM class can be activated (`CLSCTX`). |

## COM interfaces

| Type | Description |
| --- | --- |
| <xref:CsCom.Runtime.IDispatch> | The OLE Automation `IDispatch` interface, which dual interfaces derive from. |
| <xref:CsCom.Runtime.IEnumVARIANT> | The `IEnumVARIANT` interface of the `_NewEnum` members of collections. |
| <xref:CsCom.Runtime.ComEnumerator> | Enumerates an `IEnumVARIANT` as an `IEnumerator<object?>`. |
| <xref:CsCom.Runtime.IConnectionPointContainer>, <xref:CsCom.Runtime.IConnectionPoint> | The interfaces through which event sinks are connected. |
| <xref:CsCom.Runtime.IProvideClassInfo> | Gives the type information of the coclass of an object. |

## Interface pointers

| Type | Description |
| --- | --- |
| <xref:CsCom.Runtime.ComMarshalling> | Configures and performs the conversion of interface pointers: your own `ComWrappers`, releasing unique objects. See [Interface pointers](../articles/interface-pointers.md). |
| <xref:CsCom.Runtime.ComObjectMarshaller%601>, <xref:CsCom.Runtime.UniqueComObjectMarshaller%601> | Marshal COM interfaces as shared objects (one per COM object) or unique objects (one per interface pointer). |
| <xref:CsCom.Runtime.DispatchWrapperMarshaller%601>, <xref:CsCom.Runtime.UniqueDispatchWrapperMarshaller%601> | Marshal late-bound dispinterface wrappers as `IDispatch` pointers. |

## Values

| Type | Description |
| --- | --- |
| <xref:CsCom.Runtime.VariantMarshaller>, <xref:CsCom.Runtime.OptionalVariantMarshaller> | Marshal `object` values as `VARIANT`s. |
| <xref:CsCom.Runtime.ComBstr>, <xref:CsCom.Runtime.ComBstrMarshaller> | A `BSTR` accessed in place, for `--bstr-marshalling wrapper`, see [BSTRs](../articles/bstrs.md). |
| <xref:CsCom.Runtime.ComBstrValue> | A `BSTR` field of a structure, see [Structures](../articles/structures.md). |
| <xref:CsCom.Runtime.SafeArrayMarshaller%601>, <xref:CsCom.Runtime.SafeArrayMarshaller>, <xref:CsCom.Runtime.UnknownSafeArrayMarshaller> | Marshal .NET arrays as `SAFEARRAY`s, see [SAFEARRAYs](../articles/safearrays.md). |
| <xref:CsCom.Runtime.ComSafeArray%601>, <xref:CsCom.Runtime.ComSafeArrayMarshaller%601> | A `SAFEARRAY` of any rank with its native bounds, for `--safearray-marshalling wrapper`. |
| <xref:CsCom.Runtime.ComDate>, <xref:CsCom.Runtime.ComCurrency>, <xref:CsCom.Runtime.ComBool> | `DATE`, `CURRENCY` and `VARIANT_BOOL` with the layout of the COM types, for the `wrapper` [marshalling options](../articles/marshalling.md). |
| <xref:CsCom.Runtime.DateMarshaller>, <xref:CsCom.Runtime.CurrencyMarshaller> | Marshal `DateTime` and `decimal` as `DATE` and `CURRENCY`. |

## Late binding

| Type | Description |
| --- | --- |
| <xref:CsCom.Runtime.DispatchObject> | The base class of the late-bound wrappers generated for dispinterfaces. |
| <xref:CsCom.Runtime.IDispatchProvider>, <xref:CsCom.Runtime.IDispatchWrapper%601> | Implemented by the objects that wrap an `IDispatch`, such as the late-bound wrappers. |
| <xref:CsCom.Runtime.ComDispatch> | Resolves and invokes members through `IDispatch` by name or DISPID, for objects without a generated wrapper, and reads their type information. |
| <xref:CsCom.Runtime.DispatchArgumentKind>, <xref:CsCom.Runtime.DispatchFlags>, <xref:CsCom.Runtime.DispatchMemberKinds>, <xref:CsCom.Runtime.DispIds> | The arguments, invocation kinds, member kinds and well-known DISPIDs of `ComDispatch`. |
| <xref:CsCom.Runtime.DispParams>, <xref:CsCom.Runtime.ExcepInfo> | The OLE Automation `DISPPARAMS` and `EXCEPINFO` structures. |

## Events

| Type | Description |
| --- | --- |
| <xref:CsCom.Runtime.EventConnection> | Connects an event sink to a connection point and disconnects it, see [Events](../articles/events.md). |
| <xref:CsCom.Runtime.EventGate> | The gate of a connection, through which a sink passes events to its handler. |
| <xref:CsCom.Runtime.DispatchEventSink>, <xref:CsCom.Runtime.IDispatchEventHandler>, <xref:CsCom.Runtime.EventArguments> | Receive the events of dispinterfaces through `IDispatch::Invoke`. |
| <xref:CsCom.Runtime.EventSubject%601>, <xref:CsCom.Runtime.EventSubject> | The hot observables of the event sinks generated with `--event-style observable`. |
| <xref:CsCom.Runtime.EventInterface>, <xref:CsCom.Runtime.EventMember> | The source dispinterfaces of a coclass and their events, from `ComDispatch.GetEventInterfaces`. |

## Late binding with `dynamic` (CsCom.Runtime.Dynamic)

| Type | Description |
| --- | --- |
| <xref:CsCom.Runtime.ComDynamic> | `AsDynamic`, `ToDynamic`, the events of `dynamic` objects, and the extension members of `ComActivator` such as `CreateDynamicInstance` and `GetActiveDynamicInstance`. |
| <xref:CsCom.Runtime.DynamicDispatchObject> | The `dynamic` object of a COM object, which resolves members by name with `IDispatch::GetIDsOfNames`. |
