using System.Text.RegularExpressions;
using CsCom.Model;

namespace CsCom.Transformations;

/// <summary>
/// Matches the names of the model, either by a regular expression or exactly.
/// </summary>
public sealed class NameSelector
{
    private NameSelector(Regex? pattern, string? name)
    {
        Pattern = pattern;
        Name = name;
    }

    /// <summary>Gets the regular expression, or <see langword="null"/> if the selector matches an exact name.</summary>
    public Regex? Pattern { get; }

    /// <summary>Gets the exact name, or <see langword="null"/> if the selector matches a regular expression.</summary>
    public string? Name { get; }

    /// <summary>
    /// Creates a selector that matches the names in which the regular expression finds a match, like <see cref="Regex.IsMatch(string)"/>.
    /// </summary>
    public static NameSelector FromPattern(Regex pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return new NameSelector(pattern, null);
    }

    /// <summary>Creates a selector that matches a name exactly (case-sensitive).</summary>
    public static NameSelector FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new NameSelector(null, name);
    }

    /// <summary>Creates a selector from a regular expression.</summary>
    public static implicit operator NameSelector(Regex pattern) => FromPattern(pattern);

    /// <summary>Whether the selector matches the name.</summary>
    public bool IsMatch(string name) => Pattern?.IsMatch(name) ?? string.Equals(name, Name, StringComparison.Ordinal);

    /// <summary>
    /// Gets the new name of a matching name: the matches of the regular expression replaced like
    /// <see cref="Regex.Replace(string, string)"/>, which supports substitutions such as <c>$1</c>, or the new name as is for
    /// an exact name.
    /// </summary>
    public string Replace(string name, string newName) => Pattern?.Replace(name, newName) ?? newName;

    /// <inheritdoc/>
    public override string ToString() => Pattern?.ToString() ?? Name!;
}

/// <summary>
/// The kinds of types that a <see cref="TypeSelector"/> matches; the names are those of <c>cscom list</c>.
/// </summary>
[Flags]
public enum TypeKinds
{
    /// <summary>No types.</summary>
    None = 0,

    /// <summary>Enums.</summary>
    Enum = 1,

    /// <summary>Structures.</summary>
    Struct = 2,

    /// <summary>Unions.</summary>
    Union = 4,

    /// <summary>Vtable and dual interfaces.</summary>
    Interface = 8,

    /// <summary>Dispinterfaces.</summary>
    DispInterface = 16,

    /// <summary>Coclasses.</summary>
    CoClass = 32,

    /// <summary>Modules.</summary>
    Module = 64,

    /// <summary>All types.</summary>
    All = Enum | Struct | Union | Interface | DispInterface | CoClass | Module,
}

/// <summary>
/// Matches types of the model by their names and their kinds.
/// </summary>
/// <param name="Name">Matches the names of the types; all names when <see langword="null"/>.</param>
/// <param name="Kinds">The kinds of the types.</param>
public sealed record TypeSelector(NameSelector? Name, TypeKinds Kinds = TypeKinds.All)
{
    /// <summary>Creates a selector that matches the types of all kinds by their names.</summary>
    public static implicit operator TypeSelector(Regex pattern) => new(NameSelector.FromPattern(pattern));

    /// <summary>Whether the selector matches the type.</summary>
    public bool IsMatch(TypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return (Kinds & KindOf(type)) != 0 && Name?.IsMatch(type.Name) != false;
    }

    /// <summary>Gets the kind of a type.</summary>
    public static TypeKinds KindOf(TypeDefinition type) => type switch
    {
        EnumDefinition => TypeKinds.Enum,
        RecordDefinition { IsUnion: true } => TypeKinds.Union,
        RecordDefinition => TypeKinds.Struct,
        InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } => TypeKinds.DispInterface,
        InterfaceDefinition => TypeKinds.Interface,
        CoClassDefinition => TypeKinds.CoClass,
        ModuleDefinition => TypeKinds.Module,
        _ => TypeKinds.None,
    };
}
