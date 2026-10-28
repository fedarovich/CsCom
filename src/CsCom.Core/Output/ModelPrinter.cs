using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CsCom.Model;

namespace CsCom.Output;

/// <summary>
/// Writes the types and members of the model with IDL-like signatures, e.g. to find the names to use in configuration files.
/// </summary>
public static class ModelPrinter
{
    private const string Indent = "    ";

    // Not defined by System.Runtime.InteropServices.VarEnum.
    private const VarEnum VT_INT_PTR = (VarEnum)37;
    private const VarEnum VT_UINT_PTR = (VarEnum)38;

    /// <summary>
    /// Writes the types of the library and their members.
    /// </summary>
    /// <param name="library">The model.</param>
    /// <param name="writer">The writer.</param>
    /// <param name="typeFilter">Matches the names of the types to write; all types when <see langword="null"/>.</param>
    public static void Write(TypeLibrary library, TextWriter writer, Regex? typeFilter = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(writer);

        foreach (var type in library.Types.Where(t => typeFilter?.IsMatch(t.Name) != false))
        {
            string imported = type.ImportedFrom is { } source ? $" ({source.Name})" : string.Empty;
            writer.WriteLine($"{Header(type)}{imported}");
            foreach (string member in Members(type))
            {
                writer.Write(Indent);
                writer.WriteLine(member);
            }
        }
    }

