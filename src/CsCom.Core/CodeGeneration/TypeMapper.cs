using System.Runtime.InteropServices;
using CsCom.Model;

namespace CsCom.CodeGeneration;

/// <summary>
/// A managed type used in a generated signature.
/// </summary>
/// <param name="Name">The C# type, including the nullable annotation, e.g. <c>string?</c>.</param>
/// <param name="Marshalling">The marshalling attribute without brackets, if the type needs one.</param>
/// <param name="IsPointer">Whether the type is an unmanaged pointer, which requires an unsafe context.</param>
internal sealed record ManagedType(string Name, string? Marshalling = null, bool IsPointer = false)
{
    public static readonly ManagedType Void = new("void");

    public static readonly ManagedType IntPtr = new("nint");

    public bool IsVoid => Name == "void";

    public bool IsReference => Name.EndsWith('?');

}

/// <summary>
/// A managed type used by a late-bound dispinterface wrapper.
/// </summary>
/// <param name="Name">The C# type.</param>
/// <param name="FromObject">Converts an expression of type <see cref="object"/> returned by <c>IDispatch::Invoke</c> to the type.</param>
internal sealed record LateBoundType(string Name, Func<string, string> FromObject)
{
    public static readonly LateBoundType Void = new("void", static x => x);

    public static readonly LateBoundType Object = new("object?", static x => x);

    public bool IsVoid => Name == "void";
}

/// <summary>
/// A conversion between the COM type stored in a field of a structure and the .NET type of the property that exposes it.
/// </summary>
/// <param name="TypeName">The .NET type of the property.</param>
/// <param name="Get">Converts an expression of the COM type to the .NET type.</param>
/// <param name="Set">Converts an expression of the .NET type to the COM type.</param>
internal sealed record FieldConversion(string TypeName, Func<string, string> Get, Func<string, string> Set);

/// <summary>
/// Maps type library types to C# types.
/// </summary>
internal sealed class TypeMapper
{
    private const string Convert = "global::System.Convert";

    // Not defined by System.Runtime.InteropServices.VarEnum.
    private const VarEnum VT_INT_PTR = (VarEnum)37;
    private const VarEnum VT_UINT_PTR = (VarEnum)38;

    private readonly EmitContext _context;
    private readonly string _namespace;
    private readonly MarshallingOptions _marshalling;

    public TypeMapper(EmitContext context, string currentNamespace)
        : this(context, currentNamespace, context.Options.Marshalling)
    {
    }

    private TypeMapper(EmitContext context, string currentNamespace, MarshallingOptions marshalling)
    {
        _context = context;
        _namespace = currentNamespace;
        _marshalling = marshalling;
    }

    /// <summary>
    /// Gets a mapper that applies the changes of a member of the model to the marshalling options of the project.
    /// </summary>
    public TypeMapper WithMarshalling(MarshallingOverrides? overrides) =>
        overrides is null ? this : new TypeMapper(_context, _namespace, overrides.Apply(_context.Options.Marshalling));

    /// <summary>
    /// Whether the type is an interface or a class, so that a single pointer to it is passed as the interface pointer.
    /// </summary>
    public static bool IsInterfaceLike(TypeReference type) =>
        type is UserDefinedTypeReference { Definition: InterfaceDefinition or CoClassDefinition } or WellKnownTypeReference { Type: WellKnownType.IEnumVariant };

    /// <summary>
    /// Whether the type is a pointer to a value that is passed by reference (as opposed to an interface pointer).
    /// </summary>
    public static bool TryGetByRefElement(TypeReference type, out TypeReference element)
    {
        if (type is PointerTypeReference pointer && !IsInterfaceLike(pointer.ElementType))
        {
            element = pointer.ElementType;
            return true;
        }

        element = type;
        return false;
    }

