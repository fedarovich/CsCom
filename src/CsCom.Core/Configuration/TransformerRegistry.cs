using System.Diagnostics.CodeAnalysis;
using System.Xml;
using CsCom.Transformations;

namespace CsCom.Configuration;

/// <summary>
/// The transformers that can be added by configuration files, by the names of their elements.
/// </summary>
public sealed class TransformerRegistry
{
    private readonly Dictionary<string, Func<ConfigurationElement, IModelTransformer>> _modelTransformers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<ConfigurationElement, ISyntaxTransformer>> _syntaxTransformers = new(StringComparer.Ordinal);

    /// <summary>Gets the names of the model transformers.</summary>
    public IReadOnlyCollection<string> ModelTransformerNames => _modelTransformers.Keys;

    /// <summary>Gets the names of the syntax transformers.</summary>
    public IReadOnlyCollection<string> SyntaxTransformerNames => _syntaxTransformers.Keys;

    /// <summary>
    /// Creates a registry with the configurable transformers built into CsCom: the model transformers <c>RenameType</c>
    /// (<see cref="TypeRenamer"/>), <c>RenameMember</c> (<see cref="MemberRenamer"/>), <c>SetMarshalling</c>
    /// (<see cref="MarshallingSetter"/>), <c>RemoveEnumPrefix</c> (<see cref="EnumPrefixRemover"/>), <c>ExcludeType</c>
    /// (<see cref="TypeExcluder"/>), <c>SetSafeArrayRank</c> (<see cref="SafeArrayRankSetter"/>), <c>SetType</c>
    /// (<see cref="TypeSetter"/>) and <c>RenameMembersToPascalCase</c> (<see cref="MemberPascalCaser"/>), and the syntax
    /// transformers <c>AddFlagsAttribute</c> (<see cref="FlagsAttributeAdder"/>) and <c>AddAttribute</c>
    /// (<see cref="AttributeAdder"/>).
    /// </summary>
    public static TransformerRegistry CreateDefault() => new TransformerRegistry()
        .AddModelTransformer("RenameType", TypeRenamer.Create)
        .AddModelTransformer("RenameMember", MemberRenamer.Create)
        .AddModelTransformer("SetMarshalling", MarshallingSetter.Create)
        .AddModelTransformer("RemoveEnumPrefix", EnumPrefixRemover.Create)
        .AddModelTransformer("ExcludeType", TypeExcluder.Create)
        .AddModelTransformer("SetSafeArrayRank", SafeArrayRankSetter.Create)
        .AddModelTransformer("SetType", TypeSetter.Create)
        .AddModelTransformer("RenameMembersToPascalCase", MemberPascalCaser.Create)
        .AddSyntaxTransformer("AddFlagsAttribute", FlagsAttributeAdder.Create)
        .AddSyntaxTransformer("AddAttribute", AttributeAdder.Create);

    /// <summary>
    /// Registers a model transformer.
    /// </summary>
    /// <param name="name">The name of its element in the <c>ModelTransformers</c> section.</param>
    /// <param name="factory">Creates the transformer from its element.</param>
    /// <returns>The registry.</returns>
    /// <exception cref="ArgumentException">The name is not a valid XML name or is already registered.</exception>
    public TransformerRegistry AddModelTransformer(string name, Func<ConfigurationElement, IModelTransformer> factory)
    {
        Add(_modelTransformers, name, factory);
        return this;
    }

    /// <summary>
    /// Registers a syntax transformer.
    /// </summary>
    /// <param name="name">The name of its element in the <c>SyntaxTransformers</c> section.</param>
    /// <param name="factory">Creates the transformer from its element.</param>
    /// <returns>The registry.</returns>
    /// <exception cref="ArgumentException">The name is not a valid XML name or is already registered.</exception>
    public TransformerRegistry AddSyntaxTransformer(string name, Func<ConfigurationElement, ISyntaxTransformer> factory)
    {
        Add(_syntaxTransformers, name, factory);
        return this;
    }

    internal bool TryGetModelTransformer(string name, [NotNullWhen(true)] out Func<ConfigurationElement, IModelTransformer>? factory) =>
        _modelTransformers.TryGetValue(name, out factory);

    internal bool TryGetSyntaxTransformer(string name, [NotNullWhen(true)] out Func<ConfigurationElement, ISyntaxTransformer>? factory) =>
        _syntaxTransformers.TryGetValue(name, out factory);

    private static void Add<T>(Dictionary<string, T> factories, string name, T factory)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(factory);
        try
        {
            XmlConvert.VerifyNCName(name);
        }
        catch (XmlException e)
        {
            throw new ArgumentException($"'{name}' is not a valid XML element name.", nameof(name), e);
        }

        if (!factories.TryAdd(name, factory))
        {
            throw new ArgumentException($"A transformer named '{name}' is already registered.", nameof(name));
        }
    }
}
