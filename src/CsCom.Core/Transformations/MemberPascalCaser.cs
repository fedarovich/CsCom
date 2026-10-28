using System.Text;
using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Converts the names of members to PascalCase, e.g. <c>MAX_VALUE</c> to <c>MaxValue</c> and <c>checked</c> to
/// <c>Checked</c>; the <c>RenameMembersToPascalCase</c> element of configuration files.
/// </summary>
/// <remarks>
/// <para>
/// The members are the methods and properties of interfaces, the fields of structures, the members of enums, and the functions
/// and constants of modules; see <see cref="ToPascalCase"/> for the conversion. The accessors of a property share its name, so
/// they are renamed together and keep their prefix, e.g. <c>get_SomeProperty</c>, while ordinary methods are fully converted,
/// e.g. <c>get_SomeValue</c> to <c>GetSomeValue</c>. Parameters and types are not renamed.
/// </para>
/// <para>
/// A member is not renamed if its new name would be the name of another member of the type, e.g. <c>count</c> next to
/// <c>Count</c>.
/// </para>
/// </remarks>
public sealed partial class MemberPascalCaser : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MemberPascalCaser"/> class.
    /// </summary>
    /// <param name="type">Matches the types whose members are renamed; all types when <see langword="null"/>.</param>
    /// <param name="member">Matches the names of the members to rename; all members when <see langword="null"/>.</param>
    public MemberPascalCaser(TypeSelector? type, NameSelector? member)
    {
        Type = type;
        Member = member;
    }

    /// <summary>Gets the selector of the types; all types when <see langword="null"/>.</summary>
    public TypeSelector? Type { get; }

    /// <summary>Gets the selector of the members; all members when <see langword="null"/>.</summary>
    public NameSelector? Member { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        bool matched = false;
        foreach (var type in library.Types.Where(t => ModelSelectors.IsMatch(Type, t)))
        {
            // The accessors of a property share the original name, so the names are converted rather than the members.
            var members = ModelSelectors.GetMembers(type).ToList();
            var names = members.Select(static m => m.Name).Distinct(StringComparer.Ordinal).ToList();
            var selected = names.Where(n => ModelSelectors.IsMatch(Member, n)).ToList();
            matched |= selected.Count > 0;
            var newNames = selected
                .Select(static n => (Name: n, NewName: ToPascalCase(n)))
                .Where(static r => r.NewName != r.Name)
                .ToDictionary(static r => r.Name, static r => r.NewName, StringComparer.Ordinal);

            // A member that keeps its name can collide with another new name, so the collisions are removed until there are none.
            while (GetCollisions(names, newNames) is { Count: > 0 } collisions)
            {
                foreach (var (name, newName) in collisions)
                {
                    LogCollision(context.Logger, $"{type.Name}.{name}", newName);
                    newNames.Remove(name);
                }
            }

            foreach (var (name, newName) in newNames)
            {
                LogRenaming(context.Logger, $"{type.Name}.{name}", newName);
            }

            foreach (var (name, _, rename) in members)
            {
                if (newNames.TryGetValue(name, out string? newName))
                {
                    rename(newName);
                }
            }
        }

        if (!matched && (Type is not null || Member is not null))
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(Type, ("Member", Member)));
        }
    }

    /// <summary>
    /// Converts a name to PascalCase. The name is split into words at the characters that are not letters or digits, usually
    /// <c>_</c>, and the first letter of each word is converted to upper case. Words in upper case with more than two letters
    /// are converted to title case, e.g. <c>MAX</c> to <c>Max</c>; other words keep the rest of their letters, so
    /// <c>XMLDocument</c>, <c>IO</c> and <c>Value2</c> are not changed.
    /// </summary>
    /// <param name="name">The name to convert.</param>
    /// <returns>The converted name, or <paramref name="name"/> if the result would be empty or start with a digit.</returns>
    public static string ToPascalCase(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var builder = new StringBuilder(name.Length);
        int start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i == name.Length || !char.IsLetterOrDigit(name[i]))
            {
                AppendWord(builder, name.AsSpan(start, i - start));
                start = i + 1;
            }
        }

        return builder.Length == 0 || char.IsDigit(builder[0]) ? name : builder.ToString();
    }

    internal static MemberPascalCaser Create(ConfigurationElement element) => new(element.GetTypeSelector(), element.GetSelector("Member"));

    private static void AppendWord(StringBuilder builder, ReadOnlySpan<char> word)
    {
        if (word.IsEmpty)
        {
            return;
        }

        int letters = 0;
        bool upperCase = true;
        foreach (char c in word)
        {
            if (char.IsLetter(c))
            {
                letters++;
                upperCase &= char.IsUpper(c);
            }
        }

        builder.Append(char.ToUpperInvariant(word[0]));
        if (upperCase && letters > 2)
        {
            foreach (char c in word[1..])
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }
        else
        {
            builder.Append(word[1..]);
        }
    }

    // Gets the new names that are also the names of members with other original names, whether converted or kept.
    private static List<(string Name, string NewName)> GetCollisions(List<string> names, Dictionary<string, string> newNames) =>
        names
            .GroupBy(n => newNames.GetValueOrDefault(n, n), StringComparer.Ordinal)
            .Where(static g => g.Skip(1).Any())
            .SelectMany(g => g.Where(newNames.ContainsKey).Select(n => (n, g.Key)))
            .ToList();

    [LoggerMessage(Level = LogLevel.Debug, Message = "RenameMembersToPascalCase: renaming '{Name}' to '{NewName}'")]
    private static partial void LogRenaming(ILogger logger, string name, string newName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RenameMembersToPascalCase: '{Name}' is not renamed to '{NewName}', because it is the name of another member")]
    private static partial void LogCollision(ILogger logger, string name, string newName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RenameMembersToPascalCase: no member matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
