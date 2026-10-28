using System.Runtime.InteropServices;

namespace CsCom.Model;

/// <summary>
/// A reference to a type used by a member of a type library (a <c>TYPEDESC</c>).
/// </summary>
public abstract record TypeReference;

/// <summary>
/// A built-in OLE Automation type such as <c>VT_I4</c>, <c>VT_BSTR</c> or <c>VT_VARIANT</c>.
/// </summary>
/// <param name="VarType">The variant type.</param>
public sealed record PrimitiveTypeReference(VarEnum VarType) : TypeReference
{
    /// <inheritdoc/>
    public override string ToString() => VarType.ToString();
}

/// <summary>
/// A pointer to another type (<c>VT_PTR</c>).
/// </summary>
/// <param name="ElementType">The type pointed to.</param>
public sealed record PointerTypeReference(TypeReference ElementType) : TypeReference
{
    /// <inheritdoc/>
    public override string ToString() => $"{ElementType}*";
}

/// <summary>
/// A <c>SAFEARRAY</c> of elements of another type (<c>VT_SAFEARRAY</c>).
/// </summary>
/// <param name="ElementType">The type of the elements.</param>
public sealed record SafeArrayTypeReference(TypeReference ElementType) : TypeReference
{
    /// <inheritdoc/>
    public override string ToString() => $"SAFEARRAY({ElementType})";
}

/// <summary>
/// A C-style fixed-size array (<c>VT_CARRAY</c>).
/// </summary>
/// <param name="ElementType">The type of the elements.</param>
/// <param name="Dimensions">The number of elements in each dimension.</param>
public sealed record FixedArrayTypeReference(TypeReference ElementType, IReadOnlyList<int> Dimensions) : TypeReference
{
    /// <summary>The total number of elements.</summary>
    public int Length => Dimensions.Aggregate(1, static (total, dimension) => total * dimension);

    /// <inheritdoc/>
    public bool Equals(FixedArrayTypeReference? other) =>
        other is not null && ElementType.Equals(other.ElementType) && Dimensions.SequenceEqual(other.Dimensions);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ElementType, Length);

    /// <inheritdoc/>
    public override string ToString() => $"{ElementType}[{string.Join("][", Dimensions)}]";
}

/// <summary>
/// A type defined in a type library (<c>VT_USERDEFINED</c>).
/// </summary>
/// <param name="Definition">The definition of the type.</param>
public sealed record UserDefinedTypeReference(TypeDefinition Definition) : TypeReference
{
    /// <inheritdoc/>
    public override string ToString() => Definition.Name;
}

/// <summary>
/// A type that has a dedicated .NET or CsCom.Runtime counterpart, such as <c>GUID</c> or <c>IEnumVARIANT</c>.
/// </summary>
/// <param name="Type">The well-known type.</param>
public sealed record WellKnownTypeReference(WellKnownType Type) : TypeReference
{
    /// <inheritdoc/>
    public override string ToString() => Type.ToString();
}

/// <summary>
/// The types that are mapped to dedicated .NET or CsCom.Runtime types.
/// </summary>
public enum WellKnownType
{
    /// <summary><c>GUID</c>, mapped to <see cref="System.Guid"/>.</summary>
    Guid,

    /// <summary><c>IEnumVARIANT</c>, mapped to <c>CsCom.Runtime.IEnumVARIANT</c>.</summary>
    IEnumVariant,
}
