using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// The options shared by the commands that create a new project or assembly.
/// </summary>
public abstract class NewOutputRequest
{
    /// <summary>The path to the type library.</summary>
    public required string TypeLibraryPath { get; init; }

    /// <summary>The output directory.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>The project and assembly name; <c>Interop.&lt;Library&gt;</c> by default.</summary>
    public string? Name { get; init; }

    /// <summary>The namespace; <c>Interop.&lt;Library&gt;</c> by default.</summary>
    public string? Namespace { get; init; }

    /// <summary>The target frameworks; <see cref="TargetFrameworks.Default"/> when empty.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = [];

    /// <summary>The runtime identifiers; none (AnyCPU) when empty.</summary>
    public IReadOnlyList<string> RuntimeIdentifiers { get; init; } = [];

    /// <summary>The C# version; derived from the target frameworks by default.</summary>
    public int? LanguageVersion { get; init; }

    /// <summary>Whether all types are generated into a single file.</summary>
    public bool SingleFile { get; init; }

    /// <summary>Where the support code comes from.</summary>
    public SupportCodeMode SupportCode { get; init; } = SupportCodeMode.Package;

    /// <summary>How <c>DATE</c>, <c>CURRENCY</c> and <c>VARIANT_BOOL</c> values are represented.</summary>
    public MarshallingOptions Marshalling { get; init; } = new();

    /// <summary>The code generated for the event (source) interfaces.</summary>
    public EventStyle EventStyle { get; init; }

    /// <summary>Whether the members of the generated structures are public fields or properties.</summary>
    public StructProperties StructProperties { get; init; }

    /// <summary>An additional NuGet package source that provides the CsCom.Runtime package.</summary>
    public string? PackageSource { get; init; }

    /// <summary>The NuGet global packages folder used to restore the project, instead of the default one.</summary>
    public string? GlobalPackagesFolder { get; init; }
}

/// <summary>
/// The options of the <c>new project</c> command.
/// </summary>
public sealed class NewProjectRequest : NewOutputRequest
{
}

/// <summary>
/// The options of the <c>new assembly</c> command.
/// </summary>
public sealed class NewAssemblyRequest : NewOutputRequest
{
    /// <summary>Whether the temporary project is kept after the assembly is built.</summary>
    public bool KeepTemporaryFiles { get; init; }

    /// <summary>The build configuration.</summary>
    public string Configuration { get; init; } = "Release";
}

/// <summary>
/// The options of the <c>update project</c> command. The options that are not specified are taken from the project's <c>cscom.json</c>.
/// </summary>
public sealed class UpdateProjectRequest
{
    /// <summary>The path to the type library.</summary>
    public required string TypeLibraryPath { get; init; }

    /// <summary>The directory of the project; the current directory by default.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The new namespace.</summary>
    public string? Namespace { get; init; }

    /// <summary>The new C# version.</summary>
    public int? LanguageVersion { get; init; }

    /// <summary>Whether all types are generated into a single file.</summary>
    public bool? SingleFile { get; init; }

    /// <summary>The changes to the representation of <c>DATE</c>, <c>CURRENCY</c> and <c>VARIANT_BOOL</c> values.</summary>
    public MarshallingOverrides Marshalling { get; init; } = new();

    /// <summary>The new code generated for the event (source) interfaces.</summary>
    public EventStyle? EventStyle { get; init; }

    /// <summary>Whether the members of the generated structures are public fields or properties.</summary>
    public StructProperties? StructProperties { get; init; }
}

/// <summary>
/// Changes to <see cref="MarshallingOptions"/>; the options that are <see langword="null"/> are kept.
/// </summary>
public sealed record MarshallingOverrides
{
    /// <summary>The new representation of <c>DATE</c> parameters, return values and structure members.</summary>
    public ValueMarshalling? Dates { get; init; }

    /// <summary>The new representation of <c>CURRENCY</c> parameters, return values and structure members.</summary>
    public ValueMarshalling? Currencies { get; init; }

    /// <summary>The new representation of <c>VARIANT_BOOL</c> parameters, return values and structure members.</summary>
    public ValueMarshalling? Booleans { get; init; }

    /// <summary>The new representation of typed <c>SAFEARRAY(T)</c> parameters and return values.</summary>
    public SafeArrayMarshalling? SafeArrays { get; init; }

    /// <summary>Applies the changes to the options.</summary>
    public MarshallingOptions Apply(MarshallingOptions options) => options with
    {
        Dates = Dates ?? options.Dates,
        Currencies = Currencies ?? options.Currencies,
        Booleans = Booleans ?? options.Booleans,
        SafeArrays = SafeArrays ?? options.SafeArrays,
    };
}
