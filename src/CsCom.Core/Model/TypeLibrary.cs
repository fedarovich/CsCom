namespace CsCom.Model;

/// <summary>
/// A COM type library and all the types that have to be generated for it.
/// </summary>
public sealed class TypeLibrary
{
    /// <summary>The name of the library, e.g. <c>Scripting</c>.</summary>
    public required string Name { get; set; }

    /// <summary>The library identifier.</summary>
    public Guid Guid { get; init; }

    /// <summary>The major version.</summary>
    public ushort MajorVersion { get; init; }

    /// <summary>The minor version.</summary>
    public ushort MinorVersion { get; init; }

    /// <summary>The locale identifier.</summary>
    public int Lcid { get; init; }

    /// <summary>The help string.</summary>
    public string? Documentation { get; set; }

    /// <summary>The path the library was loaded from.</summary>
    public string? Path { get; init; }

    /// <summary>
    /// The types of the library followed by the types imported from other libraries
    /// (see <see cref="TypeDefinition.ImportedFrom"/>).
    /// </summary>
    public List<TypeDefinition> Types { get; } = [];

    /// <summary>The libraries some of the <see cref="Types"/> are imported from.</summary>
    public List<TypeLibraryReference> ImportedLibraries { get; } = [];
}

/// <summary>
/// Identifies a type library that types are imported from.
/// </summary>
/// <param name="Name">The name of the library.</param>
/// <param name="Guid">The library identifier.</param>
/// <param name="MajorVersion">The major version.</param>
/// <param name="MinorVersion">The minor version.</param>
public sealed record TypeLibraryReference(string Name, Guid Guid, ushort MajorVersion, ushort MinorVersion);
