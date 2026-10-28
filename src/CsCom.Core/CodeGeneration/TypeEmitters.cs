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
/// Emits enums, structures, coclasses and modules.
/// </summary>
internal sealed partial class TypeEmitters
{
    private readonly EmitContext _context;

    public TypeEmitters(EmitContext context)
    {
        _context = context;
    }

    public EnumDeclarationSyntax EmitEnum(EnumDefinition definition)
    {
        var annotations = _context.Annotations;
        var members = definition.Members.Select(member => annotations.Annotate(
            EnumMemberDeclaration(Syntax.Identifier(member.Name))
                .WithEqualsValue(EqualsValueClause(Syntax.Expression(Syntax.IntegerLiteral(member.Value))))
                .WithDocumentation(member.Documentation),
            member));

        return annotations.Annotate(
            EnumDeclaration(Syntax.Identifier(definition.Name))
                .WithModifiers(Syntax.Modifiers(_context.Accessibility))
                .WithMembers(SeparatedList(members))
                .WithDocumentation(definition.Documentation),
            definition);
    }

    public StructDeclarationSyntax EmitRecord(RecordDefinition definition, string @namespace)
    {
        var mapper = new TypeMapper(_context, @namespace);
        var members = new List<MemberDeclarationSyntax>();
        bool isUnsafe = false;
        var fieldOffset = Syntax.AttributeList(Syntax.Attribute($"{Syntax.InteropNamespace}.FieldOffset", "0"));
        var mode = _context.Options.StructProperties;
        var names = new HashSet<string>(StringComparer.Ordinal) { definition.Name };
        foreach (var field in definition.Fields)
        {
            names.Add(field.Name);
            names.Add(GetBufferName(field.Name));
        }

        foreach (var field in definition.Fields)
        {
            string type;
            FieldConversion? conversion = null;
            var fieldMapper = mapper.WithMarshalling(field.Marshalling);
            if (field.Type is FixedArrayTypeReference array)
            {
                // Inline arrays rather than fixed buffers, so that the elements can be accessed without an unsafe context.
                // Their elements have the COM types, because they cannot be converted one by one.
                var element = fieldMapper.MapField(array.ElementType);
                type = GetBufferName(field.Name);
                members.Add(_context.Annotations.Annotate(InlineArray(type, element.Name, array.Length), field, SyntaxRole.InlineArray));
                isUnsafe |= element.IsPointer;
            }
            else
            {
                var mapped = fieldMapper.MapField(field.Type);
                type = mapped.Name;
                isUnsafe |= mapped.IsPointer;
                if (mode != StructProperties.Never)
                {
                    conversion = fieldMapper.GetFieldConversion(field.Type);
                }
            }

            // The fields keep the layout of the COM structure; properties expose the members converted to .NET types.
            bool isProperty = conversion is not null;
            string fieldName = isProperty ? GetBackingFieldName(field.Name, names) : field.Name;
            var declaration = Field(type, fieldName, isProperty ? SyntaxKind.PrivateKeyword : SyntaxKind.PublicKeyword);
            if (definition.IsUnion)
            {
                declaration = declaration.WithAttributeLists(SingletonList(fieldOffset));
            }

            if (!isProperty)
            {
                members.Add(_context.Annotations.Annotate(declaration.WithDocumentation(field.Documentation), field));
                continue;
            }

            string getter = conversion?.Get(fieldName) ?? fieldName;
            string setter = conversion?.Set("value") ?? "value";
            members.Add(_context.Annotations.Annotate(declaration, field, SyntaxRole.BackingField));
            members.Add(_context.Annotations.Annotate(
                ParseMember($"public {conversion?.TypeName ?? type} {Syntax.Escape(field.Name)} {{ readonly get => {getter}; set => {fieldName} = {setter}; }}")
                    .WithDocumentation(field.Documentation),
                field));
        }

        string layout = $"{Syntax.InteropNamespace}.LayoutKind.{(definition.IsUnion ? "Explicit" : "Sequential")}";
        string layoutArguments = definition.Alignment > 0 ? $"{layout}, Pack = {definition.Alignment}" : layout;

        var modifiers = new List<SyntaxKind> { _context.Accessibility };
        if (isUnsafe)
        {
            modifiers.Add(SyntaxKind.UnsafeKeyword);
        }

        modifiers.Add(SyntaxKind.PartialKeyword);
        return _context.Annotations.Annotate(
            StructDeclaration(Syntax.Identifier(definition.Name))
                .WithModifiers(Syntax.Modifiers([.. modifiers]))
                .WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute($"{Syntax.InteropNamespace}.StructLayout", layoutArguments))))
                .WithMembers(List(members))
                .WithDocumentation(definition.Documentation),
            definition);
    }

    public ClassDeclarationSyntax EmitCoClass(CoClassDefinition definition, string @namespace)
    {
        string support = _context.Support;
        var members = new List<MemberDeclarationSyntax>();
        if (definition.Guid is { } guid)
        {
            members.Add(ParseMember($"public static readonly global::System.Guid Clsid = new({Syntax.GuidLiteral(guid)});")
                .WithDocumentation($"The class identifier (CLSID) of the {definition.OriginalName} class."));

            if (definition.Flags.HasFlag(TypeFlags.CanCreate))
            {
                string context = $"{support}.ClassContext context = {support}.ComActivator.DefaultContext";
                string create = definition.DefaultInterface?.Interface switch
                {
                    UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } wrapper } =>
                        Create(_context.GetTypeName(wrapper, @namespace), "CreateDispatchInstance"),
                    UserDefinedTypeReference { Definition: InterfaceDefinition defaultInterface } =>
                        Create(_context.GetTypeName(defaultInterface, @namespace), "CreateInstance"),
                    PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => Create($"{support}.IDispatch", "CreateInstance"),
                    _ => Create("object", "CreateInstance"),
                };

                members.Add(ParseMember(create).WithDocumentation(
                    $"Creates an instance of the {definition.OriginalName} class.",
                    ("param name=\"context\"", "The context in which the code that manages the newly created object will run.")));

                // Objects whose default interface is a vtable interface may not implement IDispatch.
                bool isDispatchable = definition.DefaultInterface?.Interface is
                    UserDefinedTypeReference { Definition: InterfaceDefinition { InterfaceKind: InterfaceKind.Dual or InterfaceKind.Dispatch } }
                    or PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH };
                if (isDispatchable)
                {
                    members.Add(ParseMember(
                            $"[global::System.Diagnostics.CodeAnalysis.RequiresDynamicCode({Syntax.Literal("Late binding with dynamic requires dynamic code generation and is not compatible with Native AOT.")})] " +
                            $"public static dynamic CreateDynamic({context}) => {support}.ComActivator.CreateDynamicInstance(Clsid, context);")
                        .WithDocumentation(
                            $"Creates an instance of the {definition.OriginalName} class for late binding with dynamic.",
                            ("param name=\"context\"", "The context in which the code that manages the newly created object will run.")));
                }

                string Create(string type, string method) =>
                    $"public static {type} Create({context}) => {support}.ComActivator.{method}<{type}>(Clsid, context);";
            }
        }

        return _context.Annotations.Annotate(
            ClassDeclaration(Syntax.Identifier(definition.Name))
                .WithModifiers(Syntax.Modifiers(_context.Accessibility, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword))
                .WithMembers(List(members))
                .WithDocumentation(definition.Documentation ?? $"The {definition.OriginalName} class."),
            definition);
    }

    public ClassDeclarationSyntax? EmitModule(ModuleDefinition definition, string @namespace)
    {
        var mapper = new TypeMapper(_context, @namespace);
        var signatures = new SignatureBuilder(_context, mapper, @namespace);
        var members = new List<MemberDeclarationSyntax>();
        bool isUnsafe = false;

        foreach (var constant in definition.Constants)
        {
            if (EmitConstant(constant, signatures) is { } declaration)
            {
                members.Add(_context.Annotations.Annotate(declaration, constant));
            }
            else
            {
                LogUnsupportedConstant(_context.Logger, definition.Name, constant.Name);
            }
        }

        foreach (var function in definition.Functions)
        {
            if (string.IsNullOrEmpty(function.DllName))
            {
                LogMissingDll(_context.Logger, definition.Name, function.Name);
                continue;
            }

            members.Add(EmitFunction(function, signatures, ref isUnsafe));
            if (_context.Options.OverloadsForDefaultValues)
            {
                var returnType = signatures.Mapper.WithMarshalling(function.ReturnMarshalling).MapValue(function.ReturnType);
                if (!returnType.IsPointer)
                {
                    members.AddRange(signatures.GetDefaultOverloads(function, signatures.MapParameters(function.Parameters), lateBound: false).Select(overload =>
                        _context.Annotations.Annotate(
                            SignatureBuilder.BuildOverload(overload, returnType.Name, function.Name, Syntax.Escape(function.Name), SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword)
                                .WithDocumentation(function.Documentation),
                            function,
                            SyntaxRole.Overload)));
                }
            }
        }

        if (members.Count == 0)
        {
            return null;
        }

        var modifiers = new List<SyntaxKind> { _context.Accessibility, SyntaxKind.StaticKeyword };
        if (isUnsafe)
        {
            modifiers.Add(SyntaxKind.UnsafeKeyword);
        }

        modifiers.Add(SyntaxKind.PartialKeyword);
        return _context.Annotations.Annotate(
            ClassDeclaration(Syntax.Identifier(definition.Name))
                .WithModifiers(Syntax.Modifiers([.. modifiers]))
                .WithMembers(List(members))
                .WithDocumentation(definition.Documentation),
            definition);
    }

    private MemberDeclarationSyntax? EmitConstant(ConstantDefinition constant, SignatureBuilder signatures)
    {
        if (constant.Value is null)
        {
            return null;
        }

        string name = Syntax.Escape(constant.Name);
        if (constant.Value is DateTime date)
        {
            return ParseMember($"public static readonly global::System.DateTime {name} = new({date.Ticks.ToString(CultureInfo.InvariantCulture)}L);")
                .WithDocumentation(constant.Documentation);
        }

        string? type = constant.Type switch
        {
            PrimitiveTypeReference { VarType: VarEnum.VT_BSTR or VarEnum.VT_LPSTR or VarEnum.VT_LPWSTR } => "string",
            // Constants use the .NET types regardless of the marshalling options.
            PrimitiveTypeReference { VarType: VarEnum.VT_CY } => "decimal",
            PrimitiveTypeReference { VarType: VarEnum.VT_BOOL } => "bool",
            PrimitiveTypeReference { VarType: VarEnum.VT_VARIANT } => constant.Value switch
            {
                string => "string",
                bool => "bool",
                double => "double",
                float => "float",
                decimal => "decimal",
                long => "long",
                _ => "int",
            },
            _ => signatures.Mapper.MapValue(constant.Type) is { Marshalling: null, IsReference: false, IsPointer: false, IsVoid: false } mapped
                ? mapped.Name
                : null,
        };

        if (type is null || signatures.FormatConstant(constant.Value, constant.Type, type) is not { } value)
        {
            return null;
        }

        return ParseMember($"public const {type} {name} = {value};").WithDocumentation(constant.Documentation);
    }

    private MethodDeclarationSyntax EmitFunction(MethodDefinition function, SignatureBuilder signatures, ref bool isUnsafe)
    {
        var returnType = signatures.Mapper.WithMarshalling(function.ReturnMarshalling).MapValue(function.ReturnType);
        var parameters = signatures.MapParameters(function.Parameters);
        isUnsafe |= returnType.IsPointer || parameters.Any(static p => p.TypeName.EndsWith('*'));

        string entryPoint = function.EntryPoint ?? $"#{function.EntryPointOrdinal?.ToString(CultureInfo.InvariantCulture)}";
        var attributes = new List<AttributeListSyntax>
        {
            Syntax.AttributeList(Syntax.Attribute(
                $"{Syntax.InteropNamespace}.LibraryImport",
                $"{Syntax.Literal(function.DllName!)}, EntryPoint = {Syntax.Literal(entryPoint)}, StringMarshalling = {Syntax.InteropNamespace}.StringMarshalling.Custom, StringMarshallingCustomType = typeof({Syntax.MarshallingNamespace}.BStrStringMarshaller)")),
        };

        if (function.CallingConvention == CallingConvention.Cdecl)
        {
            attributes.Add(Syntax.AttributeList(Syntax.Attribute(
                $"{Syntax.InteropNamespace}.UnmanagedCallConv",
                "CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) }")));
        }

        if (returnType.Marshalling is not null)
        {
            attributes.Add(Syntax.ReturnAttributeList(Syntax.Attribute(returnType.Marshalling)));
        }

        return _context.Annotations.Annotate(
            MethodDeclaration(Syntax.Type(returnType.Name), Syntax.Identifier(function.Name))
                .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword))
                .WithAttributeLists(List(attributes))
                .WithParameterList(Syntax.Parameters(parameters.Select(static p => p.ToSyntax())))
                .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
                .WithDocumentation(function.Documentation),
            function);
    }

    private static FieldDeclarationSyntax Field(string type, string name, SyntaxKind accessibility) =>
        FieldDeclaration(VariableDeclaration(Syntax.Type(type)).WithVariables(SingletonSeparatedList(VariableDeclarator(Syntax.Identifier(name)))))
            .WithModifiers(Syntax.Modifiers(accessibility));

    private static string GetBufferName(string fieldName) => $"__{fieldName}_Buffer";

    // "_" and the name starting with a lower-case letter, e.g. _flag for Flag, made unique among the members of the structure.
    private static string GetBackingFieldName(string name, HashSet<string> names)
    {
        string candidate = "_" + char.ToLowerInvariant(name[0]) + name[1..];
        while (!names.Add(candidate))
        {
            candidate += "_";
        }

        return candidate;
    }

    private static StructDeclarationSyntax InlineArray(string name, string elementType, int length) =>
        StructDeclaration(Syntax.Identifier(name))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
            .WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute(
                "global::System.Runtime.CompilerServices.InlineArray", length.ToString(CultureInfo.InvariantCulture)))))
            .WithMembers(SingletonList<MemberDeclarationSyntax>(Field(elementType, "_element0", SyntaxKind.PrivateKeyword)))
            .WithDocumentation($"A fixed-size buffer of {length} elements.");

    private static MemberDeclarationSyntax ParseMember(string text)
    {
        var member = ParseMemberDeclaration(text, options: Syntax.ParseOptions);
        if (member is null || member.ContainsDiagnostics)
        {
            throw new InvalidOperationException($"Invalid member generated: {text}");
        }

        return member;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Module '{Module}': skipping the constant '{Name}', whose value is not supported")]
    private static partial void LogUnsupportedConstant(ILogger logger, string module, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Module '{Module}': skipping the function '{Name}', which has no DLL entry point")]
    private static partial void LogMissingDll(ILogger logger, string module, string name);
}
