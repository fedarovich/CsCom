using System.Runtime.InteropServices;
using CsCom.CodeGeneration;

namespace CsCom.Model;

/// <summary>
/// A method of an interface or a function of a module (a <c>FUNCDESC</c>).
/// </summary>
public sealed class MethodDefinition
{
    /// <summary>The name of the method in the generated code.</summary>
    public required string Name { get; set; }

    /// <summary>The name of the method (or the property) in the type library.</summary>
    public required string OriginalName { get; init; }

    /// <summary>The member identifier, which is the dispatch identifier for dual interfaces and dispinterfaces.</summary>
    public int MemberId { get; init; }

    /// <summary>Whether the method is a regular method or a property accessor.</summary>
    public InvokeKind InvokeKind { get; init; }

    /// <summary>The calling convention.</summary>
    public CallingConvention CallingConvention { get; init; } = CallingConvention.StdCall;

    /// <summary>The vtable slot of the method, counting the slots of the base interfaces; -1 if the method has no vtable slot.</summary>
    public int VtableSlot { get; init; } = -1;

    /// <summary>The native return type, which is <c>VT_HRESULT</c> for most COM methods.</summary>
    public required TypeReference ReturnType { get; set; }

    /// <summary>
    /// Changes to the marshalling options for the native return value, if it is not an <c>HRESULT</c>; the <c>[retval]</c>
    /// parameter has its own <see cref="ParameterDefinition.Marshalling"/>.
    /// </summary>
    public MarshallingOverrides? ReturnMarshalling { get; set; }

    /// <summary>The parameters.</summary>
    public List<ParameterDefinition> Parameters { get; } = [];

    /// <summary>The flags of the method.</summary>
    public MemberFlags Flags { get; set; }

    /// <summary>The help string.</summary>
    public string? Documentation { get; set; }

    /// <summary>The name of the DLL that exports a module function.</summary>
    public string? DllName { get; set; }

    /// <summary>The name of the entry point of a module function.</summary>
    public string? EntryPoint { get; set; }

    /// <summary>The ordinal of the entry point of a module function, when it is exported by ordinal.</summary>
    public ushort? EntryPointOrdinal { get; set; }

    /// <summary>Whether the method is a property accessor.</summary>
    public bool IsProperty => InvokeKind != InvokeKind.Method;

    /// <summary>Whether the native method returns an <c>HRESULT</c>.</summary>
    public bool ReturnsHResult => ReturnType is PrimitiveTypeReference { VarType: VarEnum.VT_HRESULT };

    /// <summary>The <c>[retval]</c> parameter, if any.</summary>
    public ParameterDefinition? RetvalParameter =>
        Parameters.Count > 0 && Parameters[^1].Flags.HasFlag(ParameterFlags.Retval) ? Parameters[^1] : null;

    /// <inheritdoc/>
    public override string ToString() => $"{InvokeKind} {Name}";
}

/// <summary>
/// The kind of a <see cref="MethodDefinition"/> (<c>INVOKEKIND</c>).
/// </summary>
public enum InvokeKind
{
    /// <summary>A regular method.</summary>
    Method = 1,

    /// <summary>A property getter (<c>propget</c>).</summary>
    PropertyGet = 2,

    /// <summary>A property setter (<c>propput</c>).</summary>
    PropertyPut = 4,

    /// <summary>A by-reference property setter (<c>propputref</c>).</summary>
    PropertyPutRef = 8,
}

/// <summary>
/// The flags of methods and properties (<c>FUNCFLAGS</c>/<c>VARFLAGS</c>).
/// </summary>
[Flags]
public enum MemberFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The member should not be accessible from macro languages.</summary>
    Restricted = 0x1,

    /// <summary>The member returns an object that is a source of events.</summary>
    Source = 0x2,

    /// <summary>The member supports data binding.</summary>
    Bindable = 0x4,

    /// <summary>The member should not be displayed to the user, although it exists and is bindable.</summary>
    Hidden = 0x40,

    /// <summary>The member is the default member of a collection.</summary>
    DefaultCollectionElement = 0x100,

    /// <summary>The member is the default member for display in the user interface.</summary>
    UiDefault = 0x200,

    /// <summary>The member should not be displayed in a property browser.</summary>
    NonBrowsable = 0x400,

    /// <summary>The property is read-only (dispinterface variables only).</summary>
    ReadOnly = 0x10000,
}

/// <summary>
/// A parameter of a <see cref="MethodDefinition"/>.
/// </summary>
public sealed class ParameterDefinition
{
    /// <summary>The name of the parameter in the generated code.</summary>
    public required string Name { get; set; }

    /// <summary>The native type of the parameter.</summary>
    public required TypeReference Type { get; set; }

    /// <summary>The flags of the parameter.</summary>
    public ParameterFlags Flags { get; set; }

    /// <summary>The default value, when <see cref="ParameterFlags.HasDefault"/> is set.</summary>
    public object? DefaultValue { get; set; }

    /// <summary>Changes to the marshalling options for this parameter; the options of the project when <see langword="null"/>.</summary>
    public MarshallingOverrides? Marshalling { get; set; }

    /// <inheritdoc/>
    public override string ToString() => $"{Type} {Name}";
}

/// <summary>
/// The flags of a <see cref="ParameterDefinition"/> (<c>PARAMFLAGS</c>).
/// </summary>
[Flags]
public enum ParameterFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>An input parameter.</summary>
    In = 0x1,

    /// <summary>An output parameter.</summary>
    Out = 0x2,

    /// <summary>A locale identifier parameter.</summary>
    Lcid = 0x4,

    /// <summary>The return value of the member.</summary>
    Retval = 0x8,

    /// <summary>An optional parameter.</summary>
    Optional = 0x10,

    /// <summary>The parameter has a default value.</summary>
    HasDefault = 0x20,
}
