using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Renames the members or the parameters whose names match selectors; the <c>RenameMember</c> element of configuration files.
/// </summary>
/// <remarks>
/// <para>
/// The members are the methods and properties of interfaces, the fields of structures, the members of enums, and the functions
/// and constants of modules. The accessors of a property share its name, so they are renamed together.
/// </para>
/// <para>
/// The new name is applied by the last selector (the parameter, or the member if no parameter is specified), see
/// <see cref="NameSelector.Replace"/>. Names that are not valid or not unique are fixed by <see cref="NameSanitizer"/>.
/// </para>
/// </remarks>
public sealed partial class MemberRenamer : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MemberRenamer"/> class.
    /// </summary>
    /// <param name="type">Matches the types whose members are renamed; all types when <see langword="null"/>.</param>
    /// <param name="member">Matches the names of the members to rename, or of the methods whose parameters are renamed.</param>
    /// <param name="parameter">Matches the names of the parameters to rename; the members are renamed when <see langword="null"/>.</param>
    /// <param name="newName">The new name, or the replacement of the matches of a regular expression.</param>
    public MemberRenamer(TypeSelector? type, NameSelector member, NameSelector? parameter, string newName)
    {
        Type = type;
        Member = member;
        Parameter = parameter;
        NewName = newName;
    }

    /// <summary>Gets the selector of the types; all types when <see langword="null"/>.</summary>
    public TypeSelector? Type { get; }

    /// <summary>Gets the selector of the members.</summary>
    public NameSelector Member { get; }

    /// <summary>Gets the selector of the parameters to rename, if any.</summary>
    public NameSelector? Parameter { get; }

    /// <summary>Gets the new name, or the replacement of the matches of a regular expression.</summary>
    public string NewName { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        bool matched = false;
        foreach (var type in library.Types.Where(t => ModelSelectors.IsMatch(Type, t)))
        {
            if (Parameter is null)
            {
                foreach (var (name, _, rename) in ModelSelectors.GetMembers(type).Where(m => Member.IsMatch(m.Name)).ToList())
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
            LogNoMatch(context.Logger, Parameter is null ? "member" : "parameter", ModelSelectors.Describe(Type, ("Member", Member), ("Parameter", Parameter)));
        }
    }

    internal static MemberRenamer Create(ConfigurationElement element) => new(
        element.GetTypeSelector(),
        element.GetRequiredSelector("Member"),
        element.GetSelector("Parameter"),
        element.GetRequiredAttribute("NewName"));

    [LoggerMessage(Level = LogLevel.Debug, Message = "RenameMember: renaming '{Name}' to '{NewName}'")]
    private static partial void LogRenaming(ILogger logger, string name, string newName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RenameMember: no {Kind} matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string kind, string selectors);
}
