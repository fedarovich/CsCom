using System.CommandLine;
using System.CommandLine.Parsing;
using CsCom.CodeGeneration;
using CsCom.Output;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace CsCom;

/// <summary>
/// Defines the command line syntax of the tool.
/// </summary>
internal static class CommandLine
{
    private static readonly Argument<string> TypeLibraryArgument = new("TlbPath")
    {
        Description = "The type library: a .tlb file or a .dll, .ocx or .exe file with an embedded type library.",
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

    private static readonly Option<string?> PackageSourceOption = new("--package-source")
    {
        Description = "An additional NuGet package source that provides the CsCom.Runtime package.",
        HelpName = "PATH",
    };

    private static readonly Option<bool> KeepTempOption = new("--keep-temp")
    {
        Description = "Keeps the temporary project used to build the assembly.",
    };

    private static readonly MarshallingCommandOptions NewMarshallingOptions = MarshallingCommandOptions.Create(withDefaults: true);

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
        var newProject = new Command("project", "Generates a new C# project with the interop types of a type library.");
        AddNewOptions(newProject);
        newProject.SetAction(static result => Run(result, (pipeline, loggerFactory) =>
            new ProjectGenerator(pipeline, loggerFactory).CreateProject(new NewProjectRequest
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
                Marshalling = NewMarshallingOptions.GetOverrides(result).Apply(new MarshallingOptions()),
                EventStyle = result.GetValue(EventStyleOption),
                PackageSource = result.GetValue(PackageSourceOption),
            })));

        var newAssembly = new Command("assembly", "Builds a ready-to-use interop assembly from a type library.");
        AddNewOptions(newAssembly);
        newAssembly.Options.Add(KeepTempOption);
        newAssembly.SetAction(static result => Run(result, (pipeline, loggerFactory) =>
            new AssemblyGenerator(pipeline, loggerFactory).CreateAssembly(new NewAssemblyRequest
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
                Marshalling = NewMarshallingOptions.GetOverrides(result).Apply(new MarshallingOptions()),
                EventStyle = result.GetValue(EventStyleOption),
                PackageSource = result.GetValue(PackageSourceOption) ?? GetBundledPackageSource(),
                KeepTemporaryFiles = result.GetValue(KeepTempOption),
            })));

        var updateProject = new Command("project", "Regenerates the C# files of a project created with 'cscom new project'.")
        {
            TypeLibraryArgument,
            OutputOption,
            NamespaceOption,
            LanguageVersionOption,
            UpdateSingleFileOption,
        };
        UpdateMarshallingOptions.AddTo(updateProject);
        updateProject.Options.Add(UpdateEventStyleOption);
        updateProject.SetAction(static result => Run(result, (pipeline, loggerFactory) =>
            new ProjectGenerator(pipeline, loggerFactory).UpdateProject(new UpdateProjectRequest
            {
                TypeLibraryPath = result.GetRequiredValue(TypeLibraryArgument),
                ProjectDirectory = result.GetValue(OutputOption),
                Namespace = result.GetValue(NamespaceOption),
                LanguageVersion = result.GetValue(LanguageVersionOption),
                SingleFile = GetSingleFile(result),
                Marshalling = UpdateMarshallingOptions.GetOverrides(result),
                EventStyle = result.GetValue(UpdateEventStyleOption),
            })));

        return new RootCommand("Generates COM interop code from type libraries using source-generated COM interop.")
        {
            VerbosityOption,
            new Command("new", "Creates a new interop project or assembly.") { newProject, newAssembly },
            new Command("update", "Updates an interop project.") { updateProject },
        };
    }

    private static void AddNewOptions(Command command)
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
        NewMarshallingOptions.AddTo(command);
        command.Options.Add(EventStyleOption);
        command.Options.Add(PackageSourceOption);
    }

    // "--single-file" without a value means true; when the option is omitted, the recorded value is kept.
    private static bool? GetSingleFile(ParseResult result) =>
        result.GetResult(UpdateSingleFileOption) is { Implicit: false } option ? option.GetValueOrDefault<bool?>() ?? true : null;

    private static int Run(ParseResult result, Func<InteropPipeline, ILoggerFactory, object> action)
    {
        using var loggerFactory = CreateLoggerFactory(result.GetValue(VerbosityOption));
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
    /// The options that select how DATE, CURRENCY and VARIANT_BOOL values are represented. The options of the 'new' commands
    /// have default values; the options of 'update project' only override the values recorded in cscom.json.
    /// </summary>
    private sealed record MarshallingCommandOptions(
        Option<ParameterMarshalling?> DateParameters,
        Option<FieldMarshalling?> DateFields,
        Option<ParameterMarshalling?> CurrencyParameters,
        Option<FieldMarshalling?> CurrencyFields,
        Option<ParameterMarshalling?> BoolParameters,
        Option<FieldMarshalling?> BoolFields)
    {
        public static MarshallingCommandOptions Create(bool withDefaults) => new(
            Parameter("date", "DATE", "DateTime", "double", "ComDate", withDefaults),
            Field("date", "DATE", "double", "ComDate", withDefaults),
            Parameter("currency", "CURRENCY", "decimal", "long", "ComCurrency", withDefaults),
            Field("currency", "CURRENCY", "long", "ComCurrency", withDefaults),
            Parameter("bool", "VARIANT_BOOL", "bool", "short", "ComBool", withDefaults),
            Field("bool", "VARIANT_BOOL", "short", "ComBool", withDefaults));

        public void AddTo(Command command)
        {
            command.Options.Add(DateParameters);
            command.Options.Add(DateFields);
            command.Options.Add(CurrencyParameters);
            command.Options.Add(CurrencyFields);
            command.Options.Add(BoolParameters);
            command.Options.Add(BoolFields);
        }

        public MarshallingOverrides GetOverrides(ParseResult result) => new()
        {
            DateParameters = result.GetValue(DateParameters),
            DateFields = result.GetValue(DateFields),
            CurrencyParameters = result.GetValue(CurrencyParameters),
            CurrencyFields = result.GetValue(CurrencyFields),
            BoolParameters = result.GetValue(BoolParameters),
            BoolFields = result.GetValue(BoolFields),
        };

        private static Option<ParameterMarshalling?> Parameter(string name, string type, string managed, string com, string wrapper, bool withDefaults)
        {
            var option = new Option<ParameterMarshalling?>($"--{name}-parameter-marshalling")
            {
                Description = $"How {type} parameters and return values are represented: managed ({managed}), com ({com}) or wrapper ({wrapper}).",
                HelpName = "managed|com|wrapper",
            };

            if (withDefaults)
            {
                option.DefaultValueFactory = static _ => ParameterMarshalling.Managed;
            }

            return option;
        }

        private static Option<FieldMarshalling?> Field(string name, string type, string com, string wrapper, bool withDefaults)
        {
            var option = new Option<FieldMarshalling?>($"--{name}-field-marshalling")
            {
                Description = $"How {type} structure fields are represented: com ({com}) or wrapper ({wrapper}).",
                HelpName = "com|wrapper",
            };

            if (withDefaults)
            {
                option.DefaultValueFactory = static _ => FieldMarshalling.Com;
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
