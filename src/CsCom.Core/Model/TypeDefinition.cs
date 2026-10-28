namespace CsCom.Model;

/// <summary>
/// The base class of the types defined in a type library.
/// </summary>
public abstract class TypeDefinition
{
    /// <summary>The name of the type in the generated code.</summary>
    public required string Name { get; set; }

    /// <summary>The name of the type in the type library.</summary>
    public required string OriginalName { get; init; }

    /// <summary>The identifier of the type, if any.</summary>
    public Guid? Guid { get; set; }

    /// <summary>The help string.</summary>
    public string? Documentation { get; set; }

    /// <summary>The type flags.</summary>
    public TypeFlags Flags { get; set; }

    /// <summary>The library the type is imported from, or <see langword="null"/> when it belongs to the library being processed.</summary>
    public TypeLibraryReference? ImportedFrom { get; init; }

    /// <summary>The kind of the type.</summary>
    public abstract TypeKind Kind { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>
/// The kind of a <see cref="TypeDefinition"/>.
/// </summary>
public enum TypeKind
{
    /// <summary>An enumeration.</summary>
    Enum,

    /// <summary>A structure or a union.</summary>
    Record,

    /// <summary>A type alias (typedef).</summary>
    Alias,

    /// <summary>An interface or a dispinterface.</summary>
    Interface,

    /// <summary>A coclass.</summary>
    CoClass,

    /// <summary>A module with static functions and constants.</summary>
    Module,
}

/// <summary>
/// The flags of a <see cref="TypeDefinition"/> (<c>TYPEFLAGS</c>).
/// </summary>
[Flags]
public enum TypeFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>A type description that describes an Application object.</summary>
    AppObject = 0x1,

    /// <summary>Instances of the type can be created by <c>ITypeInfo::CreateInstance</c>.</summary>
    CanCreate = 0x2,

    /// <summary>The type is licensed.</summary>
    Licensed = 0x4,

    /// <summary>The type is predefined.</summary>
    PreDeclId = 0x8,

    /// <summary>The type should not be displayed to browsers.</summary>
    Hidden = 0x10,

    /// <summary>The type is a control from which other types will be derived.</summary>
    Control = 0x20,

    /// <summary>The interface supplies both <c>IDispatch</c> and vtable binding.</summary>
    Dual = 0x40,

    /// <summary>The interface cannot add members at run time.</summary>
    NonExtensible = 0x80,

    /// <summary>The types used in the interface are fully compatible with Automation.</summary>
    OleAutomation = 0x100,

    /// <summary>Should not be accessible from macro languages.</summary>
    Restricted = 0x200,

    /// <summary>The class supports aggregation.</summary>
    Aggregatable = 0x400,

    /// <summary>The type has default behaviors.</summary>
    Replaceable = 0x800,

    /// <summary>The interface derives from <c>IDispatch</c>, either directly or indirectly.</summary>
    Dispatchable = 0x1000,

    /// <summary>The interface should use reverse binding.</summary>
    ReverseBind = 0x2000,

    /// <summary>Interfaces can be marshalled by a proxy.</summary>
    Proxy = 0x4000,
}
