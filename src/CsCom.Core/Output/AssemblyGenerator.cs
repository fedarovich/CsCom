using System.Text.RegularExpressions;
using CsCom.CodeGeneration;
using CsCom.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Output;

/// <summary>
/// Builds ready-to-use interop assemblies, or NuGet packages of them, by generating a temporary project and building or packing
/// it with the .NET SDK.
/// </summary>
public sealed partial class AssemblyGenerator
{
    private const string IsolationProps = """
        <Project>
          <!-- Isolates the temporary project from the MSBuild files of the parent directories. -->
        </Project>

        """;

    private const string IsolationPackagesProps = """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
          </PropertyGroup>
        </Project>

        """;

    private static readonly string[] OutputExtensions = [".dll", ".pdb", ".xml"];

    private static readonly string[] PackageExtensions = [".nupkg", ".snupkg"];

    private readonly InteropPipeline _pipeline;
    private readonly ProjectGenerator _projectGenerator;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssemblyGenerator"/> class.
    /// </summary>
    public AssemblyGenerator(InteropPipeline pipeline, ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= NullLoggerFactory.Instance;
        _pipeline = pipeline;
        _projectGenerator = new ProjectGenerator(pipeline, loggerFactory);
        _logger = loggerFactory.CreateLogger<AssemblyGenerator>();
    }

    /// <summary>
    /// Generates and builds the interop assembly and copies it into the output directory.
    /// </summary>
    /// <returns>The paths of the copied files.</returns>
    public IReadOnlyList<string> BuildAssembly(BuildAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateProperties(request);

        // The CsCom.Runtime assembly is copied next to the interop assembly, so that both can be referenced directly.
        return RunInTemporaryProject(
            request,
            (_, manifest) => MergeProperties(
                manifest.SupportCode == SupportCodeMode.Package ? [new("CopyLocalLockFileAssemblies", "true")] : [],
                request),
            (manifest, temp, projectPath) =>
            {
                IReadOnlyList<string?> runtimes = manifest.RuntimeIdentifiers.Count > 0 ? [.. manifest.RuntimeIdentifiers] : [null];
                foreach (string? runtime in runtimes)
                {
                    RunDotNet("build", projectPath, temp, request.Configuration, runtime);
                }

                string output = Path.GetFullPath(request.OutputDirectory ?? Environment.CurrentDirectory);
                var files = CopyOutputs(temp, output, manifest, request.Configuration);
                LogAssemblyCreated(manifest.Name, output);
                return files;
            });
    }

    /// <summary>
    /// Generates and builds the interop assembly, packs it into a NuGet package and a symbols package, and copies them into
    /// the output directory.
    /// </summary>
    /// <remarks>
    /// The package is RID-neutral: it contains the assembly and its XML documentation in <c>lib/&lt;tfm&gt;</c> for each target
    /// framework, and the symbols package contains the PDBs. With <see cref="SupportCodeMode.Package"/>, the package depends on
    /// the CsCom.Runtime package.
    /// </remarks>
    /// <returns>The paths of the copied packages.</returns>
    public IReadOnlyList<string> PackAssembly(PackAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RuntimeIdentifiers.Count > 0)
        {
            throw new CsComException("The packages are RID-neutral; runtime identifiers are not supported.");
        }

        if (request.PackageVersion is { } version && !VersionPattern().IsMatch(version))
        {
            throw new CsComException($"'{version}' is not a valid package version, e.g. 1.2.3 or 1.2.3-preview.1 is expected.");
        }

        ValidateProperties(request);
        return RunInTemporaryProject(
            request,
            (library, _) => GetPackageProperties(request, library),
            (manifest, temp, projectPath) =>
            {
                string packages = Path.Combine(temp, "packages");
                RunDotNet("pack", projectPath, temp, request.Configuration, runtime: null, "--output", packages);

                string output = Path.GetFullPath(request.OutputDirectory ?? Environment.CurrentDirectory);
                Directory.CreateDirectory(output);
                var files = new List<string>();
                foreach (string file in Directory.EnumerateFiles(packages).Where(static f => PackageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
                {
                    string target = Path.Combine(output, Path.GetFileName(file));
                    File.Copy(file, target, overwrite: true);
                    files.Add(target);
                    LogPackageCreated(target);
                }

                return files;
            });
    }

    // The properties of the package.
    private static Dictionary<string, string> GetPackageProperties(PackAssemblyRequest request, TypeLibrary library)
    {
        string version = request.PackageVersion
            ?? (library.MajorVersion == 0 && library.MinorVersion == 0 ? "1.0.0" : $"{library.MajorVersion}.{library.MinorVersion}.0");
        string title = string.IsNullOrWhiteSpace(library.Documentation) ? library.Name : library.Documentation.Trim();
        return MergeProperties(
            [
                new("Version", version),
                new("Description", $"{title}: COM interop types generated by CsCom."),
                new("PackageTags", "COM;interop;CsCom"),
                new("IncludeSymbols", "true"),
                new("SymbolPackageFormat", "snupkg"),
            ],
            request);
    }

    private static void ValidateProperties(AssemblyRequest request)
    {
        if (request.Properties.Keys.FirstOrDefault(static n => !PropertyNamePattern().IsMatch(n)) is { } name)
        {
            throw new CsComException($"'{name}' is not a valid MSBuild property name.");
        }
    }

    // MSBuild property names are case-insensitive: the properties of the request override the defaults with the same name.
    private static Dictionary<string, string> MergeProperties(IEnumerable<KeyValuePair<string, string>> defaults, AssemblyRequest request)
    {
        var properties = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Properties)
        {
            properties[name] = value;
        }

        return properties;
    }

