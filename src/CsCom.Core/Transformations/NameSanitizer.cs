using System.Globalization;
using System.Text;
using CsCom.Model;

namespace CsCom.Transformations;

/// <summary>
/// Makes the names of types and members valid and unique C# identifiers.
/// </summary>
/// <remarks>
/// C# keywords are not renamed: they are escaped with <c>@</c> when the code is emitted.
/// </remarks>
public sealed class NameSanitizer : IModelTransformer
{
    /// <summary>
    /// The names of the members of the late-bound wrapper base class that dispinterface members must not hide.
    /// </summary>
    private static readonly HashSet<string> DispatchWrapperReservedNames =
    [
        "Dispatch", "Create", "Iid", "InvokeMethod", "InvokeMethodByRef", "InvokeGet", "InvokePut", "InvokePutRef",
        "ToWrapper", "ToInterface", "GetEnumeratorCore", "Invoke", "InvokeByRef",
        "Equals", "GetHashCode", "ToString", "GetType", "MemberwiseClone", "Finalize",
    ];

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        foreach (var group in library.Types.GroupBy(static t => t.ImportedFrom))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in group)
            {
                type.Name = MakeUnique(ToIdentifier(type.Name), names);
            }
        }

        foreach (var type in library.Types)
        {
            switch (type)
            {
                case EnumDefinition enumDefinition:
                    SanitizeMembers(enumDefinition.Members, static m => m.Name, static (m, n) => m.Name = n, reserved: []);
                    break;
                case RecordDefinition record:
                    SanitizeMembers(record.Fields, static f => f.Name, static (f, n) => f.Name = n, reserved: [record.Name]);
                    break;
                case InterfaceDefinition @interface:
                    SanitizeInterface(@interface);
                    break;
                case ModuleDefinition module:
                {
                    var names = new HashSet<string>(StringComparer.Ordinal) { module.Name };
                    foreach (var constant in module.Constants)
                    {
                        constant.Name = MakeUnique(ToIdentifier(constant.Name), names);
                    }

                    foreach (var function in module.Functions)
                    {
                        function.Name = MakeUnique(ToIdentifier(function.Name), names);
                        SanitizeParameters(function);
                    }

                    break;
                }
            }
        }
    }

    private static void SanitizeInterface(InterfaceDefinition @interface)
    {
        HashSet<string> reserved = @interface.InterfaceKind == InterfaceKind.Dispatch
            ? [@interface.Name, .. DispatchWrapperReservedNames]
            : [];

        // Accessors of the same property share the name, so rename by the original name.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(reserved, StringComparer.Ordinal);
        foreach (var method in @interface.Methods)
        {
            if (!renamed.TryGetValue(method.OriginalName, out string? name))
            {
                name = MakeUnique(ToIdentifier(method.Name), names);
                renamed.Add(method.OriginalName, name);
            }

            method.Name = name;
            SanitizeParameters(method);
        }

        foreach (var property in @interface.Properties)
        {
            property.Name = renamed.TryGetValue(property.Name, out string? name) ? name : MakeUnique(ToIdentifier(property.Name), names);
        }
    }

    private static void SanitizeParameters(MethodDefinition method)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in method.Parameters)
        {
            parameter.Name = MakeUnique(ToIdentifier(parameter.Name), names);
        }
    }

    private static void SanitizeMembers<T>(List<T> members, Func<T, string> getName, Action<T, string> setName, HashSet<string> reserved)
    {
        var names = new HashSet<string>(reserved, StringComparer.Ordinal);
        foreach (var member in members)
        {
            setName(member, MakeUnique(ToIdentifier(getName(member)), names));
        }
    }

    private static string MakeUnique(string name, HashSet<string> names)
    {
        string candidate = name;
        while (!names.Add(candidate))
        {
            candidate += "_";
        }

        return candidate;
    }

    /// <summary>
    /// Converts a name to a valid C# identifier by replacing invalid characters with underscores.
    /// </summary>
    public static string ToIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "_";
        }

        var builder = new StringBuilder(name.Length + 1);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool valid = i == 0 ? IsIdentifierStart(c) : IsIdentifierPart(c);
            if (i == 0 && !valid && IsIdentifierPart(c))
            {
                builder.Append('_').Append(c);
            }
            else
            {
                builder.Append(valid ? c : '_');
            }
        }

        return builder.ToString();
    }

    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsIdentifierPart(char c) =>
        c == '_' || char.IsLetterOrDigit(c) || char.GetUnicodeCategory(c) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}
