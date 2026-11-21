# Benchmarks

`src/CsCom.Benchmarks` measures calls of `Scripting.Dictionary` and `Scripting.FileSystemObject`, in-process COM objects of
every Windows installation that do little work themselves, with CsCom and with the built-in COM interop of .NET, which is
the baseline of each call:

```powershell
dotnet run -c Release -f net11.0-windows --project src/CsCom.Benchmarks -- --filter *                    # all benchmarks
dotnet run -c Release -f net10.0-windows --project src/CsCom.Benchmarks -- --filter *StaticDispatch*     # one class, on .NET 10
dotnet run -c Release -f net11.0-windows --project src/CsCom.Benchmarks -- --filter *GetItem* --job Short
```

| Class | CsCom | Built-in COM interop |
| --- | --- | --- |
| `StaticDispatchBenchmarks` | The generated `[GeneratedComInterface]` interfaces, called through the vtable. | `[ComImport]` dual interfaces, as TlbImp generates them. |
| `DynamicDispatchBenchmarks` | Late-bound wrappers (`DispatchObject`), which invoke the members by their DISPIDs through `IDispatch::Invoke`. | `[ComImport]` dispinterfaces (`InterfaceIsIDispatch`). |
| `DynamicKeywordBenchmarks` | C# `dynamic` with CsCom.Runtime.Dynamic. | C# `dynamic` with the COM binder of the runtime. |

- **Calls:** a property without arguments (`Count`), `VARIANT` arguments and results (`Exists`, `GetItem`, `SetItem`), `BSTR`
  arguments and results (`BuildPath`) and the enumeration of 100 keys with `IEnumVARIANT` (`Enumerate`). All classes have the
  memory diagnoser, so the results include the managed memory allocated by a call.
- **Code under test:** `src/CsCom.Benchmarks.Interop` imports the props and targets of CsCom.Build from the working tree and
  generates the code of `scrrun.dll` with the task and the tool built from it, and the benchmarks reference CsCom.Runtime and
  CsCom.Runtime.Dynamic as projects. It is a separate assembly, because the generated code disables runtime marshalling for
  its assembly, which the `[ComImport]` declarations need. The Scripting Runtime has no dispinterfaces, so the late-bound
  wrappers are written by hand like the generated ones.
- **Apartment:** `Scripting.Dictionary` is apartment-threaded, so the benchmark methods are marked with `[STAThread]`, which
  makes BenchmarkDotNet run them on a single-threaded apartment thread; the setup fails otherwise, because each call would
  be marshalled to another thread.
- BenchmarkDotNet is a preview version, 0.16, because 0.15.8 does not run on .NET 11. The results are written to
  `BenchmarkDotNet.Artifacts`, which is not committed.

The results of a run on 2026-11-20, on .NET 11.0.0 (X64 RyuJIT, x86-64-v3), Windows 11, 13th Gen Intel Core i7-1360P, with
BenchmarkDotNet 0.16.0-preview.2 and its default job. The ratio is the mean of CsCom divided by the mean of the built-in COM
interop, so a ratio below 1 means that CsCom is faster; the memory is the managed memory allocated by a call:

| Class | Call | Built-in | CsCom | Ratio | Built-in memory | CsCom memory |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `StaticDispatchBenchmarks` | `Count` | 8.78 ns | 9.51 ns | 1.08 | - | - |
| | `Exists` | 55.60 ns | 54.16 ns | 0.98 | - | - |
| | `GetItem` | 84.34 ns | 74.14 ns | 0.88 | 24 B | 24 B |
| | `SetItem` | 83.58 ns | 97.33 ns | 1.17 | - | - |
| | `BuildPath` | 95.25 ns | 90.51 ns | 0.95 | 104 B | 104 B |
| | `Enumerate` | 17,443.54 ns | 8,102.22 ns | 0.46 | 3608 B | 4277 B |
| `DynamicDispatchBenchmarks` | `Count` | 362.91 ns | 233.68 ns | 0.64 | 96 B | 48 B |
| | `Exists` | 557.40 ns | 270.09 ns | 0.48 | 184 B | 72 B |
| | `GetItem` | 560.06 ns | 273.96 ns | 0.49 | 184 B | 72 B |
| | `SetItem` | 549.61 ns | 332.90 ns | 0.61 | 104 B | 112 B |
| | `BuildPath` | 905.25 ns | 520.47 ns | 0.58 | 360 B | 176 B |
| | `Enumerate` | 17,514.6 ns | 8,417.7 ns | 0.48 | 3584 B | 4328 B |
| `DynamicKeywordBenchmarks` | `Count` | 227.80 ns | 260.20 ns | 1.14 | 24 B | 48 B |
| | `Exists` | 332.55 ns | 294.13 ns | 0.88 | 48 B | 104 B |
| | `GetItem` | 344.23 ns | 292.67 ns | 0.85 | 48 B | 104 B |
| | `SetItem` | 378.76 ns | 371.40 ns | 0.98 | 24 B | 144 B |
| | `BuildPath` | 583.80 ns | 550.91 ns | 0.94 | 152 B | 216 B |
| | `Enumerate` | 20,486.2 ns | 8,522.6 ns | 0.42 | 3536 B | 4366 B |

- **Static dispatch** costs about the same with both, within 17 % for a single call.
- **Late-bound wrappers** take half to two thirds of the time of the dispinterfaces of the built-in COM interop.
- **`dynamic`** is within 15 % for a single call, and CsCom allocates more memory, from 1.4 times (`BuildPath`) to six times (`SetItem`).
- **Enumerating** takes about half the time with CsCom in all three, with about 20 % more memory.
