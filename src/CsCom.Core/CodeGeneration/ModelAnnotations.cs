using System.Globalization;
using Microsoft.CodeAnalysis;

namespace CsCom.CodeGeneration;

/// <summary>
/// The role of a syntax node generated for a model element. An element can have several nodes with different roles, e.g. an
/// interface has its declaration, its extensions class, and the handler interface and the sink class of its events.
/// </summary>
public enum SyntaxRole
{
    /// <summary>
    /// The declaration of the element itself: of a type, a method, a parameter, an enum member, a structure field, a constant
    /// or a dispinterface property. For a structure field exposed by a property, the property.
    /// </summary>
    Declaration,

    /// <summary>The extensions class of an interface, which gives its accessors property syntax.</summary>
    Extensions,

    /// <summary>
    /// A property or an indexer that calls property accessor methods (of an extensions class or a dispinterface wrapper), or a
    /// property of an event arguments class that holds an argument (a parameter).
    /// </summary>
    Property,

    /// <summary>The private field that stores a structure field exposed by a property.</summary>
    BackingField,

    /// <summary>The inline array type of a fixed-size array field of a structure.</summary>
    InlineArray,

    /// <summary>The <c>GetEnumerator</c> method generated for a <c>_NewEnum</c> member.</summary>
    Enumerator,

    /// <summary>The handler interface of an event interface, and its methods.</summary>
    EventHandler,

    /// <summary>The sink class of an event interface.</summary>
    EventSink,

    /// <summary>The delegate of an event.</summary>
    EventDelegate,

    /// <summary>The event arguments class of an event.</summary>
    EventArgs,

    /// <summary>The .NET event or the <see cref="IObservable{T}"/> property of the sink class for an event.</summary>
    Event,

    /// <summary>
    /// An overload of a method that omits optional parameters whose default values C# cannot express, generated with the
    /// <c>OverloadsForDefaultValues</c> option.
    /// </summary>
    Overload,
}

/// <summary>
/// A model element and the role of a syntax node generated for it.
/// </summary>
/// <param name="Element">
/// The element: a <c>TypeDefinition</c>, <c>MethodDefinition</c>, <c>ParameterDefinition</c>, <c>FieldDefinition</c>,
/// <c>EnumMember</c>, <c>ConstantDefinition</c> or <c>DispatchPropertyDefinition</c>.
/// </param>
/// <param name="Role">The role of the node.</param>
public sealed record ModelElement(object Element, SyntaxRole Role);

/// <summary>
/// Associates the generated syntax nodes with the model elements they were generated for, through syntax annotations of the
/// kind <see cref="AnnotationKind"/>.
/// </summary>
/// <remarks>
/// The annotations are kept when nodes are changed with the <c>With…</c> methods or by syntax rewriters, but not by nodes that
/// are created anew: use <see cref="Annotate"/> to associate them with the model.
/// </remarks>
public sealed class ModelAnnotations
{
    /// <summary>The kind of the syntax annotations that refer to model elements.</summary>
    public const string AnnotationKind = "CsCom.Model";

    private readonly Dictionary<object, Dictionary<SyntaxRole, SyntaxAnnotation>> _annotations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ModelElement> _elements = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the annotation that associates nodes with an element and a role, creating it if needed.
    /// </summary>
    public SyntaxAnnotation GetAnnotation(object element, SyntaxRole role = SyntaxRole.Declaration)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!_annotations.TryGetValue(element, out var roles))
        {
            roles = [];
            _annotations.Add(element, roles);
        }

        if (!roles.TryGetValue(role, out var annotation))
        {
            string id = _elements.Count.ToString(CultureInfo.InvariantCulture);
            annotation = new SyntaxAnnotation(AnnotationKind, id);
            roles.Add(role, annotation);
            _elements.Add(id, new ModelElement(element, role));
        }

        return annotation;
    }

    /// <summary>
    /// Associates a node with an element and a role.
    /// </summary>
    public TNode Annotate<TNode>(TNode node, object element, SyntaxRole role = SyntaxRole.Declaration)
        where TNode : SyntaxNode
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.WithAdditionalAnnotations(GetAnnotation(element, role));
    }

    /// <summary>
    /// Gets the element a node is associated with, or <see langword="null"/> if it is not associated with the model.
    /// </summary>
    public ModelElement? GetElement(SyntaxNode node) => GetElements(node) is [var first, ..] ? first : null;

    /// <summary>
    /// Gets the elements a node is associated with, e.g. the getter and the setter of a property.
    /// </summary>
    public IReadOnlyList<ModelElement> GetElements(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return
        [
            .. node.GetAnnotations(AnnotationKind)
                .Select(a => a.Data is { } id && _elements.TryGetValue(id, out var element) ? element : null)
                .OfType<ModelElement>(),
        ];
    }

    /// <summary>
    /// Gets the nodes of a syntax tree that are associated with an element.
    /// </summary>
    /// <param name="root">The root of the tree.</param>
    /// <param name="element">The element.</param>
    /// <param name="role">The role of the nodes; all roles when <see langword="null"/>.</param>
    public IEnumerable<SyntaxNode> GetNodes(SyntaxNode root, object element, SyntaxRole? role = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(element);
        if (!_annotations.TryGetValue(element, out var roles))
        {
            return [];
        }

        return roles
            .Where(r => role is null || r.Key == role)
            .SelectMany(r => root.GetAnnotatedNodes(r.Value));
    }
}
