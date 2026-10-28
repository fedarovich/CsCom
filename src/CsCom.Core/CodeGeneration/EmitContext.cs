using CsCom.Model;
using CsCom.Transformations;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace CsCom.CodeGeneration;

/// <summary>
/// The state shared by the emitters.
/// </summary>
internal sealed class EmitContext
{
    public EmitContext(TypeLibrary library, CodeGenerationOptions options, ILogger logger)
    {
        Library = library;
        Options = options;
        Logger = logger;
        Support = "global::" + options.SupportNamespace;
    }

    public TypeLibrary Library { get; }

    public CodeGenerationOptions Options { get; }

    public ILogger Logger { get; }

    /// <summary>The fully qualified support namespace, e.g. <c>global::CsCom.Runtime</c>.</summary>
    public string Support { get; }

    /// <summary>The accessibility of the generated types.</summary>
    public SyntaxKind Accessibility => Options.SupportCode == SupportCodeMode.Internal ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword;

    public string GetNamespace(TypeDefinition definition) =>
        definition.ImportedFrom is null ? Options.Namespace : $"{Options.Namespace}.{NameSanitizer.ToIdentifier(definition.ImportedFrom.Name)}";

    /// <summary>
    /// Gets the name of the type as it can be used from <paramref name="currentNamespace"/>.
    /// </summary>
    public string GetTypeName(TypeDefinition definition, string currentNamespace) => GetTypeName(definition, definition.Name, currentNamespace);

    /// <summary>
    /// Gets the name of a type generated for <paramref name="definition"/> (e.g. its extensions class) as it can be used from <paramref name="currentNamespace"/>.
    /// </summary>
    public string GetTypeName(TypeDefinition definition, string name, string currentNamespace)
    {
        string @namespace = GetNamespace(definition);
        return @namespace == currentNamespace ? Syntax.Escape(name) : $"global::{@namespace}.{Syntax.Escape(name)}";
    }

    public static string GetExtensionsClassName(InterfaceDefinition definition) => definition.Name + "Extensions";
}
