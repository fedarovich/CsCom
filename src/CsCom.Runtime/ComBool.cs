namespace CsCom.Runtime;

/// <summary>
/// An OLE Automation Boolean value (<c>VARIANT_BOOL</c>). It has the layout of <c>VARIANT_BOOL</c>, so it can be used in
/// blittable structures, and converts implicitly to and from <see cref="bool"/>.
/// </summary>
/// <remarks>
/// <c>VARIANT_TRUE</c> is -1 and <c>VARIANT_FALSE</c> is 0. Like in OLE Automation, any non-zero value is true,
/// so two values are equal when they are both true or both false. The Boolean operators <c>!</c>, <c>&amp;</c>,
/// <c>|</c> and <c>^</c> work on the truth values and return <c>VARIANT_TRUE</c> or <c>VARIANT_FALSE</c>;
/// <c>&amp;&amp;</c> and <c>||</c> short-circuit, returning the left operand as is when it decides the result.
/// </remarks>
/// <param name="Value">The <c>VARIANT_BOOL</c> value.</param>
public readonly record struct ComBool(short Value)
{
    /// <summary>Gets <c>VARIANT_TRUE</c> (-1).</summary>
    public static ComBool True { get; } = new(-1);

    /// <summary>Gets <c>VARIANT_FALSE</c> (0).</summary>
    public static ComBool False { get; } = new(0);

    /// <summary>Converts the value to a <see cref="bool"/>.</summary>
    public bool ToBoolean() => Value != 0;

    /// <summary>Converts a <see cref="ComBool"/> to a <see cref="bool"/>.</summary>
    public static implicit operator bool(ComBool value) => value.ToBoolean();

    /// <summary>Converts a <see cref="bool"/> to <c>VARIANT_TRUE</c> or <c>VARIANT_FALSE</c>.</summary>
    public static implicit operator ComBool(bool value) => value ? True : False;

    /// <summary>Returns whether the value is true; together with <c>&amp;</c> and <c>|</c> enables <c>&amp;&amp;</c> and <c>||</c>.</summary>
    public static bool operator true(ComBool value) => value.ToBoolean();

    /// <summary>Returns whether the value is false; together with <c>&amp;</c> and <c>|</c> enables <c>&amp;&amp;</c> and <c>||</c>.</summary>
    public static bool operator false(ComBool value) => !value.ToBoolean();

    /// <summary>Computes the logical negation of the value.</summary>
    public static ComBool operator !(ComBool value) => !value.ToBoolean();

    /// <summary>Computes the logical AND of the values.</summary>
    public static ComBool operator &(ComBool left, ComBool right) => left.ToBoolean() & right.ToBoolean();

    /// <summary>Computes the logical OR of the values.</summary>
    public static ComBool operator |(ComBool left, ComBool right) => left.ToBoolean() | right.ToBoolean();

    /// <summary>Computes the logical exclusive OR of the values.</summary>
    public static ComBool operator ^(ComBool left, ComBool right) => left.ToBoolean() ^ right.ToBoolean();

    /// <inheritdoc/>
    public bool Equals(ComBool other) => ToBoolean() == other.ToBoolean();

    /// <inheritdoc/>
    public override int GetHashCode() => ToBoolean().GetHashCode();

    /// <inheritdoc/>
    public override string ToString() => ToBoolean().ToString();
}
