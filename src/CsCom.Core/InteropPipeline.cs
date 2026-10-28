using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using CsCom.Transformations;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom;

/// <summary>
/// The processing pipeline: reads a type library into the model, transforms the model, generates the Roslyn syntax trees and transforms them.
/// </summary>
public sealed partial class InteropPipeline
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="InteropPipeline"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public InteropPipeline(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    /// <summary>
    /// The transformers applied to the model, in order, before those of the configuration. The aliases are always resolved
    /// before them, and the names are always sanitized after them.
    /// </summary>
    public IList<IModelTransformer> ModelTransformers { get; } = new List<IModelTransformer>();

    /// <summary>
    /// The transformers applied to the generated syntax trees, in order, before those of the configuration.
    /// </summary>
    public IList<ISyntaxTransformer> SyntaxTransformers { get; } = new List<ISyntaxTransformer>();

    /// <summary>
    /// The transformers that can be added by configuration files; the configurable transformers built into CsCom by default.
    /// </summary>
    public TransformerRegistry Registry { get; } = TransformerRegistry.CreateDefault();

    /// <summary>
    /// Reads the type library into the model.
    /// </summary>
    /// <param name="source">The type library: a file, a registered LIBID or a ProgID, see <see cref="TypeLibrarySource"/>.</param>
    public TypeLibrary ReadTypeLibrary(string source)
    {
        string path = TypeLibrarySource.Resolve(source);
        if (!TypeLibrarySource.IsFile(source))
        {
            LogResolved(_loggerFactory.CreateLogger<InteropPipeline>(), source, path);
        }

        return new TypeLibraryReader(_loggerFactory.CreateLogger<TypeLibraryReader>()).Read(path);
    }

    /// <summary>
    /// Transforms the model and generates the C# files.
    /// </summary>
    /// <param name="library">The model, which is transformed in place.</param>
    /// <param name="options">The code generation options.</param>
    /// <param name="configuration">The transformers added by a configuration file, applied after those of the pipeline.</param>
    public IReadOnlyList<GeneratedDocument> GenerateCode(TypeLibrary library, CodeGenerationOptions options, PipelineConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);
        configuration ??= PipelineConfiguration.Empty;

        var context = TransformModel(library, options, configuration);
        var annotations = new ModelAnnotations();
        IReadOnlyList<GeneratedDocument> documents = new CSharpEmitter(_loggerFactory.CreateLogger<CSharpEmitter>()).Emit(library, options, annotations);
        var syntaxContext = new TransformationContext { Options = context.Options, Logger = context.Logger, Library = library, Annotations = annotations };
        foreach (var transformer in SyntaxTransformers.Concat(configuration.SyntaxTransformers))
        {
            documents = transformer.Transform(documents, syntaxContext);
        }

        return documents;
    }

    /// <summary>
    /// Applies the model transformers: resolves the aliases, applies the transformers of the pipeline and of the configuration,
    /// and sanitizes the names.
    /// </summary>
    /// <param name="library">The model, which is transformed in place.</param>
    /// <param name="options">The code generation options.</param>
    /// <param name="configuration">The transformers added by a configuration file, applied after those of the pipeline.</param>
    /// <param name="sanitizeNames">
    /// Whether the names are made valid and unique C# identifiers; <see langword="false"/> keeps the names that the transformers see.
    /// </param>
    /// <returns>The context passed to the transformers.</returns>
    public TransformationContext TransformModel(TypeLibrary library, CodeGenerationOptions options, PipelineConfiguration? configuration = null, bool sanitizeNames = true)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);
        configuration ??= PipelineConfiguration.Empty;

        var context = new TransformationContext { Options = options, Logger = _loggerFactory.CreateLogger<InteropPipeline>(), Library = library };
        IEnumerable<IModelTransformer> modelTransformers = [new AliasResolver(), .. ModelTransformers, .. configuration.ModelTransformers];
        if (sanitizeNames)
        {
            modelTransformers = modelTransformers.Append(new NameSanitizer());
        }

        foreach (var transformer in modelTransformers)
        {
            transformer.Transform(library, context);
        }

        return context;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Resolved the type library '{Source}' to '{Path}'")]
    private static partial void LogResolved(ILogger logger, string source, string path);
}
