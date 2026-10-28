using CsCom.CodeGeneration;

namespace CsCom.Model;

/// <summary>
/// An enumeration (<c>TKIND_ENUM</c>).
/// </summary>
public sealed class EnumDefinition : TypeDefinition
{
    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Enum;

    /// <summary>The members of the enumeration.</summary>
    public List<EnumMember> Members { get; } = [];
}

/// <summary>
/// A member of an <see cref="EnumDefinition"/>.
/// </summary>
public sealed class EnumMember
{
    /// <summary>The name of the member in the generated code.</summary>
    public required string Name { get; set; }

    /// <summary>The value of the member.</summary>
    public required long Value { get; set; }

    /// <summary>The help string.</summary>
    public string? Documentation { get; set; }
}

/// <summary>
/// A structure (<c>TKIND_RECORD</c>) or a union (<c>TKIND_UNION</c>).
/// </summary>
public sealed class RecordDefinition : TypeDefinition
{
    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Record;

    /// <summary>Whether this is a union.</summary>
    public bool IsUnion { get; init; }

    /// <summary>The size of an instance, in bytes.</summary>
    public int Size { get; init; }

    /// <summary>The alignment of the fields, in bytes.</summary>
    public int Alignment { get; init; }

    /// <summary>The fields.</summary>
    public List<FieldDefinition> Fields { get; } = [];
}

/// <summary>
/// A field of a <see cref="RecordDefinition"/>.
/// </summary>
public sealed class FieldDefinition
{
    /// <summary>The name of the field in the generated code.</summary>
    public required string Name { get; set; }

    /// <summary>The type of the field.</summary>
    public required TypeReference Type { get; set; }

    /// <summary>The offset of the field, in bytes.</summary>
    public int Offset { get; init; }

    /// <summary>
    /// Changes to the marshalling options for this field; the options of the project when <see langword="null"/>. Only the
    /// representation of <c>DATE</c>, <c>CURRENCY</c> and <c>VARIANT_BOOL</c> values, and <see cref="BstrMarshalling.None"/>
    /// (raw <c>BSTR</c> pointers instead of <c>ComBstrValue</c>s), apply to fields.
    /// </summary>
    public MarshallingOverrides? Marshalling { get; set; }

    /// <summary>The help string.</summary>
    public string? Documentation { get; set; }
}

/// <summary>
/// A type alias (<c>TKIND_ALIAS</c>).
/// </summary>
public sealed class AliasDefinition : TypeDefinition
{
    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Alias;

    /// <summary>The aliased type.</summary>
    public required TypeReference Target { get; set; }
}

/// <summary>
/// How the members of an <see cref="InterfaceDefinition"/> are invoked.
/// </summary>
public enum InterfaceKind
{
    /// <summary>A vtable interface (<c>TKIND_INTERFACE</c>).</summary>
    Vtable,

    /// <summary>A dual interface that can be invoked both via the vtable and via <c>IDispatch</c>.</summary>
    Dual,

    /// <summary>A dispinterface that can only be invoked via <c>IDispatch::Invoke</c>.</summary>
    Dispatch,
}

/// <summary>
/// An interface (<c>TKIND_INTERFACE</c>) or a dispinterface (<c>TKIND_DISPATCH</c>).
/// </summary>
public sealed class InterfaceDefinition : TypeDefinition
{
    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Interface;

    /// <summary>How the members of the interface are invoked.</summary>
    public InterfaceKind InterfaceKind { get; set; }

    /// <summary>
    /// The base interface: another <see cref="UserDefinedTypeReference"/> interface, <c>VT_DISPATCH</c> for interfaces
    /// derived from <c>IDispatch</c>, or <see langword="null"/> for interfaces derived from <c>IUnknown</c>.
    /// </summary>
    public TypeReference? BaseInterface { get; set; }

    /// <summary>The methods, in vtable order (for vtable and dual interfaces).</summary>
    public List<MethodDefinition> Methods { get; } = [];

    /// <summary>The properties declared as variables of a dispinterface.</summary>
    public List<DispatchPropertyDefinition> Properties { get; } = [];

    /// <summary>The number of vtable slots occupied by the base interfaces (3 for <c>IUnknown</c>, 7 for <c>IDispatch</c>).</summary>
    public int BaseVtableSlots { get; set; }
}

/// <summary>
/// A property declared as a variable of a dispinterface.
/// </summary>
public sealed class DispatchPropertyDefinition
{
    /// <summary>The name of the property in the generated code.</summary>
    public required string Name { get; set; }

    /// <summary>The dispatch identifier.</summary>
    public int DispId { get; init; }

    /// <summary>The type of the property.</summary>
    public required TypeReference Type { get; set; }

    /// <summary>Whether the property is read-only.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>The flags of the property.</summary>
    public MemberFlags Flags { get; set; }

    /// <summary>
    /// Changes to the marshalling options for this property; the options of the project when <see langword="null"/>. Only the
    /// representation of <c>SAFEARRAY</c>s applies to the late-bound wrappers of dispinterfaces.
    /// </summary>
    public MarshallingOverrides? Marshalling { get; set; }

    /// <summary>The help string.</summary>
    public string? Documentation { get; set; }
}

/// <summary>
/// A coclass (<c>TKIND_COCLASS</c>).
/// </summary>
public sealed class CoClassDefinition : TypeDefinition
{
    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.CoClass;

    /// <summary>The interfaces implemented or sourced by the class.</summary>
    public List<CoClassInterface> Interfaces { get; } = [];

    /// <summary>The default (non-source) interface of the class, if any.</summary>
    public CoClassInterface? DefaultInterface =>
        Interfaces.FirstOrDefault(static i => i.IsDefault && !i.IsSource) ?? Interfaces.FirstOrDefault(static i => !i.IsSource);
}

/// <summary>
/// An interface implemented or sourced by a <see cref="CoClassDefinition"/>.
/// </summary>
/// <param name="Interface">The interface, a <see cref="UserDefinedTypeReference"/> or <c>VT_DISPATCH</c>/<c>VT_UNKNOWN</c>.</param>
/// <param name="IsDefault">Whether this is the default interface.</param>
/// <param name="IsSource">Whether this is an event (source) interface.</param>
/// <param name="IsRestricted">Whether the interface should not be used by programmers.</param>
public sealed record CoClassInterface(TypeReference Interface, bool IsDefault, bool IsSource, bool IsRestricted);

/// <summary>
/// A module (<c>TKIND_MODULE</c>) that contains static functions and constants.
/// </summary>
public sealed class ModuleDefinition : TypeDefinition
{
    /// <inheritdoc/>
    public override TypeKind Kind => TypeKind.Module;

    /// <summary>The functions exported by the DLL the module describes.</summary>
    public List<MethodDefinition> Functions { get; } = [];

    /// <summary>The constants.</summary>
    public List<ConstantDefinition> Constants { get; } = [];
}

/// <summary>
/// A constant declared in a <see cref="ModuleDefinition"/>.
/// </summary>
public sealed class ConstantDefinition
{
    /// <summary>The name of the constant in the generated code.</summary>
    public required string Name { get; set; }

    /// <summary>The type of the constant.</summary>
    public required TypeReference Type { get; set; }

    /// <summary>The value of the constant.</summary>
    public object? Value { get; set; }

    /// <summary>The help string.</summary>
    public string? Documentation { get; set; }
}
