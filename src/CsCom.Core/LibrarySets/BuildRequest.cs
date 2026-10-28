using System.Text.Json;

namespace CsCom.LibrarySets;

/// <summary>
/// The request of the CsCom.Build MSBuild task to generate the code of the <c>CsComReference</c> items of a project, which
/// the task writes as JSON for the generator, see <see cref="LibrarySetBuild"/>.
/// </summary>
/// <remarks>
/// The options are the values of the <c>CsCom*</c> MSBuild properties and of the item metadata as they are; empty values are
/// not specified. Relative paths are relative to <see cref="ProjectDirectory"/>.
/// </remarks>
public sealed class BuildRequest
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The version of the CsCom.Build package.</summary>
    public string ToolVersion { get; init; } = string.Empty;

    /// <summary>The directory of the project.</summary>
    public required string ProjectDirectory { get; init; }

    /// <summary>The directory of the generated files, e.g. <c>obj\Debug\net10.0-windows\CsCom</c>.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>The target framework of the project (<c>$(TargetFramework)</c>).</summary>
    public string TargetFramework { get; init; } = string.Empty;

    /// <summary>The C# version of the project (<c>$(LangVersion)</c>).</summary>
    public string LanguageVersion { get; init; } = string.Empty;

    /// <summary>Where the support code comes from (<c>$(CsComSupportCode)</c>).</summary>
    public string SupportCode { get; init; } = string.Empty;

    /// <summary>The namespace of the generated support code (<c>$(CsComSupportNamespace)</c>).</summary>
    public string SupportNamespace { get; init; } = string.Empty;

    /// <summary>Whether runtime marshalling is disabled for the assembly (<c>$(CsComDisableRuntimeMarshalling)</c>).</summary>
    public string DisableRuntimeMarshalling { get; init; } = string.Empty;

    /// <summary>The default event style (<c>$(CsComEventStyle)</c>).</summary>
    public string EventStyle { get; init; } = string.Empty;

    /// <summary>The default structure properties (<c>$(CsComStructProperties)</c>).</summary>
    public string StructProperties { get; init; } = string.Empty;

    /// <summary>Whether overloads are generated for default values by default (<c>$(CsComOverloadsForDefaultValues)</c>).</summary>
    public string OverloadsForDefaultValues { get; init; } = string.Empty;

    /// <summary>The default representation of <c>DATE</c> values (<c>$(CsComDateMarshalling)</c>).</summary>
    public string DateMarshalling { get; init; } = string.Empty;

    /// <summary>The default representation of <c>CURRENCY</c> values (<c>$(CsComCurrencyMarshalling)</c>).</summary>
    public string CurrencyMarshalling { get; init; } = string.Empty;

    /// <summary>The default representation of <c>VARIANT_BOOL</c> values (<c>$(CsComBoolMarshalling)</c>).</summary>
    public string BoolMarshalling { get; init; } = string.Empty;

    /// <summary>The default representation of <c>BSTR</c> values (<c>$(CsComBstrMarshalling)</c>).</summary>
    public string BstrMarshalling { get; init; } = string.Empty;

    /// <summary>The default representation of typed <c>SAFEARRAY</c>s (<c>$(CsComSafeArrayMarshalling)</c>).</summary>
    public string SafeArrayMarshalling { get; init; } = string.Empty;

    /// <summary>The <c>CsComReference</c> items.</summary>
    public IReadOnlyList<BuildReference> References { get; init; } = [];

    /// <summary>The <c>CsComLibrary</c> items: the namespaces and the configuration files of libraries by name.</summary>
    public IReadOnlyList<BuildLibrary> Libraries { get; init; } = [];

    /// <summary>
    /// Reads a request from a JSON file.
    /// </summary>
    /// <exception cref="CsComException">The file is not a valid request.</exception>
    public static BuildRequest Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<BuildRequest>(stream, SerializerOptions) ?? throw new CsComException($"'{path}' is not a valid build request.");
        }
        catch (JsonException e)
        {
            throw new CsComException($"'{path}' is not a valid build request: {e.Message}", e);
        }
    }
}

/// <summary>
/// A <c>CsComLibrary</c> item of a <see cref="BuildRequest"/>: the namespace and the configuration file of a library, whether it
/// is a reference or its types are only used by them.
/// </summary>
public sealed class BuildLibrary
{
    /// <summary>The item spec: the name of the library, e.g. <c>stdole</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The <c>Namespace</c> metadata.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The <c>Config</c> metadata: the configuration file.</summary>
    public string Config { get; init; } = string.Empty;
}

/// <summary>
/// A <c>CsComReference</c> item of a <see cref="BuildRequest"/>: its item spec and metadata.
/// </summary>
public sealed class BuildReference
{
    /// <summary>The item spec: a type library or interop assembly file, a registered LIBID or a ProgID.</summary>
    public required string Source { get; init; }

    /// <summary>The <c>Namespace</c> metadata.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The <c>ReferencePath</c> metadata: assemblies or directories separated by <c>;</c>.</summary>
    public string ReferencePath { get; init; } = string.Empty;

    /// <summary>The <c>Config</c> metadata: the configuration file.</summary>
    public string Config { get; init; } = string.Empty;

    /// <summary>The <c>EventStyle</c> metadata.</summary>
    public string EventStyle { get; init; } = string.Empty;

    /// <summary>The <c>StructProperties</c> metadata.</summary>
    public string StructProperties { get; init; } = string.Empty;

    /// <summary>The <c>OverloadsForDefaultValues</c> metadata.</summary>
    public string OverloadsForDefaultValues { get; init; } = string.Empty;

    /// <summary>The <c>DateMarshalling</c> metadata.</summary>
    public string DateMarshalling { get; init; } = string.Empty;

    /// <summary>The <c>CurrencyMarshalling</c> metadata.</summary>
    public string CurrencyMarshalling { get; init; } = string.Empty;

    /// <summary>The <c>BoolMarshalling</c> metadata.</summary>
    public string BoolMarshalling { get; init; } = string.Empty;

    /// <summary>The <c>BstrMarshalling</c> metadata.</summary>
    public string BstrMarshalling { get; init; } = string.Empty;

    /// <summary>The <c>SafeArrayMarshalling</c> metadata.</summary>
    public string SafeArrayMarshalling { get; init; } = string.Empty;
}
