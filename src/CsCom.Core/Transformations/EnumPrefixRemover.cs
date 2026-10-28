using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Removes the prefix shared by the members of enums, e.g. <c>xl</c> from <c>xlArea</c> and <c>xlBar</c>; the
/// <c>RemoveEnumPrefix</c> element of configuration files.
/// </summary>
/// <remarks>
/// The prefix is the longest common prefix of the names that ends at a word boundary: after an underscore, or before an upper
/// case letter that follows a lower case letter or a digit. The rest of each name must start with a letter. Enums with fewer
/// than two members, or whose members have no such prefix, are not changed.
/// </remarks>
public sealed partial class EnumPrefixRemover : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnumPrefixRemover"/> class.
    /// </summary>
    /// <param name="type">Matches the names of the enums; all enums when <see langword="null"/>.</param>
    public EnumPrefixRemover(NameSelector? type)
    {
        Type = type;
    }

    /// <summary>Gets the selector of the names of the enums; all enums when <see langword="null"/>.</summary>
    public NameSelector? Type { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        bool matched = false;
        foreach (var definition in library.Types.OfType<EnumDefinition>().Where(e => ModelSelectors.IsMatch(Type, e.Name)))
        {
            matched = true;
            int length = GetPrefixLength(definition.Members.Select(static m => m.Name).ToList());
            if (length == 0)
            {
                LogNoPrefix(context.Logger, definition.Name);
                continue;
            }

            LogRemovingPrefix(context.Logger, definition.Members[0].Name[..length], definition.Name);
            foreach (var member in definition.Members)
            {
                member.Name = member.Name[length..];
            }
        }

        if (!matched && Type is not null)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(("Type", Type)));
        }
    }

    /// <summary>
    /// Gets the length of the prefix that can be removed from the names, or 0 if there is none.
    /// </summary>
    public static int GetPrefixLength(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count < 2)
        {
            return 0;
        }

        int common = names[0].Length;
        for (int i = 1; i < names.Count; i++)
        {
            common = Math.Min(common, names[0].AsSpan().CommonPrefixLength(names[i]));
        }

        for (int length = common; length > 0; length--)
        {
            if (names.All(name => IsBoundary(name, length)))
            {
                return length;
            }
        }

        return 0;
    }

    internal static EnumPrefixRemover Create(ConfigurationElement element) => new(element.GetSelector("Type"));

    private static bool IsBoundary(string name, int index) =>
        index < name.Length
        && char.IsLetter(name[index])
        && (name[index - 1] == '_' || (char.IsUpper(name[index]) && (char.IsLower(name[index - 1]) || char.IsDigit(name[index - 1]))));

    [LoggerMessage(Level = LogLevel.Debug, Message = "RemoveEnumPrefix: removing the prefix '{Prefix}' from the members of '{Name}'")]
    private static partial void LogRemovingPrefix(ILogger logger, string prefix, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "RemoveEnumPrefix: the members of '{Name}' have no common prefix")]
    private static partial void LogNoPrefix(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RemoveEnumPrefix: no enum matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
