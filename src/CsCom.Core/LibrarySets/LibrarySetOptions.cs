using CsCom.CodeGeneration;

namespace CsCom.LibrarySets;

/// <summary>
/// The options of the libraries generated together into a project, see <see cref="LibrarySetGenerator"/>. The code generation
/// options are the defaults of the references and the options of the libraries that are only used by them.
/// </summary>
public sealed class LibrarySetOptions
{
    /// <summary>The C# language version to generate the code for.</summary>
    public int LanguageVersion { get; init; } = CodeGenerationOptions.MinimumLanguageVersion;

    /// <summary>Where the support code comes from; it is generated once for all the libraries.</summary>
    public SupportCodeMode SupportCode { get; init; }

    /// <summary>The namespace of the generated support code; <c>Interop.ComSupport</c> by default.</summary>
    public string? SupportNamespace { get; init; }

    /// <summary>How <c>DATE</c>, <c>CURRENCY</c>, <c>VARIANT_BOOL</c>, <c>BSTR</c> and <c>SAFEARRAY</c> values are represented.</summary>
    public MarshallingOptions Marshalling { get; init; } = new();

    /// <summary>The code generated for the event (source) interfaces.</summary>
    public EventStyle EventStyle { get; init; }

    /// <summary>Whether the members of the generated structures are public fields or properties.</summary>
    public StructProperties StructProperties { get; init; }

    /// <summary>Whether overloads are generated for the default values that C# cannot express.</summary>
    public bool OverloadsForDefaultValues { get; init; }

    /// <summary>The warnings disabled in the generated files, see <see cref="CodeGenerationOptions.SuppressedWarnings"/>.</summary>
    public IReadOnlyList<string> SuppressedWarnings { get; init; } = [];

    /// <summary>
    /// Whether a file with <c>[assembly: DisableRuntimeMarshalling]</c> is generated, which source-generated COM interop
    /// requires to marshal <c>VARIANT</c> and <c>DECIMAL</c> values.
    /// </summary>
    public bool DisableRuntimeMarshalling { get; init; }

    /// <summary>The version of the tool written into the file headers.</summary>
    public string ToolVersion { get; init; } = ToolInfo.Version;
}
