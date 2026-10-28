using System.Text.Json.Serialization;

namespace CsCom.CodeGeneration;

/// <summary>
/// How values of an OLE Automation type are represented in the parameters and return values of the generated COM interfaces
/// and module functions, and in the members of the generated structures.
/// </summary>
public enum ValueMarshalling
{
    /// <summary>
    /// The corresponding .NET type (<see cref="System.DateTime"/>, <see cref="decimal"/>, <see cref="bool"/>), converted by a marshaller.
    /// Structures store the COM type in a private field and convert it in a property, or with <see cref="StructProperties.Never"/>
    /// expose the COM type as a public field.
    /// </summary>
    Bcl,

    /// <summary>The COM type as is (<see cref="double"/>, <see cref="long"/>, <see cref="short"/>), without conversions.</summary>
    None,

    /// <summary>The CsCom.Runtime wrapper (<c>ComDate</c>, <c>ComCurrency</c>, <c>ComBool</c>), which converts implicitly to and from the .NET type.</summary>
    Wrapper,
}

/// <summary>
/// How <c>BSTR</c> parameters and return values are represented.
/// </summary>
public enum BstrMarshalling
{
    /// <summary>A <see cref="string"/>, converted by a marshaller.</summary>
    String,

    /// <summary>The CsCom.Runtime <c>ComBstr</c> class, which wraps the <c>BSTR</c> without copying it.</summary>
    Wrapper,

    /// <summary>The <c>BSTR</c> pointer as is (<see cref="nint"/>), without marshalling.</summary>
    None,
}

/// <summary>
/// How typed <c>SAFEARRAY(T)</c> parameters and return values are represented. Type libraries do not record the rank of
/// <c>SAFEARRAY</c>s, so only the representations other than <see cref="Array"/> support multidimensional arrays.
/// </summary>
public enum SafeArrayMarshalling
{
    /// <summary>A zero-based one-dimensional array (<c>T[]</c>), converted by a marshaller.</summary>
    Array,

    /// <summary>A zero-based <see cref="System.Array"/> of any supported rank, converted by a marshaller.</summary>
    [JsonStringEnumMemberName("sysarray")]
    SysArray,

    /// <summary>The CsCom.Runtime <c>ComSafeArray&lt;T&gt;</c> class, which wraps the <c>SAFEARRAY</c> and keeps its bounds.</summary>
    Wrapper,

    /// <summary>The <c>SAFEARRAY</c> pointer as is (<see cref="nint"/>), without marshalling.</summary>
    None,
}

/// <summary>
/// How <c>DATE</c>, <c>CURRENCY</c> and <c>VARIANT_BOOL</c> values, <c>BSTR</c>s and typed <c>SAFEARRAY</c>s are represented in the generated code.
/// Late-bound dispinterface wrappers always use the .NET types.
/// </summary>
public sealed record MarshallingOptions
{
    /// <summary>The representation of <c>DATE</c> parameters, return values and structure members.</summary>
    public ValueMarshalling Dates { get; init; }

    /// <summary>The representation of <c>CURRENCY</c> parameters, return values and structure members.</summary>
    public ValueMarshalling Currencies { get; init; }

    /// <summary>The representation of <c>VARIANT_BOOL</c> parameters, return values and structure members.</summary>
    public ValueMarshalling Booleans { get; init; }

    /// <summary>
    /// The representation of <c>BSTR</c> parameters and return values. Structure fields, the elements of <c>SAFEARRAY</c>s and
    /// <c>VARIANT</c>s, and late-bound dispinterface wrappers are not affected.
    /// </summary>
    public BstrMarshalling Bstrs { get; init; }

    /// <summary>
    /// The representation of typed <c>SAFEARRAY(T)</c> parameters and return values. Late-bound dispinterface wrappers use
    /// <c>T[]</c> with <see cref="SafeArrayMarshalling.Array"/> and <see cref="System.Array"/> otherwise.
    /// </summary>
    public SafeArrayMarshalling SafeArrays { get; init; }
}

/// <summary>
/// Changes to <see cref="MarshallingOptions"/>; the options that are <see langword="null"/> are kept. They change the options
/// of a project, or of a member of the model (see <c>ParameterDefinition.Marshalling</c>).
/// </summary>
public sealed record MarshallingOverrides
{
    /// <summary>The new representation of <c>DATE</c> parameters, return values and structure members.</summary>
    public ValueMarshalling? Dates { get; init; }

    /// <summary>The new representation of <c>CURRENCY</c> parameters, return values and structure members.</summary>
    public ValueMarshalling? Currencies { get; init; }

    /// <summary>The new representation of <c>VARIANT_BOOL</c> parameters, return values and structure members.</summary>
    public ValueMarshalling? Booleans { get; init; }

    /// <summary>The new representation of <c>BSTR</c> parameters and return values.</summary>
    public BstrMarshalling? Bstrs { get; init; }

    /// <summary>The new representation of typed <c>SAFEARRAY(T)</c> parameters and return values.</summary>
    public SafeArrayMarshalling? SafeArrays { get; init; }

    /// <summary>Applies the changes to the options.</summary>
    public MarshallingOptions Apply(MarshallingOptions options) => options with
    {
        Dates = Dates ?? options.Dates,
        Currencies = Currencies ?? options.Currencies,
        Booleans = Booleans ?? options.Booleans,
        Bstrs = Bstrs ?? options.Bstrs,
        SafeArrays = SafeArrays ?? options.SafeArrays,
    };

    /// <summary>Combines two sets of changes; the changes of <paramref name="second"/> win.</summary>
    public static MarshallingOverrides Combine(MarshallingOverrides? first, MarshallingOverrides second) => first is null ? second : new()
    {
        Dates = second.Dates ?? first.Dates,
        Currencies = second.Currencies ?? first.Currencies,
        Booleans = second.Booleans ?? first.Booleans,
        Bstrs = second.Bstrs ?? first.Bstrs,
        SafeArrays = second.SafeArrays ?? first.SafeArrays,
    };
}
