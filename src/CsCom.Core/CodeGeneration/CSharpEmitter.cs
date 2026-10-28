using CsCom.Model;
using CsCom.Transformations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CsCom.CodeGeneration;

/// <summary>
/// Generates the C# syntax trees of the interop types of a <see cref="TypeLibrary"/>.
/// </summary>
public sealed class CSharpEmitter
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CSharpEmitter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public CSharpEmitter(ILogger<CSharpEmitter>? logger = null)
    {
        _logger = logger ?? NullLogger<CSharpEmitter>.Instance;
    }

    /// <summary>
    /// Generates the C# files for the types of the library.
    /// </summary>
    /// <param name="library">The library, usually after the model transformers have been applied.</param>
    /// <param name="options">The options.</param>
    /// <returns>The generated files.</returns>
    public IReadOnlyList<GeneratedDocument> Emit(TypeLibrary library, CodeGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);

        var context = new EmitContext(library, options, _logger);
        var interfaces = new InterfaceEmitter(context);
        var wrappers = new DispatchWrapperEmitter(context);
        var events = new EventEmitter(context);
        var types = new TypeEmitters(context);

        var units = new List<Unit>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in library.Types)
        {
            string @namespace = context.GetNamespace(type);
            List<MemberDeclarationSyntax> members = type switch
            {
                EnumDefinition enumDefinition => [types.EmitEnum(enumDefinition)],
                RecordDefinition record => [types.EmitRecord(record, @namespace)],
                InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } dispatch => [wrappers.Emit(dispatch, @namespace), .. events.Emit(dispatch, @namespace)],
                InterfaceDefinition @interface => [.. interfaces.Emit(@interface, @namespace), .. events.Emit(@interface, @namespace)],
                CoClassDefinition coClass => [types.EmitCoClass(coClass, @namespace)],
                ModuleDefinition module => types.EmitModule(module, @namespace) is { } declaration ? [declaration] : [],
                _ => [],
            };

            if (members.Count == 0)
            {
                continue;
            }

            string folder = type.ImportedFrom is null ? string.Empty : NameSanitizer.ToIdentifier(type.ImportedFrom.Name) + "/";
            units.Add(new Unit(UniquePath(folder + type.Name, paths), @namespace, members, default, null));
        }

        if (options.SupportCode != SupportCodeMode.Package)
        {
            foreach (var (fileName, root) in SupportCodeProvider.GetFiles(options))
            {
                var @namespace = root.Members.OfType<BaseNamespaceDeclarationSyntax>().Single();
                units.Add(new Unit("ComSupport/" + fileName, options.SupportNamespace, [.. @namespace.Members], root.Usings, root));
            }
        }

        string header = GetHeader(library, options);
        if (options.SingleFile)
        {
            return [BuildSingleFile(units, options.SingleFileName, header)];
        }

        return units.Select(unit => unit.Original is { } original
                ? new GeneratedDocument(unit.Path, SyntaxFormatter.AddHeader(original, header))
                : new GeneratedDocument(unit.Path, SyntaxFormatter.Format(CompilationUnit().WithMembers(SingletonList<MemberDeclarationSyntax>(
                    FileScopedNamespaceDeclaration(ParseName(unit.Namespace)).WithMembers(List(unit.Members)))), header)))
            .ToList();
    }

    private static GeneratedDocument BuildSingleFile(List<Unit> units, string fileName, string header)
    {
        var namespaces = units.GroupBy(static u => u.Namespace).ToList();
        MemberDeclarationSyntax[] members;
        if (namespaces.Count == 1 && units.All(static u => u.Usings.Count == 0))
        {
            members = [FileScopedNamespaceDeclaration(ParseName(namespaces[0].Key)).WithMembers(List(units.SelectMany(static u => u.Members)))];
        }
        else
        {
            members = namespaces.Select(group =>
            {
                var usings = group.SelectMany(static u => u.Usings)
                    .GroupBy(static u => u.ToString())
                    .Select(static g => g.First().WithoutTrivia())
                    .OrderBy(static u => u.Name?.ToString(), StringComparer.Ordinal);
                return (MemberDeclarationSyntax)NamespaceDeclaration(ParseName(group.Key))
                    .WithUsings(List(usings))
                    .WithMembers(List(group.SelectMany(static u => u.Members)));
            }).ToArray();
        }

        return new GeneratedDocument(fileName, SyntaxFormatter.Format(CompilationUnit().WithMembers(List(members)), header));
    }

    private static string GetHeader(TypeLibrary library, CodeGenerationOptions options) =>
        $"""
        // <auto-generated>
        //     This code was generated by CsCom {options.ToolVersion} from the type library {library.Name} {library.MajorVersion}.{library.MinorVersion} ({library.Guid:B}).
        //     Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.
        // </auto-generated>

        #nullable enable


        """;

    private static string UniquePath(string name, HashSet<string> paths)
    {
        string path = name + ".cs";
        for (int i = 2; !paths.Add(path); i++)
        {
            path = $"{name}_{i}.cs";
        }

        return path;
    }

    private sealed record Unit(string Path, string Namespace, List<MemberDeclarationSyntax> Members, SyntaxList<UsingDirectiveSyntax> Usings, CompilationUnitSyntax? Original);
}