    /// <summary>
    /// Maps a type passed by value in a source-generated COM or P/Invoke signature.
    /// </summary>
    public ManagedType MapValue(TypeReference type)
    {
        switch (type)
        {
            case PrimitiveTypeReference primitive:
                return MapPrimitive(primitive.VarType);
            case WellKnownTypeReference { Type: WellKnownType.Guid }:
                return new ManagedType("global::System.Guid");
            case WellKnownTypeReference { Type: WellKnownType.IEnumVariant }:
                return new ManagedType($"{_context.Support}.IEnumVARIANT?");
            case UserDefinedTypeReference { Definition: var definition }:
                return definition switch
                {
                    EnumDefinition or RecordDefinition => new ManagedType(_context.GetTypeName(definition, _namespace)),
                    AliasDefinition alias => MapValue(alias.Target),
                    InterfaceDefinition or CoClassDefinition => MapInterface(type),
                    _ => ManagedType.IntPtr,
                };
            case PointerTypeReference pointer when IsInterfaceLike(pointer.ElementType):
                return MapInterface(pointer.ElementType);
            case PointerTypeReference pointer:
                return MapPointer(pointer);
            case SafeArrayTypeReference array:
                return MapSafeArray(array);
            default:
                // C arrays decayed to pointers are passed as raw pointers.
                return ManagedType.IntPtr;
        }
    }

    /// <summary>
    /// Maps the type of a field of a structure, which must be blittable. Values converted to .NET types are stored as the COM
    /// type, see <see cref="GetFieldConversion"/>.
    /// </summary>
    public ManagedType MapField(TypeReference type)
    {
        switch (type)
        {
            case PrimitiveTypeReference { VarType: var vt }:
                return vt switch
                {
                    VarEnum.VT_BOOL => MapField(_marshalling.Booleans, "short", "ComBool"),
                    // A BSTR field is a pointer-sized wrapper that converts to string, unless the raw pointer is asked for.
                    VarEnum.VT_BSTR => _marshalling.Bstrs == BstrMarshalling.None ? ManagedType.IntPtr : new ManagedType($"{_context.Support}.ComBstrValue"),
                    VarEnum.VT_LPSTR or VarEnum.VT_LPWSTR or VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH => ManagedType.IntPtr,
                    VarEnum.VT_VARIANT => new ManagedType($"{Syntax.MarshallingNamespace}.ComVariant"),
                    VarEnum.VT_CY => MapField(_marshalling.Currencies, "long", "ComCurrency"),
                    VarEnum.VT_DATE => MapField(_marshalling.Dates, "double", "ComDate"),
                    _ => MapPrimitive(vt) is { Marshalling: null, IsReference: false } mapped ? mapped : ManagedType.IntPtr,
                };
            case WellKnownTypeReference { Type: WellKnownType.Guid }:
                return new ManagedType("global::System.Guid");
            case UserDefinedTypeReference { Definition: EnumDefinition or RecordDefinition } user:
                return new ManagedType(_context.GetTypeName(user.Definition, _namespace));
            case UserDefinedTypeReference { Definition: AliasDefinition alias }:
                return MapField(alias.Target);
            case PointerTypeReference pointer when !IsInterfaceLike(pointer.ElementType):
                return MapPointer(pointer);
            default:
                return ManagedType.IntPtr;
        }
    }

    /// <summary>
    /// Gets the conversion between the field of a structure, which has the COM type returned by <see cref="MapField"/>, and the
    /// .NET type of the property that exposes it, or <see langword="null"/> if the field is exposed as is.
    /// </summary>
    public FieldConversion? GetFieldConversion(TypeReference type)
    {
        var marshalling = _marshalling;
        return type switch
        {
            PrimitiveTypeReference { VarType: VarEnum.VT_BOOL } when marshalling.Booleans == ValueMarshalling.Bcl =>
                new FieldConversion("bool", static x => $"{x} != 0", static x => $"{x} ? (short)-1 : (short)0"),
            PrimitiveTypeReference { VarType: VarEnum.VT_CY } when marshalling.Currencies == ValueMarshalling.Bcl =>
                Marshaller("decimal", "CurrencyMarshaller"),
            PrimitiveTypeReference { VarType: VarEnum.VT_DATE } when marshalling.Dates == ValueMarshalling.Bcl =>
                Marshaller("global::System.DateTime", "DateMarshaller"),
            UserDefinedTypeReference { Definition: AliasDefinition alias } => GetFieldConversion(alias.Target),
            _ => null,
        };

        FieldConversion Marshaller(string typeName, string marshaller)
        {
            string name = $"{_context.Support}.{marshaller}";
            return new FieldConversion(typeName, x => $"{name}.ConvertToManaged({x})", x => $"{name}.ConvertToUnmanaged({x})");
        }
    }

