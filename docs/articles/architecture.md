# Architecture

The tool runs the type library through a pipeline (`CsCom.Core`):

1. `TypeLibraryReader` reads the type library with source-generated `ITypeLib`/`ITypeInfo` bindings into the model (`CsCom.Model`),
   or `InteropAssemblyReader` reads an interop assembly with `MetadataLoadContext` into the same model.
2. The model transformers (`IModelTransformer`) change the model. The built-in ones resolve aliases first and sanitize names last;
   those of `InteropPipeline.ModelTransformers` and of the [configuration file](configuration.md) run in between.
3. `CSharpEmitter` generates the Roslyn syntax trees.
4. The syntax transformers (`ISyntaxTransformer`) change the syntax trees. The generated declarations carry annotations that
   associate them with the model elements, which `TransformationContext.Annotations` resolves: `GetElement(node)` gives the
   element of a node and its role (`SyntaxRole.Declaration`, `Extensions`, `EventSink`, …), and `GetNodes(root, element, role)`
   finds the nodes generated for an element.
5. `ProjectGenerator` and `AssemblyGenerator` write the project, or build or pack the assembly.

For CsCom.Build, `LibrarySetGenerator` generates several libraries into one project: it reads each reference, merges the types
that they import from other libraries into a model per library, transforms each library with its options and configuration, and
emits it with the namespaces of all the libraries. The commands and CsCom.Build both use it: `LibrarySetBuild` runs the request
of the MSBuild task, which the task passes to the hidden `build-generate` command of the bundled cscom tool.
`CsComReferenceEditor` edits the `CsComReference` items of project files for the `reference` commands.

| Project | Description |
| --- | --- |
| `src/CsCom` | The command line tool (`cscom`), packed as a .NET tool. |
| `src/CsCom.Build` | The CsCom.Build package: the MSBuild props, targets and task (netstandard2.0), which runs the cscom tool bundled in its `tools` folder. |
| `src/CsCom.Core` | The pipeline. |
| `src/CsCom.Runtime` | The support code used by the generated code, built for `net10.0` and `net11.0`: source-generated COM expects a base interface from another assembly (`IDispatch`) to be compiled for the same target framework (SYSLIB1230). Its sources are also embedded into CsCom.Core for the `public` and `internal` modes. |
| `src/CsCom.Runtime.Dynamic` | The late binding with `dynamic`, which only uses the public API of CsCom.Runtime (e.g. `ComDispatch`), for the same target frameworks. Its sources are embedded like those of CsCom.Runtime, for `--dynamic`. |
| `src/CsCom.Tests` | MSTest and Verify tests, for `net10.0-windows` and `net11.0-windows`. |
| `src/CsCom.Benchmarks` | BenchmarkDotNet benchmarks that compare CsCom with the built-in COM interop, see [Benchmarks](benchmarks.md). |
| `src/CsCom.Benchmarks.Interop` | The interop code of `scrrun.dll` that the benchmarks call, generated during the build by the task and the tool of the working tree. |
