using CsCom.Model;

namespace CsCom.Transformations;

/// <summary>
/// Helpers for the transformers that select the types, members and parameters of the model by their current names.
/// </summary>
internal static class ModelSelectors
{
    /// <summary>Whether the name matches the selector; an omitted selector matches all names.</summary>
    public static bool IsMatch(NameSelector? selector, string name) => selector?.IsMatch(name) != false;

    /// <summary>Whether the type matches the selector; an omitted selector matches all types.</summary>
    public static bool IsMatch(TypeSelector? selector, TypeDefinition type) => selector?.IsMatch(type) != false;

    /// <summary>
    /// Describes the specified selectors for log messages, e.g. <c>TypeName='IShape', Member='^Move$'</c>: the attributes of
    /// exact names have the <c>Name</c> suffix.
    /// </summary>
    public static string Describe(params (string Attribute, NameSelector? Selector)[] selectors) =>
        string.Join(", ", selectors
            .Where(static s => s.Selector is not null)
            .Select(static s => $"{s.Attribute}{(s.Selector!.Name is null ? string.Empty : "Name")}='{s.Selector}'"));

    /// <summary>Describes a type selector for log messages, e.g. <c>Type='^I', Kind='interface|dispinterface'</c>.</summary>
    public static string Describe(TypeSelector? type, params (string Attribute, NameSelector? Selector)[] selectors)
    {
        string description = Describe([("Type", type?.Name), .. selectors]);
        if (type is null || type.Kinds == TypeKinds.All)
        {
            return description;
        }

        var names = Enum.GetValues<TypeKinds>()
            .Where(k => k is not TypeKinds.None and not TypeKinds.All && type.Kinds.HasFlag(k))
            .Select(static k => k.ToString().ToLowerInvariant());
        string kinds = $"Kind='{string.Join('|', names)}'";
        return description.Length == 0 ? kinds : $"{description}, {kinds}";
    }

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
    public static IEnumerable<(string Name, object Member, Action<string> Rename)> GetMembers(TypeDefinition type) => type switch
    {
        InterfaceDefinition @interface =>
        [
            .. @interface.Methods.Select(static m => (m.Name, (object)m, (Action<string>)(n => m.Name = n))),
            .. @interface.Properties.Select(static p => (p.Name, (object)p, (Action<string>)(n => p.Name = n))),
        ],
        RecordDefinition record => record.Fields.Select(static f => (f.Name, (object)f, (Action<string>)(n => f.Name = n))),
        EnumDefinition @enum => @enum.Members.Select(static m => (m.Name, (object)m, (Action<string>)(n => m.Name = n))),
        ModuleDefinition module =>
        [
            .. module.Functions.Select(static f => (f.Name, (object)f, (Action<string>)(n => f.Name = n))),
            .. module.Constants.Select(static c => (c.Name, (object)c, (Action<string>)(n => c.Name = n))),
        ],
        _ => [],
    };
}
