# By-reference arguments

A pointer parameter is passed by reference, and how depends on how the member is called:

| Parameter | Interface (vtable) | Late-bound wrapper |
| --- | --- | --- |
| `[out] T*` | `out T` | `out T` |
| `[in, out] T*`, or `T*` without a direction | `ref T` | `ref T` |
| `[in] T*` | `in T` for `VARIANT`s, `SAFEARRAY`s, structures and GUIDs, otherwise `ref T` | `T`, passed by value |
| `void*` | `void*` | `nint` |

- **Interfaces** are called through the vtable, so the argument is the address of the variable, marshalled by the COM source
  generator: the callee reads and writes the value in place.
- **Late-bound wrappers** call `IDispatch::Invoke`, which takes each argument in a `VARIANT`. A `ref` or `out` argument is
  passed as a reference of the type of the parameter, e.g. `VT_BYREF | VT_I4` for a `long*`, since the objects that implement
  `Invoke` with their type information reject a reference to a `VARIANT` there. The new value is converted back to the type
  of the parameter after the call.
  - **Typed references:** the integers and floating-point numbers, `CURRENCY`, `DATE`, `BSTR`, `VARIANT_BOOL`, `SCODE`,
    enums (as `VT_I4`), and `IUnknown*`, `IDispatch*` and interface pointers.
  - **References to a `VARIANT`:** the other types, such as `VARIANT`, `DECIMAL` and structures.
  - **`SAFEARRAY`s:** the array is first passed as a reference to a `VARIANT`, in which Visual Basic and script servers store
    an array of `VARIANT`s whatever element type the type library declares. When the object rejects that argument with a type
    mismatch, the call is repeated with a reference to the `SAFEARRAY` of the declared type, e.g. `VT_BYREF | VT_ARRAY | VT_I4`.
    The elements that come back are converted to the element type of the parameter. The second call needs an array whose
    element type is exactly the declared one: an `int[]` is a `SAFEARRAY` of `VT_I4`, so it is not passed that way to a
    parameter declared as `SAFEARRAY(int)` (`VT_INT`) or `SAFEARRAY(unsigned int)`, and neither is an `object[]` to one
    declared as an array of `IDispatch*` or `IUnknown*`. Such a call fails with the type mismatch of the first one.
  - **`out` arguments** start as a zero or a null pointer of the type of the parameter. A value that cannot be converted to
    that type is passed as a reference to a `VARIANT`.
  - **Properties:** an accessor with by-reference parameters is a `get_X`, `set_X` or `putref_X` method of the wrapper, which
    is invoked as a property, with the value of a setter as the `DISPID_PROPERTYPUT` argument.
- **Events of dispinterfaces** arrive through `IDispatch::Invoke` the same way: the `ref` and `out` parameters of a handler
  are written back to the references that the source passed.
- **`dynamic`** has no declared types, so the variable decides, see [Late binding with `dynamic`](dynamic.md).
- **By hand:** `ComDispatch.Invoke` of CsCom.Runtime, for objects that have no generated wrapper, takes a
  `DispatchArgumentKind` per argument: `Value`, `Variant` (a reference to a `VARIANT`) or `Typed` (a reference to a value of
  the type of the argument, e.g. `VT_BYREF | VT_I4` for an `int`). The new values are written to its `output` span, which must
  have an element for each argument; this is checked before the member is invoked.
