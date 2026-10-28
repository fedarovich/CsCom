using System.Globalization;
using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
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

    /// <summary>The annotation that associates the parameter syntax with <see cref="Definition"/>.</summary>
    public SyntaxAnnotation? Annotation { get; init; }

    public string Name => Syntax.Escape(Definition.Name);

    public bool IsByRef => Modifier is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword;

    /// <summary>Associates a parameter declared for this parameter with <see cref="Definition"/>.</summary>
    public ParameterSyntax Annotate(ParameterSyntax parameter) => Annotation is null ? parameter : parameter.WithAdditionalAnnotations(Annotation);

    public ParameterSyntax ToSyntax(bool includeMarshalling = true)
    {
        var parameter = Annotate(Parameter(Syntax.Identifier(Definition.Name)).WithType(Syntax.Type(TypeName)));
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
/// An overload of a method that omits optional parameters whose default values C# cannot express.
/// </summary>
/// <param name="Parameters">The parameters of the overload, with the C# default values that can be expressed.</param>
/// <param name="Arguments">The arguments of the call of the method: the parameters, then the default values of the omitted ones.</param>
/// <param name="Setup">The statements that prepare the default values, e.g. allocate a <c>BSTR</c>.</param>
/// <param name="Cleanup">The statements that release the default values after the call.</param>
internal sealed record DefaultOverload(IReadOnlyList<SignatureParameter> Parameters, IReadOnlyList<string> Arguments, IReadOnlyList<string> Setup, IReadOnlyList<string> Cleanup);

/// <summary>
/// Builds the signatures of generated methods.
/// </summary>
internal sealed partial class SignatureBuilder
{
    /// <summary>The maximum number of overloads generated per method for default values that C# cannot express.</summary>
    public const int MaxDefaultOverloads = 4;

    private const string Missing = "global::System.Reflection.Missing.Value";

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
        return _context.Annotations.Annotate(declaration.WithAttributeLists(List(attributes)).WithDocumentation(method.Documentation), method);
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
            returnType = _mapper.WithMarshalling(method.ReturnMarshalling).MapValue(method.ReturnType);
        }
        else if (HasRetval(method, out var retvalType))
        {
            returnType = retvalType is PrimitiveTypeReference { VarType: VarEnum.VT_VOID }
                ? new ManagedType("void*", IsPointer: true)
                : _mapper.WithMarshalling(method.RetvalParameter!.Marshalling).MapValue(retvalType);
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
        var returnType = hasRetval
            ? _mapper.WithMarshalling(method.RetvalParameter!.Marshalling).MapLateBound(retvalType)
            : method.ReturnsHResult ? LateBoundType.Void : _mapper.WithMarshalling(method.ReturnMarshalling).MapLateBound(method.ReturnType);
        return (returnType, parameters);
    }

    /// <summary>
    /// Maps the type of the value of a property getter: its <c>[retval]</c> parameter, or its return value if it has none.
    /// </summary>
    public LateBoundType MapLateBoundGetterType(MethodDefinition getter) => HasRetval(getter, out var retvalType)
        ? _mapper.WithMarshalling(getter.RetvalParameter!.Marshalling).MapLateBound(retvalType)
        : _mapper.WithMarshalling(getter.ReturnMarshalling).MapLateBound(getter.ReturnType);

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
                    result.Add(CreateParameter(parameter, parameter.Type, "void*", null, null));
                    continue;
                }

                bool isOut = parameter.Flags.HasFlag(ParameterFlags.Out);
                bool isIn = parameter.Flags.HasFlag(ParameterFlags.In);
                SyntaxKind modifier = isOut
                    ? isIn ? SyntaxKind.RefKeyword : SyntaxKind.OutKeyword
                    : IsPassedAsIn(element) ? SyntaxKind.InKeyword : SyntaxKind.RefKeyword;
                var type = _mapper.WithMarshalling(parameter.Marshalling).MapValue(element);
                result.Add(CreateParameter(parameter, element, type.Name, type.Marshalling, modifier));
            }
            else
            {
                var type = _mapper.WithMarshalling(parameter.Marshalling).MapValue(parameter.Type);
                result.Add(CreateParameter(parameter, parameter.Type, type.Name, type.Marshalling, null));
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
            if (TypeMapper.TryGetByRefElement(parameter.Type, out var element) && element is not PrimitiveTypeReference { VarType: VarEnum.VT_VOID })
            {
                valueType = element;
                if (parameter.Flags.HasFlag(ParameterFlags.Out))
                {
                    modifier = parameter.Flags.HasFlag(ParameterFlags.In) ? SyntaxKind.RefKeyword : SyntaxKind.OutKeyword;
                }
            }

            var type = _mapper.WithMarshalling(parameter.Marshalling).MapLateBound(valueType);
            result.Add(CreateParameter(parameter, valueType, type.Name, null, modifier));
        }

        ApplyDefaults(result, lateBound: true);
        return result;
    }

    private SignatureParameter CreateParameter(ParameterDefinition definition, TypeReference valueType, string typeName, string? marshalling, SyntaxKind? modifier) =>
        new(definition, valueType, typeName, marshalling, modifier) { Annotation = _context.Annotations.GetAnnotation(definition) };

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
            if (GetConstantDefault(parameter, lateBound) is not { } defaultValue)
            {
                return;
            }

            if (IsOptionalVariant(parameter, lateBound))
            {
                // The COM object applies the default value of an omitted argument.
                if (!lateBound)
                {
                    parameters[i] = parameter = parameter with { Marshalling = _mapper.VariantMarshalling(optional: true) };
                }

                parameter.IsOptionalVariant = true;
            }

            parameter.DefaultValue = defaultValue;
        }
    }

    private static bool IsOptionalVariant(SignatureParameter parameter, bool lateBound) =>
        parameter.TypeName == "object?" && (lateBound || parameter.Marshalling?.Contains("VariantMarshaller", StringComparison.Ordinal) == true);

    // The C# default value of an optional parameter, or null if it has none or C# cannot express it.
    private string? GetConstantDefault(SignatureParameter parameter, bool lateBound)
    {
        var flags = parameter.Definition.Flags;
        if (parameter.IsByRef || (!flags.HasFlag(ParameterFlags.Optional) && !flags.HasFlag(ParameterFlags.HasDefault)))
        {
            return null;
        }

        if (IsOptionalVariant(parameter, lateBound))
        {
            return "null";
        }

        return flags.HasFlag(ParameterFlags.HasDefault) && parameter.Definition.DefaultValue is { } value
            ? FormatConstant(value, parameter.ValueType, parameter.TypeName)
            : parameter.TypeName.EndsWith('?') && flags.HasFlag(ParameterFlags.Optional) ? "null" : null;
    }

    /// <summary>
    /// Gets the overloads of a method whose optional parameters have default values that C# cannot express, e.g.
    /// <c>ComBool.True</c> or a <c>DateTime</c>. Each overload ends before one of these parameters and passes the default
    /// values of the omitted parameters, so the numbers of arguments of the overloads do not overlap. At most
    /// <see cref="MaxDefaultOverloads"/> overloads are generated, for the first of these parameters.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="parameters">The parameters of the method, as mapped by <see cref="MapParameters"/> or <see cref="MapLateBoundParameters"/>.</param>
    /// <param name="lateBound">Whether the method is invoked through <c>IDispatch::Invoke</c>.</param>
    public List<DefaultOverload> GetDefaultOverloads(MethodDefinition method, List<SignatureParameter> parameters, bool lateBound)
    {
        var overloads = new List<DefaultOverload>();
        if (parameters.Any(static p => p.TypeName.EndsWith('*')))
        {
            return overloads;
        }

        var constants = parameters.Select(p => GetConstantDefault(p, lateBound)).ToList();
        var expressions = parameters.Select(GetDefaultExpression).ToList();

        // The trailing parameters that can be omitted, and the ones among them whose default values need an overload.
        int start = parameters.Count;
        while (start > 0 && !parameters[start - 1].IsByRef && (constants[start - 1] is not null || expressions[start - 1] is not null))
        {
            start--;
        }

        var lost = Enumerable.Range(start, parameters.Count - start).Where(i => constants[i] is null).ToList();
        if (lost.Count > MaxDefaultOverloads)
        {
            LogTooManyOverloads(_context.Logger, method.Name, lost.Count, MaxDefaultOverloads);
        }

        foreach (int index in lost.Take(MaxDefaultOverloads))
        {
            var kept = parameters.Take(index).Select(static p => p with { DefaultValue = null, IsOptionalVariant = false }).ToList();
            ApplyDefaults(kept, lateBound);

            var arguments = new List<string>();
            var setup = new List<string>();
            var cleanup = new List<string>();
            for (int i = 0; i < parameters.Count; i++)
            {
                // The method does not treat the null value of an optional VARIANT that precedes a lost default value as an
                // omitted argument, so the overloads pass Missing.Value, which is marshalled as DISP_E_PARAMNOTFOUND.
                bool passMissing = IsOptionalVariant(parameters[i], lateBound) && !parameters[i].IsOptionalVariant;
                if (i < index)
                {
                    arguments.Add(Modifier(parameters[i]) + parameters[i].Name + (passMissing ? $" ?? {Missing}" : string.Empty));
                }
                else if (passMissing)
                {
                    arguments.Add(Missing);
                }
                else if (constants[i] is { } constant)
                {
                    arguments.Add(constant);
                }
                else
                {
                    var expression = expressions[i]!;
                    arguments.Add(expression.Argument);
                    setup.AddRange(expression.Setup);
                    cleanup.AddRange(expression.Cleanup);
                }
            }

            overloads.Add(new DefaultOverload(kept, arguments, setup, cleanup));
        }

        return overloads;

        static string Modifier(SignatureParameter parameter) => parameter.Modifier switch
        {
            SyntaxKind.RefKeyword => "ref ",
            SyntaxKind.OutKeyword => "out ",
            SyntaxKind.InKeyword => "in ",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Builds an overload of a method that omits parameters with default values; the body calls <paramref name="callee"/>.
    /// </summary>
    public static MethodDeclarationSyntax BuildOverload(DefaultOverload overload, string returnType, string name, string callee, params SyntaxKind[] modifiers)
    {
        string call = $"{callee}({string.Join(", ", overload.Arguments)})";
        string statement = returnType == "void" ? $"{call};" : $"return {call};";
        var declaration = MethodDeclaration(Syntax.Type(returnType), Syntax.Identifier(name))
            .WithModifiers(Syntax.Modifiers(modifiers))
            .WithParameterList(Syntax.Parameters(overload.Parameters.Select(static p => p.ToSyntax(includeMarshalling: false))));

        if (overload.Setup.Count == 0)
        {
            return declaration
                .WithExpressionBody(Syntax.Arrow(call))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        }

        var statements = overload.Setup.Select(Syntax.Statement).ToList();
        statements.Add(overload.Cleanup.Count == 0
            ? Syntax.Statement(statement)
            : TryStatement(Block(Syntax.Statement(statement)), default, FinallyClause(Block(overload.Cleanup.Select(Syntax.Statement)))));
        return declaration.WithBody(Block(statements));
    }

    // An expression for a default value that C# cannot express as a constant, with the statements that prepare and release it.
    private DefaultExpression? GetDefaultExpression(SignatureParameter parameter)
    {
        if (parameter.IsByRef || !parameter.Definition.Flags.HasFlag(ParameterFlags.HasDefault) || parameter.Definition.DefaultValue is not { } value)
        {
            return null;
        }

        string support = _context.Support;
        string temporary = "__" + parameter.Definition.Name;
        try
        {
            return (parameter.ValueType, parameter.TypeName) switch
            {
                (PrimitiveTypeReference { VarType: VarEnum.VT_BOOL }, _) when parameter.TypeName == $"{support}.ComBool" =>
                    new(IsTrue(value) ? $"{support}.ComBool.True" : $"{support}.ComBool.False", [], []),
                (PrimitiveTypeReference { VarType: VarEnum.VT_DATE }, "global::System.DateTime") =>
                    new($"global::System.DateTime.FromOADate({FormatDouble(ToOADate(value))})", [], []),
                (PrimitiveTypeReference { VarType: VarEnum.VT_DATE }, _) when parameter.TypeName == $"{support}.ComDate" =>
                    new($"new {support}.ComDate({FormatDouble(ToOADate(value))})", [], []),
                (PrimitiveTypeReference { VarType: VarEnum.VT_CY }, _) when parameter.TypeName == $"{support}.ComCurrency" =>
                    new($"new {support}.ComCurrency({decimal.ToOACurrency(System.Convert.ToDecimal(value, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture)}L)", [], []),
                (PrimitiveTypeReference { VarType: VarEnum.VT_BSTR }, _) when value is string text && parameter.TypeName == $"{support}.ComBstr?" =>
                    new(temporary, [$"using var {temporary} = {support}.ComBstr.Create({Syntax.Literal(text)});"], []),
                (PrimitiveTypeReference { VarType: VarEnum.VT_BSTR }, "nint") when value is string text =>
                    new(
                        temporary,
                        [$"nint {temporary} = global::System.Runtime.InteropServices.Marshal.StringToBSTR({Syntax.Literal(text)});"],
                        [$"global::System.Runtime.InteropServices.Marshal.FreeBSTR({temporary});"]),
                _ => null,
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }

        static bool IsTrue(object value) => value is bool b ? b : System.Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;

        static double ToOADate(object value) => value is DateTime date ? date.ToOADate() : System.Convert.ToDouble(value, CultureInfo.InvariantCulture);

        static string FormatDouble(double value) => value.ToString("R", CultureInfo.InvariantCulture) + "D";
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Method} has {Count} parameters with default values that need overloads; only {Max} overloads are generated")]
    private static partial void LogTooManyOverloads(ILogger logger, string method, int count, int max);

    private sealed record DefaultExpression(string Argument, IReadOnlyList<string> Setup, IReadOnlyList<string> Cleanup);

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
