using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CsCom.TypeLibraries;

/// <summary>
/// Finds type libraries given as files, as registered library identifiers (LIBIDs) or as ProgIDs of registered classes.
/// </summary>
/// <remarks>
/// A source is resolved in this order:
/// <list type="number">
/// <item>An existing file, optionally followed by a resource index, e.g. <c>foo.dll\2</c>.</item>
/// <item>A LIBID with or without braces, optionally followed by the version as in the registry, e.g.
/// <c>{00020813-0000-0000-C000-000000000046}:1.9</c>; the highest registered version by default.</item>
/// <item>The ProgID of a registered class, e.g. <c>Excel.Application</c>, whose type library is used.</item>
/// </list>
/// </remarks>
public static partial class TypeLibrarySource
{
    /// <summary>
    /// Gets the path of the type library.
    /// </summary>
    /// <param name="source">The file, LIBID or ProgID.</param>
    /// <returns>The full path, which can end with a resource index.</returns>
    /// <exception cref="CsComException">The type library is not found.</exception>
    public static string Resolve(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (IsFile(source))
        {
            return Path.GetFullPath(source);
        }

        if (TryParseLibraryId(source, out var libraryId, out var version))
        {
            return FindRegisteredPath(libraryId, version)
                ?? throw new CsComException(version is null
                    ? $"The type library {Format(libraryId)} is not registered."
                    : $"The type library {Format(libraryId)} version {version} is not registered.");
        }

        if (ProgIdPattern().IsMatch(source) && FindClassLibrary(source) is var (classLibraryId, classVersion))
        {
            return FindRegisteredPath(classLibraryId, classVersion) ?? FindRegisteredPath(classLibraryId, null)
                ?? throw new CsComException($"The type library {Format(classLibraryId)} of the class '{source}' is not registered.");
        }

        throw new CsComException($"'{source}' is not a type library file, a registered LIBID or a ProgID.");
    }

    /// <summary>
    /// Gets the form of the source recorded in <c>cscom.json</c>: the full path of a file, a LIBID in upper case with
    /// braces and the version if specified, or the ProgID.
    /// </summary>
    public static string Normalize(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (IsFile(source))
        {
            return Path.GetFullPath(source);
        }

        if (TryParseLibraryId(source, out var libraryId, out var version))
        {
            return version is null ? Format(libraryId) : $"{Format(libraryId)}:{version}";
        }

        return source;
    }

    /// <summary>Whether the source is a file rather than a registered type library.</summary>
    public static bool IsFile(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // A trailing \N selects a type library resource of a DLL or an executable.
        string path = ResourceIndexPattern().Replace(source, string.Empty);
        return File.Exists(path);
    }

    /// <summary>
    /// Gets the source of a registered type library, a LIBID with its version as <see cref="Resolve"/> accepts it, if the type
    /// library is registered.
    /// </summary>
    /// <returns>The source, e.g. <c>{00020813-0000-0000-C000-000000000046}:1.9</c>, or <see langword="null"/>.</returns>
    public static string? FindRegistered(Guid libraryId, ushort majorVersion, ushort minorVersion)
    {
        var version = new TypeLibraryVersion(majorVersion, minorVersion);
        return FindRegisteredPath(libraryId, version) is null ? null : $"{Format(libraryId)}:{version}";
    }

    private static string Format(Guid libraryId) => libraryId.ToString("B").ToUpperInvariant();

    private static bool TryParseLibraryId(string source, out Guid libraryId, out TypeLibraryVersion? version)
    {
        version = null;
        int colon = source.IndexOf(':', StringComparison.Ordinal);
        string id = colon < 0 ? source : source[..colon];
        if (!Guid.TryParse(id, out libraryId))
        {
            return false;
        }

        if (colon >= 0)
        {
            version = TypeLibraryVersion.TryParse(source[(colon + 1)..])
                ?? throw new CsComException($"'{source[(colon + 1)..]}' is not a valid type library version: major.minor is expected, e.g. 1.9.");
        }

        return true;
    }

    // Finds the class of a ProgID in the 64-bit and then in the 32-bit registry, and the type library it declares.
    private static (Guid LibraryId, TypeLibraryVersion? Version)? FindClassLibrary(string progId)
    {
        foreach (var view in RegistryViews)
        {
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            string name = progId;
            if (classes.OpenSubKey($@"{name}\CurVer")?.GetValue(null) is string current)
            {
                name = current;
            }

            if (classes.OpenSubKey($@"{name}\CLSID")?.GetValue(null) is not string clsid
                || classes.OpenSubKey($@"CLSID\{clsid}\TypeLib")?.GetValue(null) is not string typeLib
                || !Guid.TryParse(typeLib, out var libraryId))
            {
                continue;
            }

            var version = classes.OpenSubKey($@"CLSID\{clsid}\Version")?.GetValue(null) is string text ? TypeLibraryVersion.TryParse(text) : null;
            return (libraryId, version);
        }

        return null;
    }

    private static string? FindRegisteredPath(Guid libraryId, TypeLibraryVersion? version)
    {
        foreach (var view in RegistryViews)
        {
            using var classes = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
            using var library = classes.OpenSubKey($@"TypeLib\{Format(libraryId)}");
            if (library is null)
            {
                continue;
            }

            var versions = library.GetSubKeyNames()
                .Select(static name => (Name: name, Version: TypeLibraryVersion.TryParse(name)))
                .Where(v => v.Version is { } parsed && (version is null || parsed == version))
                .OrderByDescending(static v => v.Version)
                .ToList();

            foreach (var (name, _) in versions)
            {
                using var versionKey = library.OpenSubKey(name);
                if (versionKey is not null && FindPlatformPath(versionKey) is { } path)
                {
                    return path;
                }
            }
        }

        return null;
    }

    // HKCR\TypeLib\{LIBID}\{version}\{LCID}\{win64|win32}, preferring the neutral locale and the platform of the process.
    private static string? FindPlatformPath(RegistryKey versionKey)
    {
        var locales = versionKey.GetSubKeyNames()
            .Where(static name => int.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            .OrderBy(static name => name == "0" ? 0 : 1);
        string[] platforms = Environment.Is64BitProcess ? ["win64", "win32"] : ["win32"];
        foreach (string locale in locales)
        {
            foreach (string platform in platforms)
            {
                using var key = versionKey.OpenSubKey($@"{locale}\{platform}");
                if (key?.GetValue(null) is string path && !string.IsNullOrWhiteSpace(path))
                {
                    return Environment.ExpandEnvironmentVariables(path);
                }
            }
        }

        return null;
    }

    private static RegistryView[] RegistryViews => Environment.Is64BitOperatingSystem ? [RegistryView.Registry64, RegistryView.Registry32] : [RegistryView.Default];

    [GeneratedRegex(@"\\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIndexPattern();

    [GeneratedRegex(@"^[A-Za-z_][\w]*(\.[\w]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProgIdPattern();

    // The version of a registered type library, written as hexadecimal numbers in the registry.
    private sealed record TypeLibraryVersion(int Major, int Minor) : IComparable<TypeLibraryVersion>
    {
        public static TypeLibraryVersion? TryParse(string text)
        {
            string[] parts = text.Trim().Split('.');
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int major)
                && int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int minor)
                ? new TypeLibraryVersion(major, minor)
                : null;
        }

        public int CompareTo(TypeLibraryVersion? other) =>
            other is null ? 1 : Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);

        public override string ToString() => $"{Major:x}.{Minor:x}";
    }
}
