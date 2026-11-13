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

    private readonly Dictionary<string, HashSet<string>> _typeNames = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the name of a type that is generated next to <paramref name="owner"/> in its namespace, e.g. its extensions class
    /// or its event sink: <paramref name="name"/>, with trailing <c>_</c>s while a type of the library or another such type
    /// has the name. The name is taken, so it is requested once per generated type.
    /// </summary>
    public string GetHelperTypeName(TypeDefinition owner, string name) => GetHelperTypeName(GetNamespace(owner), name);

    private string GetHelperTypeName(string @namespace, string name)
    {
        if (!_typeNames.TryGetValue(@namespace, out var names))
        {
            names = new HashSet<string>(Library.Types.Where(t => GetNamespace(t) == @namespace).Select(static t => t.Name), StringComparer.Ordinal);
            _typeNames.Add(@namespace, names);
        }

        while (!names.Add(name))
        {
            name += "_";
        }

        return name;
    }

    private string? _safeArrayMarshallersClassName;

    /// <summary>
    /// The name of the class that contains the marshallers of the multidimensional <c>SAFEARRAY</c> parameters:
    /// <c>CsComSafeArrayMarshallers</c>, with trailing <c>_</c>s while a type of the namespace has the name.
    /// </summary>
    public string SafeArrayMarshallersClassName => _safeArrayMarshallersClassName ??= GetHelperTypeName(Options.Namespace, "CsComSafeArrayMarshallers");

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
