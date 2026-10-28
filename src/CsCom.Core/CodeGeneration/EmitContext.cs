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
    public EmitContext(TypeLibrary library, CodeGenerationOptions options, ModelAnnotations annotations, ILogger logger, EmitScope? scope = null)
    {
        Library = library;
        Options = options;
        Annotations = annotations;
        Logger = logger;
        Scope = scope;
        Support = "global::" + options.SupportNamespace;
    }

    public TypeLibrary Library { get; }

    /// <summary>The libraries generated together with <see cref="Library"/>, if any.</summary>
    public EmitScope? Scope { get; }

    public CodeGenerationOptions Options { get; }

    /// <summary>Associates the generated syntax nodes with the model elements.</summary>
    public ModelAnnotations Annotations { get; }

    public ILogger Logger { get; }

    /// <summary>The fully qualified support namespace, e.g. <c>global::CsCom.Runtime</c>.</summary>
    public string Support { get; }

    /// <summary>The accessibility of the generated types.</summary>
    public SyntaxKind Accessibility => Options.SupportCode == SupportCodeMode.Internal ? SyntaxKind.InternalKeyword : SyntaxKind.PublicKeyword;

    /// <summary>
    /// Gets the namespace of a type: its library's namespace when several libraries are generated together, otherwise
    /// <see cref="CodeGenerationOptions.Namespace"/>, with a nested namespace for the types imported from other libraries.
    /// </summary>
    public string GetNamespace(TypeDefinition definition)
    {
        if (Scope is not null && Scope.Namespaces.TryGetValue(definition, out string? @namespace))
        {
            return @namespace;
        }

        return definition.ImportedFrom is null ? Options.Namespace : $"{Options.Namespace}.{NameSanitizer.ToIdentifier(definition.ImportedFrom.Name)}";
    }

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

    /// <summary>The name of the class that contains the marshallers of the multidimensional <c>SAFEARRAY</c> parameters.</summary>
    public const string SafeArrayMarshallersClassName = "CsComSafeArrayMarshallers";

    /// <summary>
    /// The marshallers of the multidimensional <c>SAFEARRAY</c> parameters, which are generated for their element types: the
    /// name of the nested class, the fully qualified element type and the rank.
    /// </summary>
    public SortedDictionary<string, (string ElementType, int Rank)> SafeArrayMarshallers { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the marshaller of the arrays of the specified fully qualified element type and rank, which is generated into
    /// <see cref="SafeArrayMarshallersClassName"/>.
    /// </summary>
    /// <returns>The fully qualified name of the marshaller.</returns>
    public string GetSafeArrayMarshaller(string elementType, int rank)
    {
        string element = elementType.TrimEnd('?');
        element = element.StartsWith("global::", StringComparison.Ordinal) ? element["global::".Length..] : element;
        string name = $"{NameSanitizer.ToIdentifier(element.Replace('.', '_'))}_{rank}D";
        SafeArrayMarshallers.TryAdd(name, (elementType, rank));
        return $"global::{Options.Namespace}.{SafeArrayMarshallersClassName}.{name}";
    }
}
