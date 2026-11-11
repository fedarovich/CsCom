namespace CsCom.CodeGeneration;

/// <summary>
/// How the parameterized properties of interfaces other than the indexer get indexer syntax, e.g. <c>sheet.Range["A1"]</c>.
/// C# has no named indexers, so a proxy whose indexer calls the accessor methods is returned by a member named after the
/// property. The accessor methods (<c>get_X</c>, <c>set_X</c>) are generated in any case.
/// </summary>
/// <remarks>
/// A setter cannot be called on a struct that a property returns (CS1612), so the property returns a class. A value
/// <c>StructProperty</c> is reserved for the C# versions that allow setter calls on readonly structs that are not variables.
/// </remarks>
public enum NamedIndexers
{
    /// <summary>No proxies: the parameterized properties are only accessor methods.</summary>
    None,

    /// <summary>
    /// A property returns a sealed proxy class, e.g. <c>sheet.Range["A1"] = value</c>. The small allocation of each access is
    /// usually removed by the JIT, since the members are inlined.
    /// </summary>
    ClassProperty,

    /// <summary>A method returns a readonly record struct proxy, e.g. <c>sheet.Range()["A1"] = value</c>, without allocations.</summary>
    StructMethod,
}
