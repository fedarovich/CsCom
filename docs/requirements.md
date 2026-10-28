# Introduction

CsCom is a command line tool that produces COM-interop assemblies similar to TlbImp. 
However, unlike TlbImp, it does not rely on .NET built-in COM-interop, 
but generates the code relying on Source Generated COM-interop added in .NET 8,
so they can be used with Native AOT as well.

# Requirments

## High Level Requirements

1. The tool can generate C# project targeting .NET 10 or later on Windows only.
    + The tool will add C# files source generated types for all types (classes, interface, enums) from the COM Type Library specified as an input argument.
    + The tool will generate C# 14 code when targeting .NET 10 and C# 15 code when targeting .NET 11 by default. It should be possible to specify later C# version by a command line option.
    + By default, the tool will generate a .cs file per each type, however it should be possible to allow outputting all types in a single C# file.
2. The tool can generate ready to use .NET assembly from TLB by creating a temporary project in the temporary directory, building it and copying the compiled assembly into the output location.
3. The tool must allow specifying target framework(s), runtime identifier(s), C# version to use.
4. The tool must allow specifying the project/assembly name. If not specified, `Interop.<TlbName>` will be used by default.
5. The tool must allow specifying the namespace. If not specified, `Interop.<TlbName>` will be used by default.
6. The commands accept several sources, which are generated together, each into its own namespace (`Interop.<SourceName>` by default), with the types coming from the libraries they use in the namespaces of these libraries. With several sources, the project/assembly/package name must be specified explicitly.
7. A NuGet package with MSBuild tasks (CsCom.Build) generates the interop code during the build of a project.
    + The sources are added to the project as `CsComReference` items; a project can have several of them.
    + Each source is generated into its own namespace, `Interop.<SourceName>` by default, which the `Namespace` metadata of the item can override.
    + The types coming from implicitly (transitively) referenced sources are generated into their own namespaces with the same naming convention, `Interop.<SourceName>`.

## Libraries to Use

1. System.CommandLine for parsing command line arguments.
2. Microsoft.Extensions.Logging to output logs to the console.

## Architecture

The tool accept a TLB file as an input and runs it through the processing pipeline. The pipeline consists of the following steps:

1. Read the TLB and build a data model for downstream processing. The tool must define its own data model types and fill them in based on what it reads from TLB interop APIs.
2. Run custom transformers that can change the data model. The way how the transformers will be added and configured will be decided later.
3. Generate Roslyn code tree(s) with interop types.
4. Run custom transformers that can change the Roslyn code trees.
5. Produce the outputs.

## Command line syntax

### Common commands

Generating a new C# project:

```powershell
cscom project create [<OPTIONS>] <Source>
```

Updating generated C# files in the project:

```powershell
cscom project update [<OPTIONS>] <Source>
```

Generating a .Net assembly:
```powershell
cscom assembly build [<OPTIONS>] <Source>
```

Packing the .Net assembly into a NuGet package:
```powershell
cscom assembly pack [<OPTIONS>] <Source>
```

### Well-known options:

+ `--output|-o <PATH>` - the output directory

+ `--name|-n <NAME>` - the project/assembly name (`Interop.<TlbName>` by default).

+ `--namespace|--ns <NAMESPACE>` - the namespace (`Interop.<TlbName>` by default).

+ `--framework|-f <TFM>` - the target framework. Can be specified multi times. `net10.0-windows` by default

+ `--runtime|-r <RID>` - the target runtime. Can be specified multi times. `AnyCPU` by default.

+ `--csharp-version|--cs|-l <VERSION>` - the C# version to use. If not specified, the default is C# 14 for .NET 10 and C# 15 for .NET 11.

+ `--single-file` - if specified, only a single C# file containing all types will be produced.
