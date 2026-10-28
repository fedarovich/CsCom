using System.Text.RegularExpressions;
using CsCom.Model;

namespace CsCom.Transformations;

/// <summary>
/// Selects the types, members and parameters of the model by regular expressions matched against their current names.
/// </summary>
internal static class ModelSelectors
{
    /// <summary>Whether the name matches the pattern; an omitted pattern matches all names.</summary>
    public static bool IsMatch(Regex? pattern, string name) => pattern?.IsMatch(name) != false;

    /// <summary>Describes the specified selectors for log messages, e.g. <c>Type='^IShape$', Member='^Move$'</c>.</summary>
    public static string Describe(params (string Attribute, Regex? Pattern)[] selectors) =>
        string.Join(", ", selectors.Where(static s => s.Pattern is not null).Select(static s => $"{s.Attribute}='{s.Pattern}'"));

    /// <summary>Gets the methods of an interface or the functions of a module.</summary>
    public static IReadOnlyList<MethodDefinition> GetMethods(TypeDefinition type) => type switch
    {
        InterfaceDefinition @interface => @interface.Methods,
        ModuleDefinition module => module.Functions,
        _ => [],
    };

    /// <summary>
    /// Gets the named members of a type: the methods and properties of interfaces, the fields of structures, the members of
    /// enums, and the functions and constants of modules.
    /// </summary>
    public static IEnumerable<(string Name, Action<string> Rename)> GetMembers(TypeDefinition type) => type switch
    {
        InterfaceDefinition @interface =>
        [
            .. @interface.Methods.Select(static m => (m.Name, (Action<string>)(n => m.Name = n))),
            .. @interface.Properties.Select(static p => (p.Name, (Action<string>)(n => p.Name = n))),
        ],
        RecordDefinition record => record.Fields.Select(static f => (f.Name, (Action<string>)(n => f.Name = n))),
        EnumDefinition @enum => @enum.Members.Select(static m => (m.Name, (Action<string>)(n => m.Name = n))),
        ModuleDefinition module =>
        [
            .. module.Functions.Select(static f => (f.Name, (Action<string>)(n => f.Name = n))),
            .. module.Constants.Select(static c => (c.Name, (Action<string>)(n => c.Name = n))),
        ],
        _ => [],
    };
}
