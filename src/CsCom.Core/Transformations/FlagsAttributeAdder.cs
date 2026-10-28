using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Adds <see cref="FlagsAttribute"/> to the enums whose names match a regular expression; the <c>AddFlagsAttribute</c> element
/// of configuration files.
/// </summary>
/// <remarks>
/// Type libraries do not record whether the members of an enum are bit flags. The enums are found through the
/// <see cref="TransformationContext.Annotations"/>, and the enums that already have the attribute are not changed.
/// </remarks>
public sealed partial class FlagsAttributeAdder : ISyntaxTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FlagsAttributeAdder"/> class.
    /// </summary>
    /// <param name="type">Matches the names of the enums.</param>
    public FlagsAttributeAdder(NameSelector type)
    {
        Type = type;
    }

    /// <summary>Gets the selector of the names of the enums.</summary>
    public NameSelector Type { get; }

    /// <inheritdoc/>
    public IReadOnlyList<GeneratedDocument> Transform(IReadOnlyList<GeneratedDocument> documents, TransformationContext context)
    {
        var result = SyntaxEditing.AddAttribute(
            documents,
            root => root.GetAnnotatedNodes(ModelAnnotations.AnnotationKind)
                .OfType<EnumDeclarationSyntax>()
                .Where(e => context.Annotations.GetElements(e).Any(m => m is { Element: EnumDefinition definition, Role: SyntaxRole.Declaration } && Type.IsMatch(definition.Name))),
            Syntax.AttributeList(Syntax.Attribute("global::System.Flags")),
            static declaration => SyntaxEditing.HasAttribute(declaration, "System", "Flags"),
            declaration => LogAdding(context.Logger, ((EnumDeclarationSyntax)declaration).Identifier.ValueText),
            out bool matched);

        if (!matched)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(("Type", Type)));
        }

        return result;
    }

    internal static FlagsAttributeAdder Create(ConfigurationElement element) => new(element.GetRequiredSelector("Type"));

    [LoggerMessage(Level = LogLevel.Debug, Message = "AddFlagsAttribute: adding [Flags] to '{Name}'")]
    private static partial void LogAdding(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AddFlagsAttribute: no enum matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
