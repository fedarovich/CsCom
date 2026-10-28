using System.Text.RegularExpressions;
using CsCom.CodeGeneration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Output;

/// <summary>
/// Builds ready-to-use interop assemblies by generating a temporary project and building it with the .NET SDK.
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
    public IReadOnlyList<string> CreateAssembly(NewAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var library = _pipeline.ReadTypeLibrary(request.TypeLibraryPath);
        var manifest = _projectGenerator.ResolveSettings(request, library);
        var configuration = _projectGenerator.LoadConfiguration(manifest.Configuration);
        string output = Path.GetFullPath(request.OutputDirectory ?? Environment.CurrentDirectory);
        string temp = Path.Combine(Path.GetTempPath(), "cscom", Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(temp);
        File.WriteAllText(Path.Combine(temp, "Directory.Build.props"), IsolationProps);
        File.WriteAllText(Path.Combine(temp, "Directory.Build.targets"), IsolationProps);
        File.WriteAllText(Path.Combine(temp, "Directory.Packages.props"), IsolationPackagesProps);

        // The CsCom.Runtime assembly is copied next to the interop assembly, so that both can be referenced directly.
        var extraProperties = manifest.SupportCode == SupportCodeMode.Package
            ? new Dictionary<string, string> { ["CopyLocalLockFileAssemblies"] = "true" }
            : null;
        string projectPath = _projectGenerator.WriteProject(temp, manifest, library, configuration, request.PackageSource, request.GlobalPackagesFolder, extraProperties);

        bool succeeded = false;
        try
        {
            IReadOnlyList<string?> runtimes = manifest.RuntimeIdentifiers.Count > 0 ? [.. manifest.RuntimeIdentifiers] : [null];
            foreach (string? runtime in runtimes)
            {
                Build(projectPath, temp, request.Configuration, runtime);
            }

            var files = CopyOutputs(temp, output, manifest, request.Configuration);
            succeeded = true;
            LogAssemblyCreated(manifest.Name, output);
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

    private void Build(string projectPath, string workingDirectory, string configuration, string? runtime)
    {
        List<string> arguments = ["build", projectPath, "--configuration", configuration, "-nodeReuse:false", "-p:UseSharedCompilation=false"];
        if (runtime is not null)
        {
            arguments.AddRange(["--runtime", runtime]);
        }

        var (exitCode, output) = DotNetCli.Run(arguments, workingDirectory, _logger);
        if (exitCode != 0)
        {
            foreach (string line in output.Where(static l => ErrorPattern().IsMatch(l)).Distinct())
            {
                LogBuildError(line.Trim());
            }

            throw new CsComException($"Building the interop assembly failed (exit code {exitCode}). The project is kept in '{workingDirectory}'.");
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

    [LoggerMessage(Level = LogLevel.Error, Message = "{Line}")]
    private partial void LogBuildError(string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Name} in '{Directory}'")]
    private partial void LogAssemblyCreated(string name, string directory);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Copied '{Path}'")]
    private partial void LogFileCopied(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "The temporary project is kept in '{Directory}'")]
    private partial void LogTemporaryProjectKept(string directory);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete the temporary directory '{Directory}': {Reason}")]
    private partial void LogDeleteFailed(string directory, string reason);
}
