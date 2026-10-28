namespace CsCom.Runtime;

/// <summary>
/// An OLE Automation currency value (<c>CURRENCY</c>). It has the layout of <c>CURRENCY</c>, so it can be used in blittable
/// structures, and converts implicitly to and from <see cref="decimal"/>.
/// </summary>
/// <param name="Value">The currency value: a 64-bit integer scaled by 10,000.</param>
public readonly record struct ComCurrency(long Value)
{
    /// <summary>Converts the currency value to a <see cref="decimal"/>.</summary>
    public decimal ToDecimal() => decimal.FromOACurrency(Value);

    /// <summary>Converts a <see cref="ComCurrency"/> to a <see cref="decimal"/>.</summary>
    public static implicit operator decimal(ComCurrency value) => value.ToDecimal();

    /// <summary>Converts a <see cref="decimal"/> to a <see cref="ComCurrency"/>.</summary>
    /// <exception cref="System.OverflowException">The value is out of the range of a currency value.</exception>
    public static implicit operator ComCurrency(decimal value) => new(decimal.ToOACurrency(value));

    /// <inheritdoc/>
    public override string ToString() => ToDecimal().ToString();
}