    private static string Header(TypeDefinition type) => type switch
    {
        EnumDefinition => $"enum {type.Name}",
        RecordDefinition { IsUnion: true } => $"union {type.Name}",
        RecordDefinition => $"struct {type.Name}",
        AliasDefinition alias => $"typedef {FormatType(alias.Target)} {type.Name}",
        InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } => $"dispinterface {type.Name}",
        InterfaceDefinition @interface => $"interface {type.Name} : {FormatBase(@interface.BaseInterface)}{(@interface.InterfaceKind == InterfaceKind.Dual ? " (dual)" : string.Empty)}",
        CoClassDefinition => $"coclass {type.Name}",
        ModuleDefinition => $"module {type.Name}",
        _ => $"{type.Kind} {type.Name}",
    };

    private static IEnumerable<string> Members(TypeDefinition type) => type switch
    {
        EnumDefinition @enum => @enum.Members.Select(static m => $"{m.Name} = {m.Value.ToString(CultureInfo.InvariantCulture)}"),
        RecordDefinition record => record.Fields.Select(static f => $"{FormatType(f.Type)} {f.Name}"),
        InterfaceDefinition @interface =>
        [
            .. @interface.Properties.Select(static p => $"property {(p.IsReadOnly ? "readonly " : string.Empty)}{FormatType(p.Type)} {p.Name}"),
            .. @interface.Methods.Select(Method),
        ],
        CoClassDefinition coClass => coClass.Interfaces.Select(static i => $"{Attributes(CoClassAttributes(i))}{FormatType(i.Interface)}"),
        ModuleDefinition module =>
        [
            .. module.Constants.Select(static c => $"const {FormatType(c.Type)} {c.Name} = {FormatValue(c.Value)}"),
            .. module.Functions.Select(Method),
        ],
        _ => [],
    };

    // E.g. "propget Name([out, retval] BSTR* name)"; the return type is omitted for HRESULT.
    private static string Method(MethodDefinition method)
    {
        string kind = method.InvokeKind switch
        {
            InvokeKind.PropertyGet => "propget",
            InvokeKind.PropertyPut => "propput",
            InvokeKind.PropertyPutRef => "propputref",
            _ => method.DllName is null ? "method" : "function",
        };

        string returnType = method.ReturnsHResult ? string.Empty : FormatType(method.ReturnType) + " ";
        return $"{kind} {returnType}{method.Name}({string.Join(", ", method.Parameters.Select(Parameter))})";
    }

    private static string Parameter(ParameterDefinition parameter)
    {
        var flags = new List<string>();
        AddIf(flags, parameter.Flags.HasFlag(ParameterFlags.In), "in");
        AddIf(flags, parameter.Flags.HasFlag(ParameterFlags.Out), "out");
        AddIf(flags, parameter.Flags.HasFlag(ParameterFlags.Lcid), "lcid");
        AddIf(flags, parameter.Flags.HasFlag(ParameterFlags.Retval), "retval");
        AddIf(flags, parameter.Flags.HasFlag(ParameterFlags.Optional), "optional");
        AddIf(flags, parameter.Flags.HasFlag(ParameterFlags.HasDefault), $"defaultvalue({FormatValue(parameter.DefaultValue)})");
        return $"{Attributes(flags)}{FormatType(parameter.Type)} {parameter.Name}";
    }

    private static List<string> CoClassAttributes(CoClassInterface @interface)
    {
        var flags = new List<string>();
        AddIf(flags, @interface.IsDefault, "default");
        AddIf(flags, @interface.IsSource, "source");
        AddIf(flags, @interface.IsRestricted, "restricted");
        return flags;
    }

    private static void AddIf(List<string> flags, bool condition, string flag)
    {
        if (condition)
        {
            flags.Add(flag);
        }
    }

    private static string Attributes(List<string> flags) => flags.Count == 0 ? string.Empty : $"[{string.Join(", ", flags)}] ";

    private static string FormatBase(TypeReference? baseInterface) => baseInterface switch
    {
        null => "IUnknown",
        PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => "IDispatch",
        _ => FormatType(baseInterface).TrimEnd('*'),
    };

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Formats a type with the IDL names of the OLE Automation types, e.g. <c>BSTR</c>, <c>long</c> or <c>SAFEARRAY(double)</c>.
    /// </summary>
    public static string FormatType(TypeReference type) => type switch
    {
        PrimitiveTypeReference primitive => FormatPrimitive(primitive.VarType),
        PointerTypeReference pointer => FormatType(pointer.ElementType) + "*",
        SafeArrayTypeReference array => $"SAFEARRAY({FormatType(array.ElementType)})",
        FixedArrayTypeReference array => $"{FormatType(array.ElementType)}[{string.Join("][", array.Dimensions)}]",
        UserDefinedTypeReference user => user.Definition.Name,
        WellKnownTypeReference { Type: WellKnownType.Guid } => "GUID",
        WellKnownTypeReference { Type: WellKnownType.IEnumVariant } => "IEnumVARIANT",
        _ => type.ToString(),
    };

    private static string FormatPrimitive(VarEnum vt) => vt switch
    {
        VarEnum.VT_I1 => "char",
        VarEnum.VT_UI1 => "unsigned char",
        VarEnum.VT_I2 => "short",
        VarEnum.VT_UI2 => "unsigned short",
        VarEnum.VT_I4 => "long",
        VarEnum.VT_UI4 => "unsigned long",
        VarEnum.VT_INT => "int",
        VarEnum.VT_UINT => "unsigned int",
        VarEnum.VT_I8 => "hyper",
        VarEnum.VT_UI8 => "unsigned hyper",
        VarEnum.VT_R4 => "float",
        VarEnum.VT_R8 => "double",
        VarEnum.VT_CY => "CURRENCY",
        VarEnum.VT_DATE => "DATE",
        VarEnum.VT_BSTR => "BSTR",
        VarEnum.VT_DISPATCH => "IDispatch*",
        VarEnum.VT_UNKNOWN => "IUnknown*",
        VarEnum.VT_ERROR => "SCODE",
        VarEnum.VT_BOOL => "VARIANT_BOOL",
        VarEnum.VT_VARIANT => "VARIANT",
        VarEnum.VT_DECIMAL => "DECIMAL",
        VarEnum.VT_VOID => "void",
        VarEnum.VT_HRESULT => "HRESULT",
        VarEnum.VT_LPSTR => "LPSTR",
        VarEnum.VT_LPWSTR => "LPWSTR",
        VarEnum.VT_FILETIME => "FILETIME",
        VarEnum.VT_CLSID => "CLSID",
        VT_INT_PTR => "INT_PTR",
        VT_UINT_PTR => "UINT_PTR",
        _ => vt.ToString(),
    };
}
