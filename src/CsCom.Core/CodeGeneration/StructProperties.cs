namespace CsCom.CodeGeneration;

/// <summary>
/// Whether the members of the generated structures that are converted to .NET types are properties. The structures always have
/// the layout of the COM structures, so their fields have the COM types; properties can convert them to .NET types.
/// </summary>
public enum StructProperties
{
    /// <summary>
    /// Public fields, except for the members converted to .NET types (<c>DATE</c>, <c>CURRENCY</c> and <c>VARIANT_BOOL</c> with
    /// <see cref="ValueMarshalling.Bcl"/>), which are stored in private fields and exposed as properties.
    /// </summary>
    Auto,

    /// <summary>Public fields of the COM types; members whose .NET type has another layout keep the COM type.</summary>
    Never,
}
