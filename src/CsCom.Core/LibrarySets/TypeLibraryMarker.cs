using System.Globalization;
using CsCom.Model;

namespace CsCom.LibrarySets;

/// <summary>
/// The assembly metadata (<see cref="System.Reflection.AssemblyMetadataAttribute"/>) that the generated code carries about
/// the type libraries that it was generated from, so that a type library that is exported from an assembly can refer to
/// their types instead of defining them again.
/// </summary>
public static class TypeLibraryMarker
{
    /// <summary>
    /// The key of the metadata of a type library, whose value is <c>namespace|name|{LIBID}|major.minor</c>: the namespace of
    /// the code generated from the library, and its identity.
    /// </summary>
    public const string LibraryKey = "CsCom.TypeLibrary";

    /// <summary>
    /// The key of the metadata of a type whose name is not its name in the type library, whose value is
    /// <c>namespace.Name|OriginalName</c>.
    /// </summary>
    public const string TypeKey = "CsCom.TypeLibrary.Type";

    /// <summary>The key of the metadata with the namespace of the support code that was generated into the assembly.</summary>
    public const string SupportNamespaceKey = "CsCom.SupportNamespace";

    /// <summary>Formats the value of the metadata of a type library.</summary>
    /// <param name="namespace">The namespace of the code generated from the library.</param>
    /// <param name="library">The library.</param>
    public static string FormatLibrary(string @namespace, TypeLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return string.Create(CultureInfo.InvariantCulture, $"{@namespace}|{library.Name}|{library.Guid:B}|{library.MajorVersion}.{library.MinorVersion}");
    }

    /// <summary>Parses the value of the metadata of a type library.</summary>
    /// <param name="value">The value.</param>
    /// <param name="namespace">The namespace of the code generated from the library.</param>
    /// <param name="library">The identity of the library.</param>
    /// <returns>Whether the value is valid.</returns>
    public static bool TryParseLibrary(string? value, out string @namespace, out TypeLibraryReference library)
    {
        @namespace = string.Empty;
        library = null!;
        if (value?.Split('|') is not [{ Length: > 0 } first, { Length: > 0 } name, var guid, var version]
            || !Guid.TryParse(guid, out Guid libraryId)
            || version.Split('.') is not [var major, var minor]
            || !ushort.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out ushort majorVersion)
            || !ushort.TryParse(minor, NumberStyles.None, CultureInfo.InvariantCulture, out ushort minorVersion))
        {
            return false;
        }

        @namespace = first;
        library = new TypeLibraryReference(name, libraryId, majorVersion, minorVersion);
        return true;
    }

    /// <summary>Formats the value of the metadata of a type that was renamed.</summary>
    /// <param name="namespace">The namespace of the code generated from the library of the type.</param>
    /// <param name="type">The type.</param>
    public static string FormatType(string @namespace, TypeDefinition type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return $"{@namespace}.{type.Name}|{type.OriginalName}";
    }

    /// <summary>Parses the value of the metadata of a type that was renamed.</summary>
    /// <param name="value">The value.</param>
    /// <param name="fullName">The full name of the generated type.</param>
    /// <param name="originalName">The name of the type in the type library.</param>
    /// <returns>Whether the value is valid.</returns>
    public static bool TryParseType(string? value, out string fullName, out string originalName)
    {
        if (value?.Split('|') is [{ Length: > 0 } first, { Length: > 0 } second])
        {
            fullName = first;
            originalName = second;
            return true;
        }

        fullName = originalName = string.Empty;
        return false;
    }
}
