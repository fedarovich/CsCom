using System.Globalization;
using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// A parameter of a generated method.
/// </summary>
/// <param name="Definition">The parameter in the model.</param>
/// <param name="ValueType">The type of the value, without the pointer used to pass it by reference.</param>
/// <param name="TypeName">The managed type.</param>
/// <param name="Marshalling">The marshalling attribute without brackets, if any.</param>
/// <param name="Modifier">The <c>in</c>, <c>ref</c> or <c>out</c> modifier, if any.</param>
internal sealed record SignatureParameter(ParameterDefinition Definition, TypeReference ValueType, string TypeName, string? Marshalling, SyntaxKind? Modifier)
{
    /// <summary>The default value expression, if the parameter is optional.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Whether the parameter is an optional <c>VARIANT</c>, whose <see langword="null"/> value means an omitted argument.</summary>
    public bool IsOptionalVariant { get; set; }

    public string Name => Syntax.Escape(Definition.Name);

    public bool IsByRef => Modifier is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword;

    public ParameterSyntax ToSyntax(bool includeMarshalling = true)
    {
        var parameter = Parameter(Syntax.Identifier(Definition.Name)).WithType(Syntax.Type(TypeName));
        if (Modifier is { } modifier)
        {
            parameter = parameter.WithModifiers(TokenList(Token(modifier)));
        }

        if (includeMarshalling && Marshalling is not null)
        {
            parameter = parameter.WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute(Marshalling))));
        }

        if (DefaultValue is not null)
        {
            parameter = parameter.WithDefault(EqualsValueClause(Syntax.Expression(DefaultValue)));
        }

        return parameter;
    }
}

/// <summary>
/// Builds the signatures of generated methods.
/// </summary>
internal sealed class SignatureBuilder
{
    private readonly EmitContext _context;
    private readonly TypeMapper _mapper;
    private readonly string _namespace;

    public SignatureBuilder(EmitContext context, TypeMapper mapper, string currentNamespace)
    {
        _context = context;
        _mapper = mapper;
        _namespace = currentNamespace;
    }

    public TypeMapper Mapper => _mapper;

    /// <summary>
    /// Gets the name of the method generated for a method or a property accessor.
    /// </summary>
    public static string GetMethodName(MethodDefinition method) => method.InvokeKind switch
    {
        InvokeKind.PropertyGet => "get_" + method.Name,
        InvokeKind.PropertyPut => "set_" + method.Name,
        InvokeKind.PropertyPutRef => "putref_" + method.Name,
        _ => method.Name,
    };

    /// <summary>
    /// Whether the method returns an <c>HRESULT</c> that is translated to an exception and has its <c>[retval]</c> parameter mapped to the return value.
    /// </summary>
    public static bool HasRetval(MethodDefinition method, out TypeReference retvalType)
    {
        if (method.ReturnsHResult && method.RetvalParameter is { } retval && TypeMapper.TryGetByRefElement(retval.Type, out retvalType))
        {
            return true;
        }

        retvalType = method.ReturnType;
        return false;
    }

    /// <summary>
    /// Builds a method of a source-generated COM interface.
    /// </summary>
    public MethodDeclarationSyntax BuildComMethod(MethodDefinition method, ref bool isUnsafe)
    {
        bool preserveSig = !method.ReturnsHResult;
        var (returnType, signature) = MapComSignature(method);
        var declaration = MethodDeclaration(Syntax.Type(returnType.Name), Syntax.Identifier(GetMethodName(method)))
            .WithParameterList(Syntax.Parameters(signature.Select(static p => p.ToSyntax())))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        var attributes = new List<AttributeListSyntax>();
        if (preserveSig)
        {
            attributes.Add(Syntax.AttributeList(Syntax.Attribute($"{Syntax.InteropNamespace}.PreserveSig")));
        }

        if (returnType.Marshalling is not null)
        {
            attributes.Add(Syntax.ReturnAttributeList(Syntax.Attribute(returnType.Marshalling)));
        }

        isUnsafe |= returnType.IsPointer || signature.Any(static p => p.TypeName.EndsWith('*'));
        return declaration.WithAttributeLists(List(attributes)).WithDocumentation(method.Documentation);
    }

