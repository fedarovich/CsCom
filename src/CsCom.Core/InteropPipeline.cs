using CsCom.CodeGeneration;
using CsCom.Model;
using CsCom.Transformations;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom;

/// <summary>
/// The processing pipeline: reads a type library into the model, transforms the model, generates the Roslyn syntax trees and transforms them.
/// </summary>
public sealed class InteropPipeline
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="InteropPipeline"/> class with the built-in transformers.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public InteropPipeline(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// The transformers applied to the model, in order. By default, aliases are resolved and names are sanitized.
    /// </summary>
    public IList<IModelTransformer> ModelTransformers { get; } = new List<IModelTransformer> { new AliasResolver(), new NameSanitizer() };

    /// <summary>
    /// The transformers applied to the generated syntax trees, in order.
    /// </summary>
    public IList<ISyntaxTransformer> SyntaxTransformers { get; } = new List<ISyntaxTransformer>();

    /// <summary>
    /// Reads the type library into the model.
    /// </summary>
    public TypeLibrary ReadTypeLibrary(string path) => new TypeLibraryReader(_loggerFactory.CreateLogger<TypeLibraryReader>()).Read(path);

    /// <summary>
    /// Transforms the model and generates the C# files.
    /// </summary>
    public IReadOnlyList<GeneratedDocument> GenerateCode(TypeLibrary library, CodeGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);

        var context = new TransformationContext { Options = options, Logger = _loggerFactory.CreateLogger<InteropPipeline>() };
        foreach (var transformer in ModelTransformers)
        {
            transformer.Transform(library, context);
        }

        IReadOnlyList<GeneratedDocument> documents = new CSharpEmitter(_loggerFactory.CreateLogger<CSharpEmitter>()).Emit(library, options);
        foreach (var transformer in SyntaxTransformers)
        {
            documents = transformer.Transform(documents, context);
        }

        return documents;
    }
}
