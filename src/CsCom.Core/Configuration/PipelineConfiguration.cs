using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;
using CsCom.Transformations;

namespace CsCom.Configuration;

/// <summary>
/// The transformers added by a configuration file.
/// </summary>
/// <remarks>
/// The file has a <c>CsCom</c> root element with optional <c>ModelTransformers</c> and <c>SyntaxTransformers</c> sections.
/// Each element of a section adds a transformer registered under its name in the <see cref="TransformerRegistry"/>; the
/// transformers run in the order of the elements.
/// <code language="xml">
/// <![CDATA[
/// <CsCom>
///   <ModelTransformers>
///     <RenameType Name="IShape" NewName="Shape" />
///   </ModelTransformers>
/// </CsCom>
/// ]]>
/// </code>
/// </remarks>
public sealed class PipelineConfiguration
{
    /// <summary>The name of the root element.</summary>
    public const string RootElementName = "CsCom";

    private const string ModelTransformersElementName = "ModelTransformers";
    private const string SyntaxTransformersElementName = "SyntaxTransformers";

    /// <summary>
    /// Initializes a new instance of the <see cref="PipelineConfiguration"/> class.
    /// </summary>
    public PipelineConfiguration(IReadOnlyList<IModelTransformer> modelTransformers, IReadOnlyList<ISyntaxTransformer> syntaxTransformers)
    {
        ModelTransformers = modelTransformers;
        SyntaxTransformers = syntaxTransformers;
    }

    private delegate bool TryGetFactory<T>(string name, [NotNullWhen(true)] out Func<ConfigurationElement, T>? factory);

    /// <summary>Gets a configuration without transformers.</summary>
    public static PipelineConfiguration Empty { get; } = new([], []);

    /// <summary>
    /// Gets the model transformers, which run after the aliases are resolved and before the names are sanitized.
    /// </summary>
    public IReadOnlyList<IModelTransformer> ModelTransformers { get; }

    /// <summary>Gets the syntax transformers.</summary>
    public IReadOnlyList<ISyntaxTransformer> SyntaxTransformers { get; }

    /// <summary>
    /// Loads a configuration file.
    /// </summary>
    /// <param name="path">The path to the file.</param>
    /// <param name="registry">The transformers that can be added.</param>
    /// <exception cref="CsComException">The file is missing or not valid.</exception>
    public static PipelineConfiguration Load(string path, TransformerRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(registry);
        if (!File.Exists(path))
        {
            throw new CsComException($"The configuration file '{path}' was not found.");
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException e)
        {
            throw new CsComException($"'{path}' is not a valid XML file: {e.Message}", e);
        }

        return Parse(document, registry, path);
    }

    /// <summary>
    /// Creates the transformers of a configuration document.
    /// </summary>
    /// <param name="document">The document; load it with <see cref="LoadOptions.SetLineInfo"/> to report the location of errors.</param>
    /// <param name="registry">The transformers that can be added.</param>
    /// <param name="path">The path to the file, reported with errors.</param>
    /// <exception cref="CsComException">The document is not valid.</exception>
    public static PipelineConfiguration Parse(XDocument document, TransformerRegistry registry, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(registry);
        if (document.Root is null)
        {
            throw ConfigurationElement.CreateError(document, path, "The configuration has no root element.");
        }

        var root = new ConfigurationElement(document.Root, path);
        if (document.Root.Name != RootElementName)
        {
            throw root.CreateError($"The root element must be <{RootElementName}> without a namespace.");
        }

        var modelTransformers = CreateTransformers<IModelTransformer>(root, ModelTransformersElementName, "model", registry.TryGetModelTransformer, registry.ModelTransformerNames);
        var syntaxTransformers = CreateTransformers<ISyntaxTransformer>(root, SyntaxTransformersElementName, "syntax", registry.TryGetSyntaxTransformer, registry.SyntaxTransformerNames);
        root.ThrowIfUnused();
        return new PipelineConfiguration(modelTransformers, syntaxTransformers);
    }

    private static List<T> CreateTransformers<T>(
        ConfigurationElement root,
        string sectionName,
        string kind,
        TryGetFactory<T> tryGetFactory,
        IReadOnlyCollection<string> names)
    {
        var sections = root.Elements(sectionName).ToList();
        if (sections.Count > 1)
        {
            throw sections[1].CreateError($"<{sectionName}> can only be specified once.");
        }

        var transformers = new List<T>();
        foreach (var element in sections.SelectMany(static s => s.Elements()))
        {
            if (element.Element.Name.Namespace != XNamespace.None || !tryGetFactory(element.Name, out var factory))
            {
                throw element.CreateError(names.Count == 0
                    ? $"Unknown {kind} transformer <{element.Name}>. No {kind} transformers are available."
                    : $"Unknown {kind} transformer <{element.Name}>. Available: {string.Join(", ", names.Order(StringComparer.Ordinal))}.");
            }

            try
            {
                transformers.Add(factory(element));
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                throw element.CreateError(e.Message, e);
            }

            element.ThrowIfUnused();
        }

        return transformers;
    }
}
