---
uid: CsCom.Runtime.ComDynamic
remarks: *content
---
<!--
  docfx does not generate the members of C# 14 extension blocks yet (https://github.com/dotnet/docfx/issues/11010), so this
  file replaces the remarks of ComDynamic: the first paragraph is the <remarks> of the class, and the table lists the members
  of its extension(ComActivator) block, from their XML documentation. Keep both in sync with DynamicDispatchObject.cs.
-->
A late-bound wrapper or a `dynamic` object stands for its COM object. An object of the application that is exposed to COM
(`[GeneratedComClass]`) is late bound itself when it implements `IDispatch`, also when it implements
<xref:CsCom.Runtime.IDispatchProvider>, and otherwise through the
<xref:CsCom.Runtime.IDispatchProvider.Dispatch> that it provides, also for its events.

### Extension members of ComActivator

`ComDynamic` also declares static extension members of <xref:CsCom.Runtime.ComActivator> (C# 14), which are called on
`ComActivator`, e.g. `ComActivator.CreateDynamicInstance("Excel.Application")`. All of them require dynamic code
(`[RequiresDynamicCode]`) and throw an `InvalidCastException` when the object does not implement `IDispatch`.

| Member | Description |
| --- | --- |
| `CreateDynamicInstance(Guid clsid, ClassContext context = ComActivator.DefaultContext)` | Creates an instance of the COM class identified by `clsid`. `context` is the <xref:CsCom.Runtime.ClassContext> in which the code that manages the new object runs. |
| `CreateDynamicInstance(string progId, ClassContext context = ComActivator.DefaultContext)` | Creates an instance of the COM class identified by its ProgID, e.g. `Excel.Application`. |
| `GetActiveDynamicInstance(Guid clsid)`<br>`GetActiveDynamicInstance(string progId)` | Gets the running instance of the class from the running object table. Throws a `COMException` (`MK_E_UNAVAILABLE`) when no instance is registered. |
| `TryGetActiveDynamicInstance(Guid clsid, out dynamic? instance)`<br>`TryGetActiveDynamicInstance(string progId, out dynamic? instance)` | Gets the running instance of the class if one is registered in the running object table; returns `false` otherwise, also when the ProgID is not registered. |
| `BindToDynamicMoniker(string name)` | Gets the object identified by a moniker display name, like VB's `GetObject`: the path of a document, `winmgmts:`, … Throws a `COMException` when the name is not a valid moniker or the object cannot be bound. |

See [Late binding with dynamic](../articles/dynamic.md).
