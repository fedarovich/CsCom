using CsCom.CodeGeneration;
using CsCom.Model;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Output;

/// <summary>
/// Creates and updates C# projects with the interop types of a type library.
/// </summary>
public sealed partial class ProjectGenerator
{
    private readonly InteropPipeline _pipeline;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectGenerator"/> class.
    /// </summary>
    public ProjectGenerator(InteropPipeline pipeline, ILoggerFactory? loggerFactory = null)
    {
        _pipeline = pipeline;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ProjectGenerator>();
    }

    /// <summary>
    /// Creates a new project.
    /// </summary>
    /// <returns>The path to the project file.</returns>
    public string CreateProject(NewProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var library = _pipeline.ReadTypeLibrary(request.TypeLibraryPath);
        var manifest = ResolveSettings(request, library);
        string directory = Path.GetFullPath(request.OutputDirectory ?? manifest.Name);
        if (Directory.Exists(directory)
            && (Directory.EnumerateFiles(directory, "*.csproj").Any() || File.Exists(Path.Combine(directory, CsComManifest.FileName))))
        {
            throw new CsComException($"The directory '{directory}' already contains a project. Use 'cscom update project' to update it.");
        }

        string projectPath = WriteProject(directory, manifest, library, request.PackageSource, request.GlobalPackagesFolder);
        LogProjectCreated(projectPath);
        return projectPath;
    }

    /// <summary>
    /// Regenerates the C# files of a project created with <see cref="CreateProject"/>.
    /// </summary>
    /// <returns>The path to the project directory.</returns>
    public string UpdateProject(UpdateProjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string directory = Path.GetFullPath(request.ProjectDirectory ?? Environment.CurrentDirectory);
        var manifest = CsComManifest.Read(directory);
        if (request.Namespace is not null)
        {
            ValidateNamespace(request.Namespace);
        }

        int languageVersion = ValidateLanguageVersion(request.LanguageVersion ?? manifest.LanguageVersion, manifest.TargetFrameworks);
        var library = _pipeline.ReadTypeLibrary(request.TypeLibraryPath);
        var updated = manifest with
        {
            ToolVersion = ToolInfo.Version,
            TypeLibrary = Path.GetFullPath(request.TypeLibraryPath),
            Namespace = request.Namespace ?? manifest.Namespace,
            LanguageVersion = languageVersion,
            SingleFile = request.SingleFile ?? manifest.SingleFile,
        };

        var documents = _pipeline.GenerateCode(library, CreateCodeGenerationOptions(updated));
        GeneratedFiles.Write(directory, documents);

        string? projectPath = FindProjectFile(directory, updated.Name);
        if (projectPath is null)
        {
            LogProjectFileNotFound(directory);
        }
        else if (ProjectFile.Update(projectPath, updated.LanguageVersion, ToolInfo.Version))
        {
            LogProjectFileUpdated(projectPath);
        }

        updated.Write(directory);
        LogProjectUpdated(directory, documents.Count);
        return directory;
    }

    /// <summary>
    /// Writes a project with the generated files into the directory.
    /// </summary>
    internal string WriteProject(
        string directory,
        CsComManifest manifest,
        TypeLibrary library,
        string? packageSource,
        string? globalPackagesFolder,
        IReadOnlyDictionary<string, string>? extraProperties = null)
    {
        Directory.CreateDirectory(directory);
        var documents = _pipeline.GenerateCode(library, CreateCodeGenerationOptions(manifest));
        GeneratedFiles.Write(directory, documents);
        LogFilesGenerated(documents.Count, Path.Combine(directory, GeneratedFiles.FolderName));

        string? runtimePackageVersion = ToolInfo.Version;
        if (manifest.SupportCode == SupportCodeMode.Package && ProjectFile.FindCentralPackageManagement(directory) is { } packagesProps)
        {
            runtimePackageVersion = null;
            LogCentralPackageManagement(packagesProps, ProjectFile.RuntimePackageId, ToolInfo.Version);
        }

        string projectPath = Path.Combine(directory, manifest.Name + ".csproj");
        File.WriteAllText(projectPath, ProjectFile.Create(manifest, runtimePackageVersion, extraProperties));
        if (packageSource is not null || globalPackagesFolder is not null)
        {
            File.WriteAllText(
                Path.Combine(directory, "nuget.config"),
                ProjectFile.CreateNuGetConfig(packageSource is null ? null : Path.GetFullPath(packageSource), globalPackagesFolder is null ? null : Path.GetFullPath(globalPackagesFolder)));
        }

        manifest.Write(directory);
        return projectPath;
    }

