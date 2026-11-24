using CsCom.InteropAssemblies;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.LibrarySets;

/// <summary>
/// Exports the type library of the assembly of a project for the CsCom.Build MSBuild task: validates the
/// <see cref="ExportRequest"/>, exports the library with <see cref="TypeLibraryExporter"/> and records the state that the
/// task uses to skip the export when nothing changed, in the format of <see cref="LibrarySetBuild"/>.
/// </summary>
public sealed class TypeLibraryExportBuild
{
    /// <summary>The name of the state file.</summary>
    public const string StateFileName = "cscom.typelib.state";

    /// <summary>The name of the file of the request, which the task writes next to the state file.</summary>
    public const string RequestFileName = "cscom.typelib.request.json";

    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeLibraryExportBuild"/> class.
    /// </summary>
    public TypeLibraryExportBuild(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// Exports the type library of the request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="requestHash">The hash of the request computed by the task, recorded in the state file.</param>
    /// <param name="warnings">
    /// The warnings of the export in the format of MSBuild, as <see cref="MSBuildOutput.LoggerProvider.Warnings"/> collects
    /// them; they are read after the export and recorded in the state file.
    /// </param>
    /// <returns>The path of the type library.</returns>
    /// <exception cref="CsComException">The request is not valid, or the type library cannot be exported.</exception>
    public string Run(ExportRequest request, string requestHash, IReadOnlyCollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requestHash);

        string stateDirectory = Path.GetFullPath(Path.Combine(request.ProjectDirectory, request.StateDirectory));
        Directory.CreateDirectory(stateDirectory);
        string statePath = Path.Combine(stateDirectory, StateFileName);
        File.Delete(statePath);

        // An input that changes after this point may have been read before the change.
        DateTime started = DateTime.UtcNow;
        var result = new TypeLibraryExporter(_loggerFactory).Export(new TypeLibraryExportRequest
        {
            AssemblyPath = Path.Combine(request.ProjectDirectory, request.Assembly),
            OutputPath = Path.Combine(request.ProjectDirectory, request.Output),
            Name = request.Name.Trim(),
            Guid = request.Guid.Trim(),
            Version = request.Version.Trim(),
            Platform = ParsePlatform(request.Platform),
            ReferencePaths = [.. request.ReferencePaths.Where(static p => p.Length > 0).Select(p => Path.GetFullPath(Path.Combine(request.ProjectDirectory, p)))],
            TypeLibraryPaths = [.. GetTypeLibraries(request)],
        });
        LibrarySetBuild.WriteState(statePath, requestHash, result.InputFiles, [result.Path], started, warnings ?? []);
        return result.Path;
    }

    private static TypeLibraryPlatform ParsePlatform(string platform) => platform.Trim().ToUpperInvariant() switch
    {
        "" or "WIN64" => TypeLibraryPlatform.Win64,
        "WIN32" => TypeLibraryPlatform.Win32,
        _ => throw new CsComException($"'{platform}' is not a valid value of CsComTypeLibraryPlatform: win32 or win64 is expected."),
    };

    // The type libraries of the CsComTypeLibraryReference items, and those that are files among the CsComReference items:
    // the others are registered, or interop assemblies, whose type libraries are registered if they have one.
    private static IEnumerable<string> GetTypeLibraries(ExportRequest request)
    {
        foreach (string item in request.TypeLibraries.Where(static p => !string.IsNullOrWhiteSpace(p)))
        {
            yield return Path.GetFullPath(Path.Combine(request.ProjectDirectory, item.Trim()));
        }

        foreach (string item in request.References.Where(static p => !string.IsNullOrWhiteSpace(p)))
        {
            string path;
            try
            {
                path = Path.GetFullPath(Path.Combine(request.ProjectDirectory, item.Trim()));
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (TypeLibrarySource.IsFile(path) && !InteropAssembly.IsAssembly(TypeLibrarySource.GetFilePath(path)))
            {
                yield return path;
            }
        }
    }
}