    /// <summary>
    /// Maps the return type and the parameters of a method of a source-generated COM interface: methods that return an
    /// <c>HRESULT</c> return their <c>[retval]</c> parameter, the other ones keep their signature (<c>[PreserveSig]</c>).
    /// </summary>
    public (ManagedType ReturnType, List<SignatureParameter> Parameters) MapComSignature(MethodDefinition method)
    {
        IEnumerable<ParameterDefinition> parameters = method.Parameters;
        ManagedType returnType;
        if (!method.ReturnsHResult)
        {
            returnType = _mapper.MapValue(method.ReturnType);
        }
        else if (HasRetval(method, out var retvalType))
        {
            returnType = retvalType is PrimitiveTypeReference { VarType: VarEnum.VT_VOID } ? new ManagedType("void*", IsPointer: true) : _mapper.MapValue(retvalType);
            parameters = method.Parameters.Take(method.Parameters.Count - 1);
        }
        else
        {
            returnType = ManagedType.Void;
        }

        return (returnType, MapParameters(parameters));
    }

    /// <summary>
    /// Maps the return type and the parameters of a method invoked through <c>IDispatch::Invoke</c>.
    /// </summary>
    public (LateBoundType ReturnType, List<SignatureParameter> Parameters) MapLateBoundSignature(MethodDefinition method)
    {
        bool hasRetval = HasRetval(method, out var retvalType);
        var parameters = MapLateBoundParameters(hasRetval ? method.Parameters.Take(method.Parameters.Count - 1) : method.Parameters);
        var returnType = hasRetval ? _mapper.MapLateBound(retvalType) : method.ReturnsHResult ? LateBoundType.Void : _mapper.MapLateBound(method.ReturnType);
        return (returnType, parameters);
    }

    /// <summary>
    /// Maps the parameters of a source-generated COM or P/Invoke method.
    /// </summary>
    public List<SignatureParameter> MapParameters(IEnumerable<ParameterDefinition> parameters)
    {
        var result = new List<SignatureParameter>();
        foreach (var parameter in parameters)
        {
            if (TypeMapper.TryGetByRefElement(parameter.Type, out var element))
            {
                if (element is PrimitiveTypeReference { VarType: VarEnum.VT_VOID })
                {
                    result.Add(new SignatureParameter(parameter, parameter.Type, "void*", null, null));
                    continue;
                }

                bool isOut = parameter.Flags.HasFlag(ParameterFlags.Out);
                bool isIn = parameter.Flags.HasFlag(ParameterFlags.In);
                SyntaxKind modifier = isOut
                    ? isIn ? SyntaxKind.RefKeyword : SyntaxKind.OutKeyword
                    : IsPassedAsIn(element) ? SyntaxKind.InKeyword : SyntaxKind.RefKeyword;
                var type = _mapper.MapValue(element);
                result.Add(new SignatureParameter(parameter, element, type.Name, type.Marshalling, modifier));
            }
            else
            {
                var type = _mapper.MapValue(parameter.Type);
                result.Add(new SignatureParameter(parameter, parameter.Type, type.Name, type.Marshalling, null));
            }
        }

        ApplyDefaults(result, lateBound: false);
        return result;
    }

    /// <summary>
    /// Maps the parameters of a late-bound dispinterface wrapper method.
    /// </summary>
    public List<SignatureParameter> MapLateBoundParameters(IEnumerable<ParameterDefinition> parameters)
    {
        var result = new List<SignatureParameter>();
        foreach (var parameter in parameters)
        {
            SyntaxKind? modifier = null;
            TypeReference valueType = parameter.Type;
            if (TypeMapper.TryGetByRefElement(parameter.Type, out var element))
            {
                valueType = element;
                if (parameter.Flags.HasFlag(ParameterFlags.Out))
                {
                    modifier = parameter.Flags.HasFlag(ParameterFlags.In) ? SyntaxKind.RefKeyword : SyntaxKind.OutKeyword;
                }
            }

            var type = _mapper.MapLateBound(valueType);
            result.Add(new SignatureParameter(parameter, valueType, type.Name, null, modifier));
        }

        ApplyDefaults(result, lateBound: true);
        return result;
    }

    private static bool IsPassedAsIn(TypeReference element) =>
        element is PrimitiveTypeReference { VarType: VarEnum.VT_VARIANT }
            or SafeArrayTypeReference
            or UserDefinedTypeReference { Definition: RecordDefinition }
            or WellKnownTypeReference { Type: WellKnownType.Guid };

