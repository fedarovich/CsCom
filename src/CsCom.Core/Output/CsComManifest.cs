using System.Text.Json;
using System.Text.Json.Serialization;
using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// The <c>cscom.json</c> file that records how a project was generated, so that it can be updated later.
/// </summary>
public sealed record CsComManifest
{
    /// <summary>The name of the manifest file.</summary>
    public const string FileName = "cscom.json";

    /// <summary>The version of the tool that generated the project.</summary>
    public required string ToolVersion { get; init; }

    /// <summary>The path to the type library the project was generated from.</summary>
    public required string TypeLibrary { get; init; }

    /// <summary>The project and assembly name.</summary>
    public required string Name { get; init; }

    /// <summary>The namespace of the generated types.</summary>
    public required string Namespace { get; init; }

    /// <summary>The target frameworks.</summary>
    public required IReadOnlyList<string> TargetFrameworks { get; init; }

    /// <summary>The runtime identifiers.</summary>
    public IReadOnlyList<string> RuntimeIdentifiers { get; init; } = [];

    /// <summary>The C# version.</summary>
    public required int LanguageVersion { get; init; }

    /// <summary>Whether all types are generated into a single file.</summary>
    public bool SingleFile { get; init; }

    /// <summary>Where the support code comes from.</summary>
    [JsonConverter(typeof(CamelCaseEnumConverter<SupportCodeMode>))]
    public SupportCodeMode SupportCode { get; init; }

    /// <summary>
    /// Reads the manifest from the project directory.
    /// </summary>
    public static CsComManifest Read(string projectDirectory)
    {
        string path = Path.Combine(projectDirectory, FileName);
        if (!File.Exists(path))
        {
            throw new CsComException($"'{path}' was not found. Only the projects created with 'cscom new project' can be updated.");
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), CsComJsonContext.Default.CsComManifest)
                ?? throw new CsComException($"'{path}' is empty.");
        }
        catch (JsonException e)
        {
            throw new CsComException($"'{path}' is not valid: {e.Message}", e);
        }
    }

    /// <summary>
    /// Writes the manifest into the project directory.
    /// </summary>
    public void Write(string projectDirectory)
    {
        string json = JsonSerializer.Serialize(this, CsComJsonContext.Default.CsComManifest);
        File.WriteAllText(Path.Combine(projectDirectory, FileName), json.ReplaceLineEndings() + Environment.NewLine);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(CsComManifest))]
internal sealed partial class CsComJsonContext : JsonSerializerContext
{
}

internal sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum
{
}
