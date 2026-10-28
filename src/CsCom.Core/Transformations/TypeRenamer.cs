using System.Text.RegularExpressions;
using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Renames the types whose names match a regular expression; the <c>RenameType</c> element of configuration files.
/// </summary>
/// <remarks>
/// The new name replaces the matches of the expression in the name (see <see cref="Regex.Replace(string, string)"/>), so it can
/// use substitutions like <c>$1</c>. Names that are not valid or not unique are fixed by <see cref="NameSanitizer"/>.
/// </remarks>
public sealed partial class TypeRenamer : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TypeRenamer"/> class.
    /// </summary>
    /// <param name="type">Matches the names of the types to rename.</param>
    /// <param name="newName">The replacement of the matches.</param>
    public TypeRenamer(Regex type, string newName)
    {
        Type = type;
        NewName = newName;
    }

    /// <summary>Gets the expression that matches the names of the types to rename.</summary>
    public Regex Type { get; }

    /// <summary>Gets the replacement of the matches.</summary>
    public string NewName { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        bool matched = false;
        foreach (var type in library.Types.Where(t => Type.IsMatch(t.Name)))
        {
            matched = true;
            string name = Type.Replace(type.Name, NewName);
            LogRenaming(context.Logger, type.Name, name);
            type.Name = name;
        }

        if (!matched)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(("Type", Type)));
        }
    }

    internal static TypeRenamer Create(ConfigurationElement element) =>
        new(element.GetRequiredRegex("Type"), element.GetRequiredAttribute("NewName"));

    [LoggerMessage(Level = LogLevel.Debug, Message = "RenameType: renaming the type '{Name}' to '{NewName}'")]
    private static partial void LogRenaming(ILogger logger, string name, string newName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RenameType: no type matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
