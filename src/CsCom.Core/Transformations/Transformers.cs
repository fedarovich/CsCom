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
/// Transforms the generated Roslyn syntax trees before they are written.
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
}
