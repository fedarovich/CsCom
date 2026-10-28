using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Renames the types whose names match a selector; the <c>RenameType</c> element of configuration files.
/// </summary>
/// <remarks>
/// With a regular expression, the new name replaces its matches in the name (see
/// <see cref="System.Text.RegularExpressions.Regex.Replace(string, string)"/>), so it can use substitutions like <c>$1</c>; with
/// an exact name, it is the new name as is. Names that are not valid or not unique are fixed by <see cref="NameSanitizer"/>.
/// </remarks>
public sealed partial class TypeRenamer : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TypeRenamer"/> class.
    /// </summary>
    /// <param name="type">Matches the types to rename; its name selector is required.</param>
    /// <param name="newName">The new name, or the replacement of the matches of a regular expression.</param>
    public TypeRenamer(TypeSelector type, string newName)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.Name is null)
        {
            throw new ArgumentException("The selector must match the names of the types.", nameof(type));
        }

        Type = type;
        NewName = newName;
    }

    /// <summary>Gets the selector of the types to rename.</summary>
    public TypeSelector Type { get; }

    /// <summary>Gets the new name, or the replacement of the matches of a regular expression.</summary>
    public string NewName { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        bool matched = false;
        foreach (var type in library.Types.Where(Type.IsMatch))
        {
            matched = true;
            string name = Type.Name!.Replace(type.Name, NewName);
            LogRenaming(context.Logger, type.Name, name);
            type.Name = name;
        }

        if (!matched)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(Type));
        }
    }

    internal static TypeRenamer Create(ConfigurationElement element) => new(
        new TypeSelector(element.GetRequiredSelector("Type"), element.GetFlags<TypeKinds>("Kind") ?? TypeKinds.All),
        element.GetRequiredAttribute("NewName"));

    [LoggerMessage(Level = LogLevel.Debug, Message = "RenameType: renaming the type '{Name}' to '{NewName}'")]
    private static partial void LogRenaming(ILogger logger, string name, string newName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RenameType: no type matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