    /// <summary>
    /// Maps a type used by a late-bound dispinterface wrapper, whose values are converted to and from <c>VARIANT</c>s.
    /// </summary>
    public LateBoundType MapLateBound(TypeReference type)
    {
        switch (type)
        {
            case PrimitiveTypeReference { VarType: var vt }:
                return MapLateBoundPrimitive(vt);
            case WellKnownTypeReference { Type: WellKnownType.Guid }:
                return new LateBoundType("global::System.Guid", static x => $"(global::System.Guid){x}!");
            case WellKnownTypeReference { Type: WellKnownType.IEnumVariant }:
            {
                string name = $"{_context.Support}.IEnumVARIANT";
                return new LateBoundType(name + "?", x => $"ToInterface<{name}>({x})");
            }

            case UserDefinedTypeReference { Definition: var definition }:
                switch (definition)
                {
                    case EnumDefinition:
                    {
                        string name = _context.GetTypeName(definition, _namespace);
                        return new LateBoundType(name, x => $"({name}){Convert}.ToInt32({x})");
                    }

                    case RecordDefinition:
                    {
                        string name = _context.GetTypeName(definition, _namespace);
                        return new LateBoundType(name, x => $"({name}){x}!");
                    }

                    case AliasDefinition alias:
                        return MapLateBound(alias.Target);
                    case InterfaceDefinition or CoClassDefinition:
                        return MapLateBoundInterface(type);
                    default:
                        return LateBoundType.Object;
                }

            case PointerTypeReference pointer when IsInterfaceLike(pointer.ElementType):
                return MapLateBoundInterface(pointer.ElementType);
            case SafeArrayTypeReference when _marshalling.SafeArrays != SafeArrayMarshalling.Array:
                // The values of late-bound calls are copies in VARIANTs, which can hold SAFEARRAYs of any rank.
                return new LateBoundType("global::System.Array?", static x => $"(global::System.Array?){x}");
            case SafeArrayTypeReference array:
            {
                LateBoundType element = MapLateBound(array.ElementType);
                bool isPrimitive = element.Name is "object?" or "string?" or "bool" or "sbyte" or "byte" or "short" or "ushort" or "int" or "uint"
                    or "long" or "ulong" or "float" or "double" or "decimal" or "global::System.DateTime";
                if (array.Rank is 2 or 3 && (isPrimitive || array.ElementType is UserDefinedTypeReference { Definition: EnumDefinition }))
                {
                    // Multidimensional arrays are converted element by element, e.g. VT_I4 elements to an enum.
                    string method = array.Rank == 2 ? "ToArray2D" : "ToArray3D";
                    return new LateBoundType($"{element.Name}{RankSpecifier(array.Rank.Value)}?", x => $"{method}<{element.Name}>({x})");
                }

                if (isPrimitive)
                {
                    string name = $"{element.Name}[]?";
                    return new LateBoundType(name, x => $"({name}){x}");
                }

                return new LateBoundType("global::System.Array?", static x => $"(global::System.Array?){x}");
            }

            default:
                return LateBoundType.Object;
        }
    }

