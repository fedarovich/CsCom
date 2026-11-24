using System.Text.Json;

namespace CsCom.LibrarySets;

/// <summary>
/// The request that the MSBuild task of CsCom.Build writes for the export of the type library of the assembly of a project:
/// the properties of the project as they are, which <see cref="TypeLibraryExportBuild"/> validates.
/// </summary>
public sealed class ExportRequest
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The version of the tool, which makes a new version export again.</summary>
    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>The directory of the project; relative paths are relative to it.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The assembly that the project compiled.</summary>
    public required string Assembly { get; init; }

    /// <summary>The type library to write.</summary>
    public required string Output { get; init; }

    /// <summary>The directory of the state file.</summary>
    public required string StateDirectory { get; init; }

    /// <summary>The name of the library (<c>CsComTypeLibraryName</c>).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The identifier of the library (<c>CsComTypeLibraryGuid</c>).</summary>
    public string Guid { get; init; } = string.Empty;

    /// <summary>The version of the library, <c>major.minor</c> (<c>CsComTypeLibraryVersion</c>).</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>The platform of the library, <c>win32</c> or <c>win64</c> (<c>CsComTypeLibraryPlatform</c>).</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>The assemblies that the project references.</summary>
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];

    /// <summary>The files of the type libraries of generated code (<c>CsComTypeLibraryReference</c>).</summary>
    public IReadOnlyList<string> TypeLibraries { get; init; } = [];

    /// <summary>
    /// The <c>CsComReference</c> items of the project, whose files that are type libraries are those of the code generated
    /// into the assembly.
    /// </summary>
    public IReadOnlyList<string> References { get; init; } = [];

    /// <summary>
    /// Reads a request file.
    /// </summary>
    /// <exception cref="CsComException">The file is not a valid request.</exception>
    public static ExportRequest Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ExportRequest>(stream, SerializerOptions) ?? throw new CsComException($"'{path}' is not a valid export request.");
        }
        catch (JsonException e)
        {
            throw new CsComException($"'{path}' is not a valid export request: {e.Message}", e);
        }
    }
}
