using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.RegularExpressions;
using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Output;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace CsCom;

/// <summary>
/// Defines the command line syntax of the tool.
/// </summary>
internal static class CommandLine
{
    private const string TypeLibraryDescription =
        "The type library: a .tlb file, a .dll, .ocx or .exe file with an embedded type library, a registered LIBID ({GUID} or {GUID}:major.minor) or a ProgID; or a .NET interop assembly, such as a primary interop assembly or the output of TlbImp.";

    private static readonly Argument<string> TypeLibraryArgument = new("TlbPath")
    {
        Description = TypeLibraryDescription,
    };

    private static readonly Argument<string?> UpdateTypeLibraryArgument = new("TlbPath")
    {
        Description = TypeLibraryDescription + " The type library recorded in cscom.json by default.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<string?> TypeFilterOption = new("--type", "-t")
    {
        Description = "A regular expression that matches the names of the types to list.",
        HelpName = "REGEX",
    };

    private static readonly Option<string?> OutputOption = new("--output", "-o")
    {
        Description = "The output directory.",
        HelpName = "PATH",
    };

    private static readonly Option<string?> NameOption = new("--name", "-n")
    {
        Description = "The project and assembly name. Interop.<TlbName> by default.",
        HelpName = "NAME",
    };

    private static readonly Option<string?> NamespaceOption = new("--namespace", "--ns")
    {
        Description = "The namespace of the generated types. Interop.<TlbName> by default.",
        HelpName = "NAMESPACE",
    };

    private static readonly Option<string[]> FrameworkOption = new("--framework", "-f")
    {
        Description = $"The target framework; can be specified multiple times. {TargetFrameworks.Default} by default.",
        HelpName = "TFM",
    };

    private static readonly Option<string[]> RuntimeOption = new("--runtime", "-r")
    {
        Description = "The target runtime identifier; can be specified multiple times. AnyCPU by default.",
        HelpName = "RID",
    };

    private static readonly Option<int?> LanguageVersionOption = new("--csharp-version", "-c")
    {
        Description = "The C# version of the generated code. C# 14 for .NET 10 and C# 15 for .NET 11 by default.",
        HelpName = "VERSION",
    };

    private static readonly Option<bool> SingleFileOption = new("--single-file")
    {
        Description = "Generates all types into a single C# file instead of a file per type.",
    };

    private static readonly Option<bool?> UpdateSingleFileOption = new("--single-file")
    {
        Description = "Whether all types are generated into a single C# file; the value recorded in cscom.json by default.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<SupportCodeMode> SupportCodeOption = new("--support-code")
    {
        Description = "Where the support code (IDispatch, activation, late binding) comes from: the CsCom.Runtime package, or public or internal types emitted into the project.",
        DefaultValueFactory = static _ => SupportCodeMode.Package,
        HelpName = "package|public|internal",
    };

    private const string EventStyleDescription =
        "The code generated for the events of coclasses: a sink class with .NET events (events) or observables (observable) and a handler interface, only the handler interface (interface), or none.";

    private static readonly Option<EventStyle> EventStyleOption = new("--event-style")
    {
        Description = EventStyleDescription,
        DefaultValueFactory = static _ => EventStyle.Events,
        HelpName = "events|observable|interface|none",
    };

    private static readonly Option<EventStyle?> UpdateEventStyleOption = new("--event-style")
    {
        Description = EventStyleDescription + " The value recorded in cscom.json by default.",
        HelpName = "events|observable|interface|none",
    };

    private const string StructPropertiesDescription =
        "Whether structure members converted to .NET types are generated as properties over private fields of the COM types (auto), " +
        "or all members are public fields of the COM types (never).";

    private static readonly Option<StructProperties> StructPropertiesOption = new("--struct-properties")
    {
        Description = StructPropertiesDescription,
        DefaultValueFactory = static _ => StructProperties.Auto,
        HelpName = "auto|never",
    };

    private static readonly Option<StructProperties?> UpdateStructPropertiesOption = new("--struct-properties")
    {
        Description = StructPropertiesDescription + " The value recorded in cscom.json by default.",
        HelpName = "auto|never",
    };

    private const string OverloadsDescription =
        "Generates overloads for the methods whose optional parameters have default values that C# cannot express (e.g. ComBool.True or a DateTime), so that these parameters can be omitted.";

    private static readonly Option<bool> OverloadsOption = new("--overloads-for-default-values")
    {
        Description = OverloadsDescription,
    };

    private static readonly Option<bool?> UpdateOverloadsOption = new("--overloads-for-default-values")
    {
        Description = OverloadsDescription + " The value recorded in cscom.json by default.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    private const string ConfigDescription = "An XML file that adds and configures model and syntax transformers.";

    private static readonly Option<string?> ConfigOption = new("--config")
    {
        Description = ConfigDescription,
        HelpName = "PATH",
    };

    private static readonly Option<string?> UpdateConfigOption = new("--config")
    {
        Description = ConfigDescription + " The file recorded in cscom.json by default; an empty value removes it.",
        HelpName = "PATH",
    };

    private const string ReferencePathDescription =
        "For interop assemblies: an assembly that the interop assembly references, or a directory of such assemblies; can be repeated. The directory of the interop assembly and the global assembly cache are searched as well.";

    private static readonly Option<string[]> ReferencePathOption = new("--reference-path")
    {
        Description = ReferencePathDescription,
        HelpName = "PATH",
    };

    private static readonly Option<string[]> UpdateReferencePathOption = new("--reference-path")
    {
        Description = ReferencePathDescription + " The paths recorded in cscom.json by default; an empty value removes them.",
        HelpName = "PATH",
    };

    private static readonly Option<string?> PackageSourceOption = new("--package-source")
    {
        Description = "An additional NuGet package source that provides the CsCom.Runtime package.",
        HelpName = "PATH",
    };

    private static readonly Option<bool> KeepTempOption = new("--keep-temp")
    {
        Description = "Keeps the temporary project used to build the assembly.",
    };

    private static readonly MarshallingCommandOptions CreateMarshallingOptions = MarshallingCommandOptions.Create(withDefaults: true);

    private static readonly MarshallingCommandOptions UpdateMarshallingOptions = MarshallingCommandOptions.Create(withDefaults: false);

    private static readonly Option<Verbosity> VerbosityOption = new("--verbosity", "-v")
    {
        Description = "The verbosity of the output.",
        DefaultValueFactory = static _ => Verbosity.Normal,
        HelpName = "quiet|minimal|normal|detailed|diagnostic",
        Recursive = true,
    };

    public static RootCommand Create()
    {
        var createProject = new Command("create", "Generates a new C# project with the interop types of a type library or an interop assembly.");
        AddCreateOptions(createProject);
        createProject.SetAction(static result => Run(result, (pipeline, loggerFactory) =>
            new ProjectGenerator(pipeline, loggerFactory).CreateProject(new CreateProjectRequest
            {
                TypeLibraryPath = result.GetRequiredValue(TypeLibraryArgument),
                OutputDirectory = result.GetValue(OutputOption),
                Name = result.GetValue(NameOption),
                Namespace = result.GetValue(NamespaceOption),
                TargetFrameworks = result.GetValue(FrameworkOption) ?? [],
                RuntimeIdentifiers = result.GetValue(RuntimeOption) ?? [],
                LanguageVersion = result.GetValue(LanguageVersionOption),
                SingleFile = result.GetValue(SingleFileOption),
                SupportCode = result.GetValue(SupportCodeOption),
                Marshalling = CreateMarshallingOptions.GetOverrides(result).Apply(new MarshallingOptions()),
                EventStyle = result.GetValue(EventStyleOption),
                StructProperties = result.GetValue(StructPropertiesOption),
                OverloadsForDefaultValues = result.GetValue(OverloadsOption),
                ConfigurationPath = result.GetValue(ConfigOption),
                ReferencePaths = result.GetValue(ReferencePathOption) ?? [],
                PackageSource = result.GetValue(PackageSourceOption),
            })));

        var buildAssembly = new Command("build", "Builds a ready-to-use interop assembly from a type library or an interop assembly.");
        AddCreateOptions(buildAssembly);
        buildAssembly.Options.Add(KeepTempOption);
        buildAssembly.SetAction(static result => Run(result, (pipeline, loggerFactory) =>
            new AssemblyGenerator(pipeline, loggerFactory).BuildAssembly(new BuildAssemblyRequest
            {
                TypeLibraryPath = result.GetRequiredValue(TypeLibraryArgument),
                OutputDirectory = result.GetValue(OutputOption),
                Name = result.GetValue(NameOption),
                Namespace = result.GetValue(NamespaceOption),
                TargetFrameworks = result.GetValue(FrameworkOption) ?? [],
                RuntimeIdentifiers = result.GetValue(RuntimeOption) ?? [],
                LanguageVersion = result.GetValue(LanguageVersionOption),
                SingleFile = result.GetValue(SingleFileOption),
                SupportCode = result.GetValue(SupportCodeOption),
                Marshalling = CreateMarshallingOptions.GetOverrides(result).Apply(new MarshallingOptions()),
                EventStyle = result.GetValue(EventStyleOption),
                StructProperties = result.GetValue(StructPropertiesOption),
                OverloadsForDefaultValues = result.GetValue(OverloadsOption),
                ConfigurationPath = result.GetValue(ConfigOption),
                ReferencePaths = result.GetValue(ReferencePathOption) ?? [],
                PackageSource = result.GetValue(PackageSourceOption) ?? GetBundledPackageSource(),
                KeepTemporaryFiles = result.GetValue(KeepTempOption),
            })));

        var updateProject = new Command("update", "Regenerates the C# files of a project created with 'cscom project create'.")
        {
            UpdateTypeLibraryArgument,
            OutputOption,
            NamespaceOption,
            LanguageVersionOption,
            UpdateSingleFileOption,
        };
        UpdateMarshallingOptions.AddTo(updateProject);
        updateProject.Options.Add(UpdateEventStyleOption);
        updateProject.Options.Add(UpdateStructPropertiesOption);
        updateProject.Options.Add(UpdateOverloadsOption);
        updateProject.Options.Add(UpdateConfigOption);
        updateProject.Options.Add(UpdateReferencePathOption);
        updateProject.SetAction(static result => Run(result, (pipeline, loggerFactory) =>
            new ProjectGenerator(pipeline, loggerFactory).UpdateProject(new UpdateProjectRequest
            {
                TypeLibraryPath = result.GetValue(UpdateTypeLibraryArgument),
                ProjectDirectory = result.GetValue(OutputOption),
                Namespace = result.GetValue(NamespaceOption),
                LanguageVersion = result.GetValue(LanguageVersionOption),
                SingleFile = GetFlag(result, UpdateSingleFileOption),
                Marshalling = UpdateMarshallingOptions.GetOverrides(result),
                EventStyle = result.GetValue(UpdateEventStyleOption),
                StructProperties = result.GetValue(UpdateStructPropertiesOption),
                OverloadsForDefaultValues = GetFlag(result, UpdateOverloadsOption),
                ConfigurationPath = result.GetValue(UpdateConfigOption),
                ReferencePaths = result.GetResult(UpdateReferencePathOption) is { Implicit: false }
                    ? [.. (result.GetValue(UpdateReferencePathOption) ?? []).Where(static path => path.Length > 0)]
                    : null,
            })));

        var createConfig = new Command("create", "Writes a starter cscom.xml configuration file, unless it exists, and the cscom.xsd schema that gives IntelliSense in XML editors.")
        {
            OutputOption,
        };
        createConfig.SetAction(static result => Run(result, (_, loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("cscom");
            var (path, created) = PipelineConfiguration.WriteStarterFiles(Path.GetFullPath(result.GetValue(OutputOption) ?? Environment.CurrentDirectory));
            logger.LogInformation(created ? "Created '{Path}' and its schema" : "Updated the schema of the existing '{Path}'", path);
            return path;
        }));

        var list = new Command("list", "Lists the types and members of a type library or an interop assembly with the names that the transformers of configuration files match.")
        {
            TypeLibraryArgument,
            TypeFilterOption,
        };
        list.Options.Add(ConfigOption);
        list.Options.Add(ReferencePathOption);
        list.SetAction(static result => Run(result, (pipeline, _) => List(result, pipeline), quietByDefault: true));

        return new RootCommand("Generates COM interop code from type libraries and interop assemblies using source-generated COM interop.")
        {
            VerbosityOption,
            new Command("project", "Creates or updates an interop project.") { createProject, updateProject },
            new Command("assembly", "Builds an interop assembly.") { buildAssembly },
            new Command("config", "Creates configuration files.") { createConfig },
            list,
        };
    }

    // Writes the model after the aliases are resolved and the model transformers of the configuration are applied, before
    // the names are sanitized: the names that the selectors of configuration files match.
    private static bool List(ParseResult result, InteropPipeline pipeline)
    {
        Regex? filter = null;
        if (result.GetValue(TypeFilterOption) is { } pattern)
        {
            try
            {
                filter = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                throw new CsComException($"'{pattern}' is not a valid regular expression: {e.Message}", e);
            }
        }

        var library = pipeline.ReadTypeLibrary(result.GetRequiredValue(TypeLibraryArgument), result.GetValue(ReferencePathOption) ?? []);
        var configuration = result.GetValue(ConfigOption) is { } path ? PipelineConfiguration.Load(Path.GetFullPath(path), pipeline.Registry) : null;
        pipeline.TransformModel(library, new CodeGenerationOptions { Namespace = "Interop" }, configuration, sanitizeNames: false);
        ModelPrinter.Write(library, result.InvocationConfiguration.Output, filter);
        return true;
    }

    private static void AddCreateOptions(Command command)
    {
        command.Arguments.Add(TypeLibraryArgument);
        command.Options.Add(OutputOption);
        command.Options.Add(NameOption);
        command.Options.Add(NamespaceOption);
        command.Options.Add(FrameworkOption);
        command.Options.Add(RuntimeOption);
        command.Options.Add(LanguageVersionOption);
        command.Options.Add(SingleFileOption);
        command.Options.Add(SupportCodeOption);
        CreateMarshallingOptions.AddTo(command);
        command.Options.Add(EventStyleOption);
        command.Options.Add(StructPropertiesOption);
        command.Options.Add(OverloadsOption);
        command.Options.Add(ConfigOption);
        command.Options.Add(ReferencePathOption);
        command.Options.Add(PackageSourceOption);
    }

    // A flag of 'project update' without a value means true; when the option is omitted, the recorded value is kept.
    private static bool? GetFlag(ParseResult result, Option<bool?> flag) =>
        result.GetResult(flag) is { Implicit: false } option ? option.GetValueOrDefault<bool?>() ?? true : null;

    // Commands that write their result to the output log only warnings and errors unless a verbosity is specified.
    private static int Run(ParseResult result, Func<InteropPipeline, ILoggerFactory, object> action, bool quietByDefault = false)
    {
        var verbosity = quietByDefault && result.GetResult(VerbosityOption) is null or { Implicit: true } ? Verbosity.Minimal : result.GetValue(VerbosityOption);
        using var loggerFactory = CreateLoggerFactory(verbosity);
        var logger = loggerFactory.CreateLogger("cscom");
        if (!OperatingSystem.IsWindows())
        {
            logger.LogError("cscom only runs on Windows, because it reads type libraries with the OLE Automation APIs.");
            return 1;
        }

        try
        {
            action(new InteropPipeline(loggerFactory), loggerFactory);
            return 0;
        }
        catch (CsComException e)
        {
            logger.LogError("{Message}", e.Message);
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError("{Message}", e.Message);
            return 1;
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Unexpected error: {Message}", e.Message);
            return 1;
        }
    }

    private static ILoggerFactory CreateLoggerFactory(Verbosity verbosity) => LoggerFactory.Create(builder => builder
        .SetMinimumLevel(verbosity switch
        {
            Verbosity.Quiet => LogLevel.Error,
            Verbosity.Minimal => LogLevel.Warning,
            Verbosity.Normal => LogLevel.Information,
            Verbosity.Detailed => LogLevel.Debug,
            _ => LogLevel.Trace,
        })
        .AddConsole(static options =>
        {
            options.FormatterName = CompactConsoleFormatter.FormatterName;
            options.LogToStandardErrorThreshold = LogLevel.Warning;
        })
        .AddConsoleFormatter<CompactConsoleFormatter, ConsoleFormatterOptions>());

    /// <summary>
    /// Gets the folder of the CsCom.Runtime package bundled with the tool, if any.
    /// </summary>
    private static string? GetBundledPackageSource()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "packages");
        return File.Exists(Path.Combine(directory, $"CsCom.Runtime.{ToolInfo.Version}.nupkg")) ? directory : null;
    }

    /// <summary>
    /// The options that select how DATE, CURRENCY and VARIANT_BOOL values and typed SAFEARRAYs are represented. The options
    /// of 'project create' and 'assembly build' have default values; the options of 'project update' only override the values
    /// recorded in cscom.json.
    /// </summary>
    private sealed record MarshallingCommandOptions(
        Option<ValueMarshalling?> Dates,
        Option<ValueMarshalling?> Currencies,
        Option<ValueMarshalling?> Booleans,
        Option<BstrMarshalling?> Bstrs,
        Option<SafeArrayMarshalling?> SafeArrays)
    {
        public static MarshallingCommandOptions Create(bool withDefaults) => new(
            Value("date", "DATE", "DateTime", "double", "ComDate", withDefaults),
            Value("currency", "CURRENCY", "decimal", "long", "ComCurrency", withDefaults),
            Value("bool", "VARIANT_BOOL", "bool", "short", "ComBool", withDefaults),
            Bstr(withDefaults),
            SafeArray(withDefaults));

        public void AddTo(Command command)
        {
            command.Options.Add(Dates);
            command.Options.Add(Currencies);
            command.Options.Add(Booleans);
            command.Options.Add(Bstrs);
            command.Options.Add(SafeArrays);
        }

        public MarshallingOverrides GetOverrides(ParseResult result) => new()
        {
            Dates = result.GetValue(Dates),
            Currencies = result.GetValue(Currencies),
            Booleans = result.GetValue(Booleans),
            Bstrs = result.GetValue(Bstrs),
            SafeArrays = result.GetValue(SafeArrays),
        };

        private static Option<BstrMarshalling?> Bstr(bool withDefaults)
        {
            var option = new Option<BstrMarshalling?>("--bstr-marshalling")
            {
                Description = "How BSTR parameters and return values are represented: string (string), " +
                    "wrapper (ComBstr, which wraps the native string without copying it) or none (the BSTR pointer).",
                HelpName = "string|wrapper|none",
            };

            if (withDefaults)
            {
                option.DefaultValueFactory = static _ => BstrMarshalling.String;
            }

            return option;
        }

        private static Option<SafeArrayMarshalling?> SafeArray(bool withDefaults)
        {
            var option = new Option<SafeArrayMarshalling?>("--safearray-marshalling")
            {
                Description = "How typed SAFEARRAY(T) parameters and return values are represented: array (one-dimensional T[]), " +
                    "sysarray (System.Array of up to three dimensions), wrapper (ComSafeArray<T>) or none (the SAFEARRAY pointer).",
                HelpName = "array|sysarray|wrapper|none",
            };

            if (withDefaults)
            {
                option.DefaultValueFactory = static _ => SafeArrayMarshalling.Array;
            }

            return option;
        }

        private static Option<ValueMarshalling?> Value(string name, string type, string bcl, string none, string wrapper, bool withDefaults)
        {
            var option = new Option<ValueMarshalling?>($"--{name}-marshalling")
            {
                Description = $"How {type} parameters, return values and structure members are represented: bcl ({bcl}), none ({none}) or wrapper ({wrapper}). " +
                    $"With '--struct-properties never', bcl structure members are {none}.",
                HelpName = "bcl|none|wrapper",
            };

            if (withDefaults)
            {
                option.DefaultValueFactory = static _ => ValueMarshalling.Bcl;
            }

            return option;
        }
    }
    private enum Verbosity
    {
        Quiet,
        Minimal,
        Normal,
        Detailed,
        Diagnostic,
        Q = Quiet,
        M = Minimal,
        N = Normal,
        D = Detailed,
        Diag = Diagnostic,
    }
}
