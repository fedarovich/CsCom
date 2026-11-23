# CsCom.Runtime

The support code of the COM interop code that [CsCom](https://www.nuget.org/packages/CsCom) generates: `IDispatch` and
`IEnumVARIANT`, `ComActivator`, the marshallers of `VARIANT`s, `SAFEARRAY`s, `BSTR`s, `DATE` and `CURRENCY`, the base class
of the late-bound wrappers of dispinterfaces, and the event sinks. It is built on the source-generated COM interop of .NET,
for .NET 10 and .NET 11, and can be trimmed and compiled with Native AOT.

## Usage

It is rarely referenced by hand: the projects that `cscom project create` generates reference it, and the
[CsCom.Build](https://www.nuget.org/packages/CsCom.Build) package depends on it. With `--support-code public` or `internal`
(`CsComSupportCode` of CsCom.Build), the support code is generated into the project instead, which then does not need this
package.

Its API is also there for the code that uses the generated types, e.g. to create a COM object or to get one that is
running:

```csharp
using CsCom.Runtime;
using Interop.Excel; // generated from the type library of Excel

_Application excel = ComActivator.CreateInstance<_Application>("Excel.Application");
_Application running = ComActivator.GetActiveInstance<_Application>("Excel.Application");
```

## Documentation

See [cscom.fedarovich.com](https://cscom.fedarovich.com/): [Support code](https://cscom.fedarovich.com/articles/support-code.html),
[Generated code](https://cscom.fedarovich.com/articles/generated-code.html) and the
[API reference](https://cscom.fedarovich.com/api/index.html).
