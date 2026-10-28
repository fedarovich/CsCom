using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// The options shared by the <c>project create</c>, <c>assembly build</c> and <c>assembly pack</c> commands, which create a new
/// project or assembly.
/// </summary>
public abstract class OutputRequest
{
    /// <summary>
    /// The sources of the interop types: type library files, registered LIBIDs or ProgIDs, or .NET interop assemblies, see
    /// <see cref="TypeLibraries.TypeLibrarySource"/>. Each library is generated into its own namespace, and the types that
    /// they use from other libraries into the namespaces of these libraries.
    /// </summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>The output directory.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>
    /// The project and assembly name; <c>Interop.&lt;Library&gt;</c> by default with a single source, and required with
    /// several sources.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>The namespace of a single source; <c>Interop.&lt;Library&gt;</c> by default. See <see cref="Namespaces"/> for several sources.</summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// The namespaces of libraries by their names, e.g. <c>Excel</c> or <c>stdole</c>, whether they are sources or are only
    /// used by them; <c>Interop.&lt;Library&gt;</c> by default.
    /// </summary>
    public IReadOnlyDictionary<string, string> Namespaces { get; init; } = new Dictionary<string, string>();

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

    /// <summary>Whether overloads are generated for the default values that C# cannot express.</summary>
    public bool OverloadsForDefaultValues { get; init; }

    /// <summary>
    /// The configuration file with the transformers of a single source, relative to the current directory; none when
    /// <see langword="null"/>. See <see cref="ConfigurationPaths"/> for several sources.
    /// </summary>
    public string? ConfigurationPath { get; init; }

    /// <summary>
    /// The configuration files of libraries by their names, whether they are sources or are only used by them, relative to
    /// the current directory.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConfigurationPaths { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// For interop assemblies, the assemblies or directories that contain the assemblies they reference, relative to the
    /// current directory.
    /// </summary>
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];

    /// <summary>An additional NuGet package source that provides the CsCom.Runtime package.</summary>
    public string? PackageSource { get; init; }

    /// <summary>The NuGet global packages folder used to restore the project, instead of the default one.</summary>
    public string? GlobalPackagesFolder { get; init; }
}

/// <summary>
/// The options of the <c>project create</c> command.
/// </summary>
public sealed class CreateProjectRequest : OutputRequest
{
}

/// <summary>
/// The options shared by the <c>assembly build</c> and <c>assembly pack</c> commands, which build the assembly in a temporary project.
/// </summary>
public abstract class AssemblyRequest : OutputRequest
{
    /// <summary>Whether the temporary project is kept after the assembly is built.</summary>
    public bool KeepTemporaryFiles { get; init; }

    /// <summary>The build configuration.</summary>
    public string Configuration { get; init; } = "Release";

    /// <summary>
    /// Additional MSBuild properties of the temporary project, e.g. <c>Version</c>, <c>Authors</c> or
    /// <c>PackageLicenseExpression</c>; they override the properties that CsCom sets.
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The options of the <c>assembly build</c> command.
/// </summary>
public sealed class BuildAssemblyRequest : AssemblyRequest
{
}

/// <summary>
/// The options of the <c>assembly pack</c> command. The packages are RID-neutral, so <see cref="OutputRequest.RuntimeIdentifiers"/>
/// must be empty.
/// </summary>
public sealed class PackAssemblyRequest : AssemblyRequest
{
    /// <summary>
    /// The version of the package and the assembly; <c>&lt;major&gt;.&lt;minor&gt;.0</c> of the type library by default with a
    /// single source, and <c>1.0.0</c> with several sources.
    /// </summary>
    public string? PackageVersion { get; init; }
}

/// <summary>
/// The options of the <c>project update</c> command. The options that are not specified are taken from the project's <c>cscom.json</c>.
/// </summary>
public sealed class UpdateProjectRequest
{
    /// <summary>
    /// The new sources of the interop types: type library files, registered LIBIDs or ProgIDs, or .NET interop assemblies, see
    /// <see cref="TypeLibraries.TypeLibrarySource"/>; the sources recorded in <c>cscom.json</c> when <see langword="null"/> or empty.
    /// </summary>
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary>The directory of the project; the current directory by default.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>The new namespace of a single source.</summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// The changes to the namespaces of libraries by their names, which are merged into the recorded ones; an empty namespace
    /// removes the recorded one.
    /// </summary>
    public IReadOnlyDictionary<string, string> Namespaces { get; init; } = new Dictionary<string, string>();

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

    /// <summary>Whether overloads are generated for the default values that C# cannot express.</summary>
    public bool? OverloadsForDefaultValues { get; init; }

    /// <summary>
    /// The new configuration file with the transformers of a single source, relative to the current directory. The recorded
    /// files are kept when <see langword="null"/>, and all of them are removed when empty.
    /// </summary>
    public string? ConfigurationPath { get; init; }

    /// <summary>
    /// The changes to the configuration files of libraries by their names, relative to the current directory, which are merged
    /// into the recorded ones; an empty path removes the recorded one.
    /// </summary>
    public IReadOnlyDictionary<string, string> ConfigurationPaths { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The new paths of the assemblies referenced by an interop assembly, relative to the current directory. The recorded
    /// paths are kept when <see langword="null"/> and removed when empty.
    /// </summary>
    public IReadOnlyList<string>? ReferencePaths { get; init; }
}
