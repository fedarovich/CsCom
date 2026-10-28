using System.Globalization;
using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Adds an attribute to the declarations generated for the types or members that match selectors; the <c>AddAttribute</c>
/// element of configuration files.
/// </summary>
/// <remarks>
/// The declarations are found through the <see cref="TransformationContext.Annotations"/>, with the specified
/// <see cref="SyntaxRole"/>: by default the declarations of the types or members themselves, but e.g. also the extensions
/// class (<see cref="SyntaxRole.Extensions"/>) or the event sink (<see cref="SyntaxRole.EventSink"/>) of an interface. The
/// declarations that already have the attribute are not changed.
/// </remarks>
public sealed partial class AttributeAdder : ISyntaxTransformer
{
    private readonly AttributeListSyntax _attributeList;
    private readonly HashSet<string> _attributes;

    /// <summary>
    /// Initializes a new instance of the <see cref="AttributeAdder"/> class.
    /// </summary>
    /// <param name="type">Matches the types.</param>
    /// <param name="member">Matches the names of the members; the types themselves when <see langword="null"/>.</param>
    /// <param name="role">The role of the declarations generated for the types or members.</param>
    /// <param name="attribute">The C# attribute without brackets, e.g. <c>System.Obsolete("Use IShape2")</c>.</param>
    /// <exception cref="ArgumentException">The attribute is not valid.</exception>
    public AttributeAdder(TypeSelector type, NameSelector? member, SyntaxRole role, string attribute)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(attribute);
        Type = type;
        Member = member;
        Role = role;
        Attribute = attribute;
        _attributeList = ParseAttributeList(attribute);
        _attributes = [.. _attributeList.Attributes.Select(Normalize)];
    }

    /// <summary>Gets the selector of the types.</summary>
    public TypeSelector Type { get; }

    /// <summary>Gets the selector of the members, or <see langword="null"/> for the types themselves.</summary>
    public NameSelector? Member { get; }

    /// <summary>Gets the role of the declarations.</summary>
    public SyntaxRole Role { get; }

    /// <summary>Gets the attribute.</summary>
    public string Attribute { get; }

    /// <inheritdoc/>
    public IReadOnlyList<GeneratedDocument> Transform(IReadOnlyList<GeneratedDocument> documents, TransformationContext context)
    {
        var library = context.Library ?? throw new InvalidOperationException("The syntax transformers need the model in the transformation context.");
        var types = library.Types.Where(Type.IsMatch);
        var elements = (Member is null ? types : types.SelectMany(t => ModelSelectors.GetMembers(t).Where(m => Member.IsMatch(m.Name)).Select(static m => m.Member))).ToList();

        var result = SyntaxEditing.AddAttribute(
            documents,
            root => elements.SelectMany(e => context.Annotations.GetNodes(root, e, Role)).OfType<MemberDeclarationSyntax>(),
            _attributeList,
            HasAttribute,
            declaration => LogAdding(context.Logger, Attribute, context.Annotations.GetElement(declaration)?.Element.ToString() ?? string.Empty),
            out bool matched);

        if (!matched)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(Type, ("Member", Member)), Role);
        }

        return result;
    }

    internal static AttributeAdder Create(ConfigurationElement element) => new(
        element.GetTypeSelector(required: true)!,
        element.GetSelector("Member"),
        element.GetEnum("Role", SyntaxRole.Declaration),
        element.GetRequiredAttribute("Attribute"));

    private static AttributeListSyntax ParseAttributeList(string attribute)
    {
        var unit = SyntaxFactory.ParseCompilationUnit($"[{attribute}] class C {{ }}", options: Syntax.ParseOptions);
        var error = unit.GetDiagnostics().FirstOrDefault(static d => d.Severity == DiagnosticSeverity.Error);
        if (error is not null || unit.Members is not [ClassDeclarationSyntax { AttributeLists: [{ Target: null } list] }])
        {
            string reason = error?.GetMessage(CultureInfo.InvariantCulture) ?? "a single attribute list without a target is expected";
            throw new ArgumentException($"'{attribute}' is not a valid attribute: {reason}", nameof(attribute));
        }

        return list.NormalizeWhitespace();
    }

    private static string Normalize(AttributeSyntax attribute) => attribute.NormalizeWhitespace().ToFullString();

    private bool HasAttribute(MemberDeclarationSyntax declaration)
    {
        var existing = declaration.AttributeLists.SelectMany(static l => l.Attributes).Select(Normalize).ToHashSet();
        return _attributes.All(existing.Contains);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "AddAttribute: adding [{Attribute}] to '{Element}'")]
    private static partial void LogAdding(ILogger logger, string attribute, string element);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AddAttribute: no declaration matches {Selectors} with the role {Role}")]
    private static partial void LogNoMatch(ILogger logger, string selectors, SyntaxRole role);
}
