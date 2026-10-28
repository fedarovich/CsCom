using CsCom.CodeGeneration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Transforms the <see cref="TypeLibrary"/> model before the code is generated.
/// </summary>
public interface IModelTransformer
{
    /// <summary>
    /// Transforms the library in place.
    /// </summary>
    void Transform(TypeLibrary library, TransformationContext context);
}

/// <summary>
/// Transforms the generated Roslyn syntax trees before they are written. The nodes generated for the model elements can be
/// found with <see cref="TransformationContext.Annotations"/>.
/// </summary>
public interface ISyntaxTransformer
{
    /// <summary>
    /// Transforms the generated documents. Documents can be changed, added or removed.
    /// </summary>
    IReadOnlyList<GeneratedDocument> Transform(IReadOnlyList<GeneratedDocument> documents, TransformationContext context);
}

/// <summary>
/// The context passed to the transformers.
/// </summary>
public sealed class TransformationContext
{
    /// <summary>The code generation options.</summary>
    public required CodeGenerationOptions Options { get; init; }

    /// <summary>The logger.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// The model, which is also passed to the model transformers; for the syntax transformers, the model the code was
    /// generated from, e.g. to find the members of a type.
    /// </summary>
    public TypeLibrary? Library { get; init; }

    /// <summary>
    /// Associates the generated syntax nodes with the model elements; empty for the model transformers, which run before the
    /// code is generated.
    /// </summary>
    public ModelAnnotations Annotations { get; init; } = new();
}