    // Generates the project in a temporary directory, runs the action and deletes the directory, unless the action failed or
    // the temporary files are kept.
    private IReadOnlyList<string> RunInTemporaryProject(
        AssemblyRequest request,
        Func<TypeLibrary, CsComManifest, IReadOnlyDictionary<string, string>?> getProperties,
        Func<CsComManifest, string, string, IReadOnlyList<string>> action)
    {
        var library = _pipeline.ReadTypeLibrary(request.Source, request.ReferencePaths);
        var manifest = _projectGenerator.ResolveSettings(request, library);
        var configuration = _projectGenerator.LoadConfiguration(manifest.Configuration);
        string temp = Path.Combine(Path.GetTempPath(), "cscom", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, "Directory.Build.props"), IsolationProps);
        File.WriteAllText(Path.Combine(temp, "Directory.Build.targets"), IsolationProps);
        File.WriteAllText(Path.Combine(temp, "Directory.Packages.props"), IsolationPackagesProps);
        string projectPath = _projectGenerator.WriteProject(
            temp, manifest, library, configuration, request.PackageSource, request.GlobalPackagesFolder, getProperties(library, manifest));

        bool succeeded = false;
        try
        {
            var files = action(manifest, temp, projectPath);
            succeeded = true;
            return files;
        }
        finally
        {
            if (succeeded && !request.KeepTemporaryFiles)
            {
                TryDelete(temp);
            }
            else
            {
                LogTemporaryProjectKept(temp);
            }
        }
    }

    // Runs 'dotnet build' or 'dotnet pack' on the temporary project.
    private void RunDotNet(string command, string projectPath, string workingDirectory, string configuration, string? runtime, params string[] extraArguments)
    {
        List<string> arguments = [command, projectPath, "--configuration", configuration, "-nodeReuse:false", "-p:UseSharedCompilation=false"];
        if (runtime is not null)
        {
            arguments.AddRange(["--runtime", runtime]);
        }

        arguments.AddRange(extraArguments);
        var (exitCode, output) = DotNetCli.Run(arguments, workingDirectory, _logger);
        if (exitCode != 0)
        {
            foreach (string line in output.Where(static l => ErrorPattern().IsMatch(l)).Distinct())
            {
                LogBuildError(line.Trim());
            }

            string action = command == "pack" ? "Packing" : "Building";
            throw new CsComException($"{action} the interop assembly failed (exit code {exitCode}). The project is kept in '{workingDirectory}'.");
        }
    }

    private List<string> CopyOutputs(string temp, string output, CsComManifest manifest, string configuration)
    {
        bool flat = manifest.TargetFrameworks.Count == 1 && manifest.RuntimeIdentifiers.Count <= 1;
        IReadOnlyList<string?> runtimes = manifest.RuntimeIdentifiers.Count > 0 ? [.. manifest.RuntimeIdentifiers] : [null];
        var files = new List<string>();
        foreach (string targetFramework in manifest.TargetFrameworks)
        {
            foreach (string? runtime in runtimes)
            {
                string source = Path.Combine(temp, "bin", configuration, targetFramework);
                string destination = flat ? output : Path.Combine(output, targetFramework);
                if (runtime is not null)
                {
                    source = Path.Combine(source, runtime);
                    destination = flat ? destination : Path.Combine(destination, runtime);
                }

                if (!Directory.Exists(source))
                {
                    throw new CsComException($"The build output '{source}' was not found. The project is kept in '{temp}'.");
                }

                Directory.CreateDirectory(destination);
                foreach (string file in Directory.EnumerateFiles(source).Where(static f => OutputExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
                {
                    string target = Path.Combine(destination, Path.GetFileName(file));
                    File.Copy(file, target, overwrite: true);
                    files.Add(target);
                    LogFileCopied(target);
                }
            }
        }

        return files;
    }

    private void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(directory, e.Message);
        }
    }

    [GeneratedRegex(@":\s*error\s+\w+\d+\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPattern();

    // A NuGet version: 2 to 4 numbers, with optional prerelease labels and build metadata.
    [GeneratedRegex(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyNamePattern();

    [LoggerMessage(Level = LogLevel.Error, Message = "{Line}")]
    private partial void LogBuildError(string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Name} in '{Directory}'")]
    private partial void LogAssemblyCreated(string name, string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Copied '{Path}'")]
    private partial void LogFileCopied(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the package '{Path}'")]
    private partial void LogPackageCreated(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "The temporary project is kept in '{Directory}'")]
    private partial void LogTemporaryProjectKept(string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete the temporary directory '{Directory}': {Reason}")]
    private partial void LogDeleteFailed(string directory, string reason);
}