    private ManagedType MapPrimitive(VarEnum vt) => vt switch
    {
        VarEnum.VT_I1 => new("sbyte"),
        VarEnum.VT_UI1 => new("byte"),
        VarEnum.VT_I2 => new("short"),
        VarEnum.VT_UI2 => new("ushort"),
        VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_ERROR or VarEnum.VT_HRESULT => new("int"),
        VarEnum.VT_UI4 or VarEnum.VT_UINT => new("uint"),
        VarEnum.VT_I8 => new("long"),
        VarEnum.VT_UI8 => new("ulong"),
        VarEnum.VT_R4 => new("float"),
        VarEnum.VT_R8 => new("double"),
        VarEnum.VT_CY => MapParameter(
            _marshalling.Currencies,
            new("decimal", $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({_context.Support}.CurrencyMarshaller))"),
            "long",
            "ComCurrency"),
        VarEnum.VT_DATE => MapParameter(
            _marshalling.Dates,
            new("global::System.DateTime", $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({_context.Support}.DateMarshaller))"),
            "double",
            "ComDate"),
        VarEnum.VT_DECIMAL => new("decimal"),
        VT_INT_PTR => ManagedType.IntPtr,
        VT_UINT_PTR => new("nuint"),
        // String parameters are marshalled as BSTRs by the StringMarshalling of the interfaces and functions.
        VarEnum.VT_BSTR => _marshalling.Bstrs switch
        {
            BstrMarshalling.Wrapper => new($"{_context.Support}.ComBstr?"),
            BstrMarshalling.None => ManagedType.IntPtr,
            _ => new("string?"),
        },
        VarEnum.VT_LPSTR => new("string?", $"{Syntax.InteropNamespace}.MarshalAs({Syntax.InteropNamespace}.UnmanagedType.LPStr)"),
        VarEnum.VT_LPWSTR => new("string?", $"{Syntax.InteropNamespace}.MarshalAs({Syntax.InteropNamespace}.UnmanagedType.LPWStr)"),
        VarEnum.VT_BOOL => MapParameter(
            _marshalling.Booleans,
            new("bool", $"{Syntax.InteropNamespace}.MarshalAs({Syntax.InteropNamespace}.UnmanagedType.VariantBool)"),
            "short",
            "ComBool"),
        VarEnum.VT_VARIANT => new("object?", VariantMarshalling(optional: false)),
        VarEnum.VT_UNKNOWN => new("object?", UnknownMarshalling),
        VarEnum.VT_DISPATCH => new($"{_context.Support}.IDispatch?"),
        VarEnum.VT_VOID => ManagedType.Void,
        VarEnum.VT_FILETIME => new($"{Syntax.InteropNamespace}.ComTypes.FILETIME"),
        VarEnum.VT_CLSID => new("global::System.Guid"),
        _ => ManagedType.IntPtr,
    };

    private ManagedType MapParameter(ValueMarshalling marshalling, ManagedType managed, string comType, string wrapper) => marshalling switch
    {
        ValueMarshalling.None => new ManagedType(comType),
        ValueMarshalling.Wrapper => new ManagedType($"{_context.Support}.{wrapper}"),
        _ => managed,
    };

    private ManagedType MapField(ValueMarshalling marshalling, string comType, string wrapper) =>
        new(marshalling == ValueMarshalling.Wrapper ? $"{_context.Support}.{wrapper}" : comType);

