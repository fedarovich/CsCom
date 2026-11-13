using System.Globalization;
using System.Text;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Makes the names of types and members valid and unique C# identifiers.
/// </summary>
/// <remarks>
/// C# keywords are not renamed: they are escaped with <c>@</c> when the code is emitted.
/// </remarks>
public sealed partial class NameSanitizer : IModelTransformer
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
        // The types of a library are generated into one namespace, also the copies that another version of the library
        // imported, so they are grouped by the library and not by its version. The type that comes first keeps its name.
        foreach (var group in library.Types.GroupBy(t => GetLibraryKey(t.ImportedFrom?.Guid ?? library.Guid, t.ImportedFrom?.Name ?? library.Name)))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in group)
            {
                string name = ToIdentifier(type.Name);
                type.Name = MakeUnique(name, names);
                if (type.Name != name)
                {
                    LogTypeRenamed(context.Logger, name, type.Name);
                }
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
        // A member cannot have the name of its type. The members of a late-bound wrapper are named after the methods and the
        // properties; those of the other interfaces are the methods, since the accessors of the properties have prefixes.
        HashSet<string> reserved = @interface.InterfaceKind == InterfaceKind.Dispatch
            ? [@interface.Name, .. DispatchWrapperReservedNames]
            : @interface.Methods.Any(m => m.InvokeKind == InvokeKind.Method && ToIdentifier(m.Name) == @interface.Name) ? [@interface.Name] : [];

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
            // A property is not an accessor of a method, whose names are looked up by their original names: its name can be
            // the former name of a method that was renamed.
            property.Name = MakeUnique(ToIdentifier(property.Name), names);
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

    private static string GetLibraryKey(Guid guid, string name) => guid != Guid.Empty ? guid.ToString() : name.ToUpperInvariant();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The type '{Name}' is renamed to '{NewName}', because another type of the library has the name")]
    private static partial void LogTypeRenamed(ILogger logger, string name, string newName);

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
