using System;

namespace CsCom.Runtime;

/// <summary>
/// An OLE Automation date (<c>DATE</c>). It has the layout of <c>DATE</c>, so it can be used in blittable structures,
/// and converts implicitly to and from <see cref="DateTime"/>.
/// </summary>
/// <param name="Value">The OLE Automation date: the number of days since midnight, 30 December 1899.</param>
public readonly record struct ComDate(double Value)
{
    /// <summary>Converts the date to a <see cref="DateTime"/>.</summary>
    /// <exception cref="ArgumentException"><see cref="Value"/> is not a valid OLE Automation date.</exception>
    public DateTime ToDateTime() => DateTime.FromOADate(Value);

    /// <summary>Converts a <see cref="ComDate"/> to a <see cref="DateTime"/>.</summary>
    public static implicit operator DateTime(ComDate value) => value.ToDateTime();

    /// <summary>Converts a <see cref="DateTime"/> to a <see cref="ComDate"/>.</summary>
    public static implicit operator ComDate(DateTime value) => new(value.ToOADate());

    /// <inheritdoc/>
    public override string ToString() => ToDateTime().ToString();
}
