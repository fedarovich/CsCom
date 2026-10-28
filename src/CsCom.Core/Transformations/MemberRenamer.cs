using System.Text.RegularExpressions;
using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Renames the members or the parameters whose names match regular expressions; the <c>RenameMember</c> element of
/// configuration files.
/// </summary>
/// <remarks>
/// <para>
/// The members are the methods and properties of interfaces, the fields of structures, the members of enums, and the functions
/// and constants of modules. The accessors of a property share its name, so they are renamed together.
/// </para>
/// <para>
/// The new name replaces the matches of the last expression (the parameter, or the member if no parameter is specified) in the
/// name, see <see cref="Regex.Replace(string, string)"/>. Names that are not valid or not unique are fixed by <see cref="NameSanitizer"/>.
/// </para>
/// </remarks>
public sealed partial class MemberRenamer : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MemberRenamer"/> class.
    /// </summary>
    /// <param name="type">Matches the names of the types whose members are renamed; all types when <see langword="null"/>.</param>
    /// <param name="member">Matches the names of the members to rename, or of the methods whose parameters are renamed.</param>
    /// <param name="parameter">Matches the names of the parameters to rename; the members are renamed when <see langword="null"/>.</param>
    /// <param name="newName">The replacement of the matches.</param>
    public MemberRenamer(Regex? type, Regex member, Regex? parameter, string newName)
    {
        Type = type;
        Member = member;
        Parameter = parameter;
        NewName = newName;
    }

    /// <summary>Gets the expression that matches the names of the types; all types when <see langword="null"/>.</summary>
    public Regex? Type { get; }

    /// <summary>Gets the expression that matches the names of the members.</summary>
    public Regex Member { get; }

    /// <summary>Gets the expression that matches the names of the parameters to rename, if any.</summary>
    public Regex? Parameter { get; }

    /// <summary>Gets the replacement of the matches.</summary>
    public string NewName { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        bool matched = false;
        foreach (var type in library.Types.Where(t => ModelSelectors.IsMatch(Type, t.Name)))
        {
            if (Parameter is null)
            {
                foreach (var (name, rename) in ModelSelectors.GetMembers(type).Where(m => Member.IsMatch(m.Name)).ToList())
                {
                    matched = true;
                    string newName = Member.Replace(name, NewName);
                    LogRenaming(context.Logger, $"{type.Name}.{name}", newName);
                    rename(newName);
                }

                continue;
            }

            foreach (var method in ModelSelectors.GetMethods(type).Where(m => Member.IsMatch(m.Name)))
            {
                foreach (var parameter in method.Parameters.Where(p => Parameter.IsMatch(p.Name)))
                {
                    matched = true;
                    string newName = Parameter.Replace(parameter.Name, NewName);
                    LogRenaming(context.Logger, $"{type.Name}.{method.Name}({parameter.Name})", newName);
                    parameter.Name = newName;
                }
            }
        }

        if (!matched)
        {
            LogNoMatch(context.Logger, Parameter is null ? "member" : "parameter", ModelSelectors.Describe(("Type", Type), ("Member", Member), ("Parameter", Parameter)));
        }
    }

    internal static MemberRenamer Create(ConfigurationElement element) => new(
        element.GetRegex("Type"),
        element.GetRequiredRegex("Member"),
        element.GetRegex("Parameter"),
        element.GetRequiredAttribute("NewName"));

    [LoggerMessage(Level = LogLevel.Debug, Message = "RenameMember: renaming '{Name}' to '{NewName}'")]
    private static partial void LogRenaming(ILogger logger, string name, string newName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RenameMember: no {Kind} matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string kind, string selectors);
}