    /// <summary>
    /// Validates the options of a new project or assembly and applies the defaults.
    /// </summary>
    internal CsComManifest ResolveSettings(NewOutputRequest request, TypeLibrary library)
    {
        var targetFrameworks = request.TargetFrameworks.Count > 0
            ? request.TargetFrameworks.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [TargetFrameworks.Default];
        foreach (string targetFramework in targetFrameworks)
        {
            try
            {
                TargetFrameworks.GetMajorVersion(targetFramework);
            }
            catch (ArgumentException e)
            {
                throw new CsComException(e.Message, e);
            }

            if (!TargetFrameworks.IsWindowsSpecific(targetFramework))
            {
                LogNotWindowsSpecific(targetFramework);
            }
        }

        var runtimeIdentifiers = request.RuntimeIdentifiers
            .Where(static r => !string.Equals(r, "AnyCPU", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string runtimeIdentifier in runtimeIdentifiers.Where(static r => !r.StartsWith("win", StringComparison.OrdinalIgnoreCase)))
        {
            LogNotWindowsRuntime(runtimeIdentifier);
        }

        string defaultName = "Interop." + library.Name;
        string name = request.Name ?? defaultName;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new CsComException($"'{name}' is not a valid project name.");
        }

        string @namespace = request.Namespace ?? defaultName;
        ValidateNamespace(@namespace);

        return new CsComManifest
        {
            ToolVersion = ToolInfo.Version,
            TypeLibrary = Path.GetFullPath(request.TypeLibraryPath),
            Name = name,
            Namespace = @namespace,
            TargetFrameworks = targetFrameworks,
            RuntimeIdentifiers = runtimeIdentifiers,
            LanguageVersion = ValidateLanguageVersion(request.LanguageVersion, targetFrameworks),
            SingleFile = request.SingleFile,
            SupportCode = request.SupportCode,
        };
    }

    internal static CodeGenerationOptions CreateCodeGenerationOptions(CsComManifest manifest) => new()
    {
        Namespace = manifest.Namespace,
        LanguageVersion = manifest.LanguageVersion,
        SingleFile = manifest.SingleFile,
        SingleFileName = manifest.Name + ".cs",
        SupportCode = manifest.SupportCode,
    };

    private static int ValidateLanguageVersion(int? languageVersion, IReadOnlyList<string> targetFrameworks)
    {
        int minimum = TargetFrameworks.GetDefaultLanguageVersion(targetFrameworks);
        if (languageVersion is null)
        {
            return minimum;
        }

        if (languageVersion < minimum)
        {
            throw new CsComException(
                $"C# {languageVersion} is not supported for {string.Join(", ", targetFrameworks)}: C# {minimum} or later is required.");
        }

        return languageVersion.Value;
    }

    private static void ValidateNamespace(string @namespace)
    {
        if (string.IsNullOrWhiteSpace(@namespace) || !@namespace.Split('.').All(static part => SyntaxFacts.IsValidIdentifier(part) && SyntaxFacts.GetKeywordKind(part) == SyntaxKind.None))
        {
            throw new CsComException($"'{@namespace}' is not a valid namespace.");
        }
    }

    private static string? FindProjectFile(string directory, string name)
    {
        string path = Path.Combine(directory, name + ".csproj");
        if (File.Exists(path))
        {
            return path;
        }

        var projects = Directory.GetFiles(directory, "*.csproj");
        return projects.Length == 1 ? projects[0] : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated {Count} files in '{Directory}'")]
    private partial void LogFilesGenerated(int count, string directory);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the project '{Path}'")]
    private partial void LogProjectCreated(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated the project '{Directory}' with {Count} generated files")]
    private partial void LogProjectUpdated(string directory, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated the project file '{Path}'")]
    private partial void LogProjectFileUpdated(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No project file was found in '{Directory}'; only the generated files were updated")]
    private partial void LogProjectFileNotFound(string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The project uses central package management ({Path}): add <PackageVersion Include=\"{Package}\" Version=\"{Version}\" /> to it")]
    private partial void LogCentralPackageManagement(string path, string package, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The target framework '{TargetFramework}' is not Windows-specific; COM interop only works on Windows")]
    private partial void LogNotWindowsSpecific(string targetFramework);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The runtime identifier '{RuntimeIdentifier}' is not a Windows runtime; COM interop only works on Windows")]
    private partial void LogNotWindowsRuntime(string runtimeIdentifier);
}
