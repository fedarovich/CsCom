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

The results of a run on .NET 11.0.0 (X64 RyuJIT, x86-64-v3), Windows 11, 13th Gen Intel Core i7-1360P, with
BenchmarkDotNet 0.16.0-preview.2 and its default job. The ratio is the mean of CsCom divided by the mean of the built-in COM
interop, so a ratio below 1 means that CsCom is faster; the memory is the managed memory allocated by a call:

| Class | Call | Built-in | CsCom | Ratio | Built-in memory | CsCom memory |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `StaticDispatchBenchmarks` | `Count` | 10.51 ns | 11.40 ns | 1.08 | - | - |
| | `Exists` | 57.09 ns | 58.08 ns | 1.02 | - | - |
| | `GetItem` | 89.74 ns | 80.26 ns | 0.89 | 24 B | 24 B |
| | `SetItem` | 89.32 ns | 103.27 ns | 1.16 | - | - |
| | `BuildPath` | 102.60 ns | 98.51 ns | 0.96 | 104 B | 104 B |
| | `Enumerate` | 17,813.52 ns | 8,340.56 ns | 0.47 | 3608 B | 4294 B |
| `DynamicDispatchBenchmarks` | `Count` | 377.26 ns | 247.21 ns | 0.66 | 96 B | 48 B |
| | `Exists` | 556.54 ns | 276.92 ns | 0.50 | 184 B | 72 B |
| | `GetItem` | 569.01 ns | 287.99 ns | 0.51 | 184 B | 72 B |
| | `SetItem` | 563.17 ns | 345.21 ns | 0.61 | 104 B | 112 B |
| | `BuildPath` | 979.97 ns | 532.81 ns | 0.54 | 360 B | 176 B |
| | `Enumerate` | 17,696.97 ns | 8,555.24 ns | 0.48 | 3584 B | 4319 B |
| `DynamicKeywordBenchmarks` | `Count` | 245.20 ns | 275.13 ns | 1.12 | 24 B | 48 B |
| | `Exists` | 351.89 ns | 305.35 ns | 0.87 | 48 B | 104 B |
| | `GetItem` | 358.68 ns | 304.65 ns | 0.85 | 48 B | 104 B |
| | `SetItem` | 371.90 ns | 368.12 ns | 0.99 | 24 B | 144 B |
| | `BuildPath` | 624.52 ns | 578.28 ns | 0.93 | 152 B | 216 B |
| | `Enumerate` | 20,456.70 ns | 8,880.83 ns | 0.43 | 3536 B | 4370 B |

- **Static dispatch** costs about the same with both, within 16 % for a single call.
- **Late-bound wrappers** take half to two thirds of the time of the dispinterfaces of the built-in COM interop.
- **`dynamic`** is within 15 % for a single call, and CsCom allocates more memory, from 1.4 times (`BuildPath`) to six times (`SetItem`).
- **Enumerating** takes about half the time with CsCom in all three, with about 20 % more memory.