    public string VariantMarshalling(bool optional) =>
        $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({_context.Support}.{(optional ? "OptionalVariantMarshaller" : "VariantMarshaller")}))";

    private static string UnknownMarshalling =>
        $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({Syntax.MarshallingNamespace}.ComInterfaceMarshaller<object>))";

    /// <summary>
    /// Maps a <c>SAFEARRAY</c> as selected by <see cref="MarshallingOptions.SafeArrays"/>: to a one-dimensional array marshalled
    /// by <c>SafeArrayMarshaller&lt;T&gt;</c>, a <see cref="System.Array"/>, a <c>ComSafeArray&lt;T&gt;</c> or the <c>SAFEARRAY*</c>.
    /// <c>SAFEARRAY</c>s whose elements cannot be marshalled (records, <c>CURRENCY</c>, dispinterfaces) are always passed as raw <c>SAFEARRAY*</c>.
    /// </summary>
    private ManagedType MapSafeArray(SafeArrayTypeReference array)
    {
        var marshalling = _marshalling.SafeArrays;
        string? element = array.ElementType is PrimitiveTypeReference { VarType: VarEnum.VT_UNKNOWN } ? "object?" : GetSafeArrayElement(array.ElementType);
        if (element is null || marshalling == SafeArrayMarshalling.None)
        {
            return ManagedType.IntPtr;
        }

        if (marshalling == SafeArrayMarshalling.Wrapper)
        {
            return new ManagedType($"{_context.Support}.ComSafeArray<{element}>?");
        }

        // Arrays of objects would become SAFEARRAYs of VARIANTs, so IUnknown pointers need their own marshaller.
        if (array.ElementType is PrimitiveTypeReference { VarType: VarEnum.VT_UNKNOWN })
        {
            return new ManagedType("object?[]?", $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({_context.Support}.UnknownSafeArrayMarshaller))");
        }

        if (marshalling == SafeArrayMarshalling.SysArray)
        {
            return new ManagedType("global::System.Array?", $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({_context.Support}.SafeArrayMarshaller))");
        }

        // The COM generator only supports generic marshallers of one-dimensional arrays, so the arrays of a known higher rank
        // use marshallers generated for their element types.
        if (array.Rank is 2 or 3)
        {
            string qualifiedElement = new TypeMapper(_context, string.Empty, _marshalling).GetSafeArrayElement(array.ElementType)!;
            string marshaller = _context.GetSafeArrayMarshaller(qualifiedElement, array.Rank.Value);
            return new ManagedType($"{element}{RankSpecifier(array.Rank.Value)}?", $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({marshaller}))");
        }

        string typeArgument = element.TrimEnd('?');
        return new ManagedType($"{element}[]?", $"{Syntax.MarshallingNamespace}.MarshalUsing(typeof({_context.Support}.SafeArrayMarshaller<{typeArgument}>))");
    }

    /// <summary>Gets the C# rank specifier of an array, e.g. <c>[,]</c>.</summary>
    public static string RankSpecifier(int rank) => $"[{new string(',', rank - 1)}]";

    private string? GetSafeArrayElement(TypeReference element)
    {
        switch (element)
        {
            case PrimitiveTypeReference { VarType: var vt }:
                return vt switch
                {
                    VarEnum.VT_VARIANT => "object?",
                    VarEnum.VT_BSTR => "string?",
                    VarEnum.VT_BOOL => "bool",
                    VarEnum.VT_DATE => "global::System.DateTime",
                    VarEnum.VT_DECIMAL => "decimal",
                    VarEnum.VT_DISPATCH => $"{_context.Support}.IDispatch?",
                    VarEnum.VT_I1 => "sbyte",
                    VarEnum.VT_UI1 => "byte",
                    VarEnum.VT_I2 => "short",
                    VarEnum.VT_UI2 => "ushort",
                    VarEnum.VT_I4 or VarEnum.VT_INT => "int",
                    VarEnum.VT_UI4 or VarEnum.VT_UINT => "uint",
                    VarEnum.VT_I8 => "long",
                    VarEnum.VT_UI8 => "ulong",
                    VarEnum.VT_R4 => "float",
                    VarEnum.VT_R8 => "double",
                    _ => null,
                };
            case UserDefinedTypeReference { Definition: EnumDefinition definition }:
                return _context.GetTypeName(definition, _namespace);
            case UserDefinedTypeReference { Definition: AliasDefinition alias }:
                return GetSafeArrayElement(alias.Target);
            case PointerTypeReference pointer when IsInterfaceLike(pointer.ElementType):
                return GetSafeArrayElement(pointer.ElementType);
            case UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: not InterfaceKind.Dispatch } definition }:
                return _context.GetTypeName(definition, _namespace) + "?";
            case UserDefinedTypeReference { Definition: CoClassDefinition coClass }:
                return coClass.DefaultInterface?.Interface is UserDefinedTypeReference defaultInterface ? GetSafeArrayElement(defaultInterface) : null;
            case WellKnownTypeReference { Type: WellKnownType.IEnumVariant }:
                return $"{_context.Support}.IEnumVARIANT?";
            default:
                // Records (VT_RECORD), CURRENCY, VT_ERROR and dispinterface wrappers are not supported.
                return null;
        }
    }

    private ManagedType MapInterface(TypeReference type)
    {
        switch (type)
        {
            case UserDefinedTypeReference { Definition: InterfaceDefinition definition }:
                return new ManagedType(_context.GetTypeName(definition, _namespace) + "?");
            case UserDefinedTypeReference { Definition: CoClassDefinition coClass }:
                return coClass.DefaultInterface?.Interface switch
                {
                    UserDefinedTypeReference defaultInterface => MapInterface(defaultInterface),
                    PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => MapPrimitive(VarEnum.VT_DISPATCH),
                    _ => MapPrimitive(VarEnum.VT_UNKNOWN),
                };
            case WellKnownTypeReference { Type: WellKnownType.IEnumVariant }:
                return new ManagedType($"{_context.Support}.IEnumVARIANT?");
            default:
                return MapPrimitive(VarEnum.VT_UNKNOWN);
        }
    }

    private ManagedType MapPointer(PointerTypeReference pointer)
    {
        ManagedType element = pointer.ElementType is PrimitiveTypeReference { VarType: VarEnum.VT_VOID }
            ? ManagedType.Void
            : MapField(pointer.ElementType);
        return new ManagedType(element.Name + "*", IsPointer: true);
    }

    private LateBoundType MapLateBoundInterface(TypeReference type)
    {
        switch (type)
        {
            case UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } definition }:
            {
                string name = _context.GetTypeName(definition, _namespace);
                return new LateBoundType(name + "?", x => $"ToWrapper<{name}>({x})");
            }

            case UserDefinedTypeReference { Definition: InterfaceDefinition definition }:
            {
                string name = _context.GetTypeName(definition, _namespace);
                return new LateBoundType(name + "?", x => $"ToInterface<{name}>({x})");
            }

            case UserDefinedTypeReference { Definition: CoClassDefinition coClass }:
                return coClass.DefaultInterface?.Interface is UserDefinedTypeReference defaultInterface
                    ? MapLateBoundInterface(defaultInterface)
                    : LateBoundType.Object;
            case WellKnownTypeReference { Type: WellKnownType.IEnumVariant }:
                return MapLateBound(type);
            default:
                return LateBoundType.Object;
        }
    }

    private static LateBoundType MapLateBoundPrimitive(VarEnum vt) => vt switch
    {
        VarEnum.VT_I1 => new("sbyte", static x => $"{Convert}.ToSByte({x})"),
        VarEnum.VT_UI1 => new("byte", static x => $"{Convert}.ToByte({x})"),
        VarEnum.VT_I2 => new("short", static x => $"{Convert}.ToInt16({x})"),
        VarEnum.VT_UI2 => new("ushort", static x => $"{Convert}.ToUInt16({x})"),
        VarEnum.VT_I4 or VarEnum.VT_INT or VarEnum.VT_ERROR or VarEnum.VT_HRESULT => new("int", static x => $"{Convert}.ToInt32({x})"),
        VarEnum.VT_UI4 or VarEnum.VT_UINT => new("uint", static x => $"{Convert}.ToUInt32({x})"),
        VarEnum.VT_I8 => new("long", static x => $"{Convert}.ToInt64({x})"),
        VarEnum.VT_UI8 => new("ulong", static x => $"{Convert}.ToUInt64({x})"),
        VarEnum.VT_R4 => new("float", static x => $"{Convert}.ToSingle({x})"),
        VarEnum.VT_R8 => new("double", static x => $"{Convert}.ToDouble({x})"),
        VarEnum.VT_CY or VarEnum.VT_DECIMAL => new("decimal", static x => $"{Convert}.ToDecimal({x})"),
        VarEnum.VT_DATE => new("global::System.DateTime", static x => $"{Convert}.ToDateTime({x})"),
        VarEnum.VT_BOOL => new("bool", static x => $"{Convert}.ToBoolean({x})"),
        VarEnum.VT_BSTR or VarEnum.VT_LPSTR or VarEnum.VT_LPWSTR => new("string?", static x => $"(string?){x}"),
        VT_INT_PTR => new("nint", static x => $"(nint){Convert}.ToInt64({x})"),
        VT_UINT_PTR => new("nuint", static x => $"(nuint){Convert}.ToUInt64({x})"),
        VarEnum.VT_VOID => LateBoundType.Void,
        _ => LateBoundType.Object,
    };
}
