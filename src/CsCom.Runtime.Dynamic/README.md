# CsCom.Runtime.Dynamic

Late binding with C# `dynamic` for COM objects: members are resolved by name with `IDispatch::GetIDsOfNames` and invoked
with `IDispatch::Invoke`. It works with the COM interop code that [CsCom](https://www.nuget.org/packages/CsCom) generates,
and on its own, without interop types. It extends [CsCom.Runtime](https://www.nuget.org/packages/CsCom.Runtime).

## Usage

```csharp
using CsCom.Runtime;

dynamic fso = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject"); // by ProgID or CLSID
string path = fso.BuildPath(@"C:\Windows", "System32");
string drive = fso.GetFolder(@"C:\Windows").Drive.DriveLetter; // returned COM objects are dynamic too

dynamic excel = ComActivator.GetActiveDynamicInstance("Excel.Application"); // a running instance
dynamic shape = typedShape.AsDynamic(); // a COM object of the generated interop code
```

The `--dynamic` option of the cscom tool, or the `CsComDynamic` property of CsCom.Build, also generates `CreateDynamic()`
for the coclasses.

`dynamic` needs dynamic code, which Native AOT does not support: the API is annotated with `[RequiresDynamicCode]`, so the
compiler warns where an AOT application uses it.

## Documentation

See [cscom.fedarovich.com](https://cscom.fedarovich.com/), and
[Late binding with dynamic](https://cscom.fedarovich.com/articles/dynamic.html) in particular.
