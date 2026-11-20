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
/// How the interface pointers in the parameters and return values of the generated COM interfaces and module functions are
/// converted to managed objects. They are shared objects, unless <c>SetMarshalling</c> selects unique objects for a
/// member or a parameter.
/// </summary>
/// <remarks>
/// Both marshallers are types of CsCom.Runtime, which use the <c>StrategyBasedComWrappers</c> set with
/// <c>ComMarshalling.ComWrappers</c>, and the instance of .NET without it.
/// </remarks>
public enum InterfaceMarshalling
{
    /// <summary>
    /// With <c>ComObjectMarshaller&lt;T&gt;</c>: one managed object per COM object, which all the references to it share.
    /// </summary>
    Shared,

    /// <summary>
    /// With <c>UniqueComObjectMarshaller&lt;T&gt;</c>: a managed object of its own for every interface pointer, which
    /// belongs to its receiver and which <c>ComMarshalling.Release</c> releases at once. A pointer that is passed in and
    /// out (<c>ref</c>) stays a shared object, because it is passed back.
    /// </summary>
    Unique,
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
    /// The representation of <c>BSTR</c> parameters and return values. Structure fields are <c>ComBstrValue</c>s, except with
    /// <see cref="BstrMarshalling.None"/>, which keeps the pointers. The elements of <c>SAFEARRAY</c>s and <c>VARIANT</c>s, and
    /// late-bound dispinterface wrappers are not affected.
    /// </summary>
    public BstrMarshalling Bstrs { get; init; }

    /// <summary>
    /// The representation of typed <c>SAFEARRAY(T)</c> parameters and return values. Late-bound dispinterface wrappers use
    /// <c>T[]</c> with <see cref="SafeArrayMarshalling.Array"/> and <see cref="System.Array"/> otherwise.
    /// </summary>
    public SafeArrayMarshalling SafeArrays { get; init; }
}

/// <summary>
/// Changes to the <see cref="MarshallingOptions"/> of a project, of a library reference, or of a member of the model
/// (<see cref="MarshallingOverrides.Options"/>); the options that are <see langword="null"/> are kept. They cannot select
/// the marshaller of interface pointers, which only <see cref="MarshallingOverrides"/> of members can.
/// </summary>
public sealed record MarshallingOptionOverrides
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
    public static MarshallingOptionOverrides Combine(MarshallingOptionOverrides first, MarshallingOptionOverrides second) => new()
    {
        Dates = second.Dates ?? first.Dates,
        Currencies = second.Currencies ?? first.Currencies,
        Booleans = second.Booleans ?? first.Booleans,
        Bstrs = second.Bstrs ?? first.Bstrs,
        SafeArrays = second.SafeArrays ?? first.SafeArrays,
    };
}

/// <summary>
/// Changes to the marshalling of a member of the model (see <c>ParameterDefinition.Marshalling</c>), selected with
/// <c>SetMarshalling</c>: to the <see cref="MarshallingOptions"/>, and to the marshaller of its interface pointers. The
/// options that are <see langword="null"/> are kept.
/// </summary>
public sealed record MarshallingOverrides
{
    /// <summary>
    /// The changes to the <see cref="MarshallingOptions"/>, which a project and a library reference can make too; the
    /// options of a member are those of its project with these changes (<see cref="MarshallingOptionOverrides.Apply"/>).
    /// </summary>
    public MarshallingOptionOverrides Options { get; init; } = new();

    /// <summary>
    /// The new marshaller of the interface pointers in parameters and return values, which only members have, unlike the
    /// options of a project.
    /// </summary>
    public InterfaceMarshalling? Interfaces { get; init; }

    /// <summary>Combines two sets of changes; the changes of <paramref name="second"/> win.</summary>
    public static MarshallingOverrides Combine(MarshallingOverrides? first, MarshallingOverrides second) => first is null ? second : new()
    {
        Options = MarshallingOptionOverrides.Combine(first.Options, second.Options),
        Interfaces = second.Interfaces ?? first.Interfaces,
    };
}
