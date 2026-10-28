using CsCom.CodeGeneration;

namespace CsCom.LibrarySets;

/// <summary>
/// A type library or an interop assembly generated into a project together with others, see <see cref="LibrarySetGenerator"/>.
/// The options that are <see langword="null"/> are taken from <see cref="LibrarySetOptions"/>.
/// </summary>
public sealed class LibrarySetReference
{
    /// <summary>
    /// The source of the interop types: a type library file, a registered LIBID or ProgID, or a .NET interop assembly, see
    /// <see cref="TypeLibraries.TypeLibrarySource"/>.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>The namespace of the types of the library; <c>Interop.&lt;Library&gt;</c> by default.</summary>
    public string? Namespace { get; init; }

    /// <summary>For interop assemblies, the assemblies or directories that contain the assemblies they reference.</summary>
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];

    /// <summary>The configuration file with the transformers of the library, if any.</summary>
    public string? ConfigurationPath { get; init; }

    /// <summary>The changes to the representation of <c>DATE</c>, <c>CURRENCY</c>, <c>VARIANT_BOOL</c>, <c>BSTR</c> and <c>SAFEARRAY</c> values.</summary>
    public MarshallingOverrides Marshalling { get; init; } = new();

    /// <summary>The code generated for the event (source) interfaces.</summary>
    public EventStyle? EventStyle { get; init; }

    /// <summary>Whether the members of the generated structures are public fields or properties.</summary>
    public StructProperties? StructProperties { get; init; }

    /// <summary>Whether overloads are generated for the default values that C# cannot express.</summary>
    public bool? OverloadsForDefaultValues { get; init; }
}