    // C# requires optional parameters to follow the required ones, so only the trailing ones get default values.
    private void ApplyDefaults(List<SignatureParameter> parameters, bool lateBound)
    {
        for (int i = parameters.Count - 1; i >= 0; i--)
        {
            var parameter = parameters[i];
            var flags = parameter.Definition.Flags;
            if (parameter.IsByRef || (!flags.HasFlag(ParameterFlags.Optional) && !flags.HasFlag(ParameterFlags.HasDefault)))
            {
                return;
            }

            if (parameter.TypeName == "object?")
            {
                bool isVariant = lateBound || parameter.Marshalling?.Contains("VariantMarshaller", StringComparison.Ordinal) == true;
                if (isVariant)
                {
                    // The COM object applies the default value of an omitted argument.
                    if (!lateBound)
                    {
                        parameters[i] = parameter = parameter with { Marshalling = _mapper.VariantMarshalling(optional: true) };
                    }

                    parameter.IsOptionalVariant = true;
                    parameter.DefaultValue = "null";
                    continue;
                }
            }

            string? defaultValue = flags.HasFlag(ParameterFlags.HasDefault) && parameter.Definition.DefaultValue is { } value
                ? FormatConstant(value, parameter.ValueType, parameter.TypeName)
                : parameter.TypeName.EndsWith('?') && flags.HasFlag(ParameterFlags.Optional) ? "null" : null;

            if (defaultValue is null)
            {
                return;
            }

            parameter.DefaultValue = defaultValue;
        }
    }

    /// <summary>
    /// Formats a constant of a type library type as a C# expression, or returns <see langword="null"/> if it cannot be represented.
    /// </summary>
    public string? FormatConstant(object value, TypeReference type, string typeName)
    {
        try
        {
            // The representation of these types depends on the marshalling options: the .NET type, the COM type or a wrapper,
            // whose only constant is its default value.
            if (type is PrimitiveTypeReference { VarType: VarEnum.VT_BOOL or VarEnum.VT_CY or VarEnum.VT_DATE } automation)
            {
                return typeName switch
                {
                    "bool" => IsTrue(value) ? "true" : "false",
                    "short" => IsTrue(value) ? "-1" : "0",
                    "decimal" => $"{ToDecimal(value).ToString(CultureInfo.InvariantCulture)}m",
                    "long" => decimal.ToOACurrency(ToDecimal(value)).ToString(CultureInfo.InvariantCulture),
                    "double" => FormatFloating(value is DateTime date ? date.ToOADate() : System.Convert.ToDouble(value, CultureInfo.InvariantCulture), "D"),
                    _ when typeName.StartsWith(_context.Support + ".", StringComparison.Ordinal) && IsDefault(automation.VarType, value) => "default",
                    _ => null,
                };
            }

            if (type is UserDefinedTypeReference { Definition: EnumDefinition enumDefinition })
            {
                long number = System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
                string name = _context.GetTypeName(enumDefinition, _namespace);
                return number < 0 ? $"({name})({number.ToString(CultureInfo.InvariantCulture)})" : $"({name}){number.ToString(CultureInfo.InvariantCulture)}";
            }

            return typeName switch
            {
                "bool" => value is bool b ? (b ? "true" : "false") : System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0 ? "true" : "false",
                "string?" or "string" => value is string s ? Syntax.Literal(s) : null,
                "sbyte" or "short" or "int" or "long" => System.Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                "byte" or "ushort" or "uint" or "ulong" => System.Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
                // Type libraries often store double constants as VT_R4; format them with the float precision.
                "float" => FormatFloating(System.Convert.ToSingle(value, CultureInfo.InvariantCulture), "F"),
                "double" when value is float single => FormatFloating(single, "D"),
                "double" => FormatFloating(System.Convert.ToDouble(value, CultureInfo.InvariantCulture), "D"),
                "decimal" => $"{System.Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}m",
                _ => null,
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }

        static bool IsTrue(object value) => value is bool b ? b : System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

        static decimal ToDecimal(object value) => System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);

        static bool IsDefault(VarEnum vt, object value) => vt switch
        {
            VarEnum.VT_BOOL => !IsTrue(value),
            VarEnum.VT_CY => ToDecimal(value) == 0,
            _ => (value is DateTime date ? date.ToOADate() : System.Convert.ToDouble(value, CultureInfo.InvariantCulture)) == 0,
        };

        static string FormatFloating<T>(T value, string suffix) where T : System.Numerics.IFloatingPointIeee754<T>
        {
            if (T.IsNaN(value) || T.IsInfinity(value))
            {
                string type = suffix == "F" ? "float" : "double";
                return T.IsNaN(value) ? $"{type}.NaN" : T.IsPositive(value) ? $"{type}.PositiveInfinity" : $"{type}.NegativeInfinity";
            }

            return value.ToString("R", CultureInfo.InvariantCulture) + suffix;
        }
    }
}
