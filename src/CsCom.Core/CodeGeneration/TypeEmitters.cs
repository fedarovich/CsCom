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
        var members = definition.Members.Select(static member =>
            EnumMemberDeclaration(Syntax.Identifier(member.Name))
                .WithEqualsValue(EqualsValueClause(Syntax.Expression(Syntax.IntegerLiteral(member.Value))))
                .WithDocumentation(member.Documentation));

        return EnumDeclaration(Syntax.Identifier(definition.Name))
            .WithModifiers(Syntax.Modifiers(_context.Accessibility))
            .WithMembers(SeparatedList(members))
            .WithDocumentation(definition.Documentation);
    }

    public StructDeclarationSyntax EmitRecord(RecordDefinition definition, string @namespace)
    {
        var mapper = new TypeMapper(_context, @namespace);
        var members = new List<MemberDeclarationSyntax>();
        bool isUnsafe = false;
        var fieldOffset = Syntax.AttributeList(Syntax.Attribute($"{Syntax.InteropNamespace}.FieldOffset", "0"));

        foreach (var field in definition.Fields)
        {
            FieldDeclarationSyntax declaration;
            if (field.Type is FixedArrayTypeReference array)
            {
                // Inline arrays rather than fixed buffers, so that the elements can be accessed without an unsafe context.
                var element = mapper.MapField(array.ElementType);
                string bufferName = $"__{field.Name}_Buffer";
                members.Add(InlineArray(bufferName, element.Name, array.Length));
                declaration = Field(bufferName, field.Name);
                isUnsafe |= element.IsPointer;
            }
            else
            {
                var type = mapper.MapField(field.Type);
                declaration = Field(type.Name, field.Name);
                isUnsafe |= type.IsPointer;
            }

            if (definition.IsUnion)
            {
                declaration = declaration.WithAttributeLists(SingletonList(fieldOffset));
            }

            members.Add(declaration.WithDocumentation(field.Documentation));
        }

        string layout = $"{Syntax.InteropNamespace}.LayoutKind.{(definition.IsUnion ? "Explicit" : "Sequential")}";
        string layoutArguments = definition.Alignment > 0 ? $"{layout}, Pack = {definition.Alignment}" : layout;

        var modifiers = new List<SyntaxKind> { _context.Accessibility };
        if (isUnsafe)
        {
            modifiers.Add(SyntaxKind.UnsafeKeyword);
        }

        modifiers.Add(SyntaxKind.PartialKeyword);
        return StructDeclaration(Syntax.Identifier(definition.Name))
            .WithModifiers(Syntax.Modifiers([.. modifiers]))
            .WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute($"{Syntax.InteropNamespace}.StructLayout", layoutArguments))))
            .WithMembers(List(members))
            .WithDocumentation(definition.Documentation);
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

        return ClassDeclaration(Syntax.Identifier(definition.Name))
            .WithModifiers(Syntax.Modifiers(_context.Accessibility, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword))
            .WithMembers(List(members))
            .WithDocumentation(definition.Documentation ?? $"The {definition.OriginalName} class.");
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
                members.Add(declaration);
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
        return ClassDeclaration(Syntax.Identifier(definition.Name))
            .WithModifiers(Syntax.Modifiers([.. modifiers]))
            .WithMembers(List(members))
            .WithDocumentation(definition.Documentation);
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
        var returnType = signatures.Mapper.MapValue(function.ReturnType);
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

        return MethodDeclaration(Syntax.Type(returnType.Name), Syntax.Identifier(function.Name))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword, SyntaxKind.StaticKeyword, SyntaxKind.PartialKeyword))
            .WithAttributeLists(List(attributes))
            .WithParameterList(Syntax.Parameters(parameters.Select(static p => p.ToSyntax())))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken))
            .WithDocumentation(function.Documentation);
    }

    private static FieldDeclarationSyntax Field(string type, string name) =>
        FieldDeclaration(VariableDeclaration(Syntax.Type(type)).WithVariables(SingletonSeparatedList(VariableDeclarator(Syntax.Identifier(name)))))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword));

    private static StructDeclarationSyntax InlineArray(string name, string elementType, int length) =>
        StructDeclaration(Syntax.Identifier(name))
            .WithModifiers(Syntax.Modifiers(SyntaxKind.PublicKeyword))
            .WithAttributeLists(SingletonList(Syntax.AttributeList(Syntax.Attribute(
                "global::System.Runtime.CompilerServices.InlineArray", length.ToString(CultureInfo.InvariantCulture)))))
            .WithMembers(SingletonList<MemberDeclarationSyntax>(
                Field(elementType, "_element0").WithModifiers(Syntax.Modifiers(SyntaxKind.PrivateKeyword))))
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
