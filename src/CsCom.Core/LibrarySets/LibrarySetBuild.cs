using System.Globalization;
using System.Text;
using CsCom.CodeGeneration;
using CsCom.Output;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.LibrarySets;

/// <summary>
/// Generates the code of the <c>CsComReference</c> items of a project for the CsCom.Build MSBuild task: validates the
/// <see cref="BuildRequest"/>, generates the libraries with <see cref="LibrarySetGenerator"/>, writes the files into the output
/// directory and records the state that the task uses to skip the generation when nothing changed.
/// </summary>
/// <remarks>
/// The state file (<see cref="StateFileName"/>) is a text file with a line per entry: <c>hash=</c> the hash of the request,
/// <c>input=</c> the last write time of an input file in ticks, <c>|</c> and its path, and <c>output=</c> the path of a
/// generated file. It is written last, so it only exists after a successful generation.
/// </remarks>
public sealed partial class LibrarySetBuild
{
    /// <summary>The name of the state file in the output directory.</summary>
    public const string StateFileName = "cscom.state";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    private readonly InteropPipeline _pipeline;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySetBuild"/> class.
    /// </summary>
    public LibrarySetBuild(InteropPipeline pipeline, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<LibrarySetBuild>();
    }

    /// <summary>
    /// Generates the code of the request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="requestHash">The hash of the request computed by the task, recorded in the state file.</param>
    /// <returns>The paths of the generated files.</returns>
    /// <exception cref="CsComException">The request, a reference or a configuration file is not valid.</exception>
    public IReadOnlyList<string> Run(BuildRequest request, string requestHash)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestHash);

        string output = Path.GetFullPath(request.OutputDirectory);
        string statePath = Path.Combine(output, StateFileName);
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }

        var options = CreateOptions(request);
        var references = request.References.Select(r => CreateReference(r, request.ProjectDirectory)).ToList();
        var result = new LibrarySetGenerator(_pipeline, _loggerFactory).Generate(references, options);
        var files = WriteDocuments(output, result.Documents);
        WriteState(statePath, requestHash, result.InputFiles, files);
        LogGenerated(files.Count, output);
        return files;
    }

    private LibrarySetOptions CreateOptions(BuildRequest request)
    {
        var supportCode = ParseEnum<SupportCodeMode>(request.SupportCode, "CsComSupportCode") ?? SupportCodeMode.Package;
        return new LibrarySetOptions
        {
            LanguageVersion = GetLanguageVersion(request.LanguageVersion, request.TargetFramework),
            SupportCode = supportCode,
            SupportNamespace = string.IsNullOrWhiteSpace(request.SupportNamespace) ? null : request.SupportNamespace.Trim(),
            Marshalling = new MarshallingOptions
            {
                Dates = ParseEnum<ValueMarshalling>(request.DateMarshalling, "CsComDateMarshalling") ?? ValueMarshalling.Bcl,
                Currencies = ParseEnum<ValueMarshalling>(request.CurrencyMarshalling, "CsComCurrencyMarshalling") ?? ValueMarshalling.Bcl,
                Booleans = ParseEnum<ValueMarshalling>(request.BoolMarshalling, "CsComBoolMarshalling") ?? ValueMarshalling.Bcl,
                Bstrs = ParseEnum<BstrMarshalling>(request.BstrMarshalling, "CsComBstrMarshalling") ?? BstrMarshalling.String,
                SafeArrays = ParseEnum<SafeArrayMarshalling>(request.SafeArrayMarshalling, "CsComSafeArrayMarshalling") ?? SafeArrayMarshalling.Array,
            },
            EventStyle = ParseEnum<EventStyle>(request.EventStyle, "CsComEventStyle") ?? EventStyle.Events,
            StructProperties = ParseEnum<StructProperties>(request.StructProperties, "CsComStructProperties") ?? StructProperties.Auto,
            OverloadsForDefaultValues = ParseBoolean(request.OverloadsForDefaultValues, "CsComOverloadsForDefaultValues") ?? false,
            DisableRuntimeMarshalling = ParseBoolean(request.DisableRuntimeMarshalling, "CsComDisableRuntimeMarshalling") ?? true,

            // The code of the libraries is not documented, and with CsCom.Runtime the interfaces derive from its IDispatch.
            SuppressedWarnings = supportCode == SupportCodeMode.Package ? ["CS1591", "SYSLIB1230"] : ["CS1591"],
            ToolVersion = string.IsNullOrEmpty(request.ToolVersion) ? ToolInfo.Version : request.ToolVersion,
        };
    }

    private int GetLanguageVersion(string languageVersion, string targetFramework)
    {
        int defaultVersion = CodeGenerationOptions.MinimumLanguageVersion;
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            if (!TargetFrameworks.TryGetMajorVersion(targetFramework.Trim(), out _, out string? error))
            {
                throw new CsComException(error);
            }

            defaultVersion = TargetFrameworks.GetDefaultLanguageVersion(targetFramework.Trim());

            if (!TargetFrameworks.IsWindowsSpecific(targetFramework.Trim()))
            {
                LogNotWindowsSpecific(targetFramework);
            }
        }

        string value = languageVersion.Trim();
        if (value.Length == 0 || value.Equals("default", StringComparison.OrdinalIgnoreCase) || value.Equals("latest", StringComparison.OrdinalIgnoreCase)
            || value.Equals("latestMajor", StringComparison.OrdinalIgnoreCase) || value.Equals("preview", StringComparison.OrdinalIgnoreCase))
        {
            return defaultVersion;
        }

        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number))
        {
            throw new CsComException($"'{languageVersion}' is not a valid C# version (LangVersion).");
        }

        int version = (int)number;
        return version >= CodeGenerationOptions.MinimumLanguageVersion
            ? version
            : throw new CsComException($"The generated code requires C# {CodeGenerationOptions.MinimumLanguageVersion} or later, but the project uses C# {value} (LangVersion).");
    }

    private static LibrarySetReference CreateReference(BuildReference reference, string projectDirectory)
    {
        string item = reference.Source;
        if (string.IsNullOrWhiteSpace(item))
        {
            throw new CsComException("A CsComReference item has an empty Include.");
        }

        // A relative path is relative to the project; anything else is a registered LIBID or ProgID.
        string source = item.Trim();
        string relative = Path.Combine(projectDirectory, source);
        if (TypeLibrarySource.IsFile(relative))
        {
            source = Path.GetFullPath(relative);
        }

        return new LibrarySetReference
        {
            Source = source,
            Namespace = string.IsNullOrWhiteSpace(reference.Namespace) ? null : reference.Namespace.Trim(),
            ReferencePaths = [.. reference.ReferencePath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => Path.GetFullPath(Path.Combine(projectDirectory, path)))],
            ConfigurationPath = string.IsNullOrWhiteSpace(reference.Config) ? null : Path.GetFullPath(Path.Combine(projectDirectory, reference.Config.Trim())),
            Marshalling = new MarshallingOverrides
            {
                Dates = ParseEnum<ValueMarshalling>(reference.DateMarshalling, "DateMarshalling", item),
                Currencies = ParseEnum<ValueMarshalling>(reference.CurrencyMarshalling, "CurrencyMarshalling", item),
                Booleans = ParseEnum<ValueMarshalling>(reference.BoolMarshalling, "BoolMarshalling", item),
                Bstrs = ParseEnum<BstrMarshalling>(reference.BstrMarshalling, "BstrMarshalling", item),
                SafeArrays = ParseEnum<SafeArrayMarshalling>(reference.SafeArrayMarshalling, "SafeArrayMarshalling", item),
            },
            EventStyle = ParseEnum<EventStyle>(reference.EventStyle, "EventStyle", item),
            StructProperties = ParseEnum<StructProperties>(reference.StructProperties, "StructProperties", item),
            OverloadsForDefaultValues = ParseBoolean(reference.OverloadsForDefaultValues, "OverloadsForDefaultValues", item),
        };
    }

    // The names of the values are the lower-case names of the enum members, as in the options of the command line.
    private static T? ParseEnum<T>(string value, string name, string? item = null)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse(value.Trim(), ignoreCase: true, out T result) && Enum.IsDefined(result) && !char.IsDigit(value.Trim()[0]))
        {
            return result;
        }

        string names = string.Join(", ", Enum.GetNames<T>().Select(static n => n.ToLowerInvariant()));
        throw new CsComException($"'{value}' is not a valid value of {Describe(name, item)}: one of {names} is expected.");
    }

    private static bool? ParseBoolean(string value, string name, string? item = null) =>
        string.IsNullOrWhiteSpace(value) ? null
            : bool.TryParse(value.Trim(), out bool result) ? result
            : throw new CsComException($"'{value}' is not a valid value of {Describe(name, item)}: true or false is expected.");

    private static string Describe(string name, string? item) => item is null ? name : $"the {name} metadata of the CsComReference '{item}'";

    // Only the files whose content changed are written, so that their timestamps stay the same for incremental builds.
    private static List<string> WriteDocuments(string output, IReadOnlyList<GeneratedDocument> documents)
    {
        var files = new List<string>(documents.Count);
        foreach (var document in documents)
        {
            string path = Path.GetFullPath(Path.Combine(output, document.RelativePath));
            string text = document.GetText();
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text, Utf8WithBom);
            }

            files.Add(path);
        }

        var generated = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(output, "*.cs", SearchOption.AllDirectories).Where(f => !generated.Contains(f)).ToList())
        {
            File.Delete(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(output, "*", SearchOption.AllDirectories).OrderByDescending(static d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }

        return files;
    }

    private static void WriteState(string path, string requestHash, IReadOnlyList<string> inputs, IReadOnlyList<string> outputs)
    {
        var builder = new StringBuilder();
        builder.Append("hash=").AppendLine(requestHash);
        foreach (string input in inputs)
        {
            builder.Append("input=").Append(File.GetLastWriteTimeUtc(input).Ticks.ToString(CultureInfo.InvariantCulture)).Append('|').AppendLine(input);
        }

        foreach (string output in outputs)
        {
            builder.Append("output=").AppendLine(output);
        }

        File.WriteAllText(path, builder.ToString());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated {Count} files in '{Directory}'")]
    private partial void LogGenerated(int count, string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The target framework '{TargetFramework}' is not Windows-specific; COM interop only works on Windows")]
    private partial void LogNotWindowsSpecific(string targetFramework);
}
