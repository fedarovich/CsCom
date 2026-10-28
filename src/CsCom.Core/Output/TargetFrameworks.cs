using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// Validates target framework monikers and derives the default C# version from them.
/// </summary>
public static partial class TargetFrameworks
{
    /// <summary>The default target framework.</summary>
    public const string Default = "net10.0-windows";

    /// <summary>The oldest supported .NET version.</summary>
    public const int MinimumVersion = 10;

    // The values of LangVersion that do not name a version.
    private static readonly HashSet<string> DefaultLanguageVersions = new(StringComparer.OrdinalIgnoreCase) { "default", "latest", "latestMajor", "preview" };

    /// <summary>
    /// Validates the target framework and returns its major .NET version.
    /// </summary>
    /// <exception cref="ArgumentException">The target framework is not a .NET 10 or later TFM.</exception>
    public static int GetMajorVersion(string targetFramework) =>
        TryGetMajorVersion(targetFramework, out int major, out string? error) ? major : throw new ArgumentException(error, nameof(targetFramework));

    /// <summary>
    /// Validates the target frameworks and resolves the C# version of the generated code.
    /// </summary>
    /// <param name="languageVersion">
    /// The C# version: a number such as <c>14</c> or <c>14.0</c>, or <c>default</c>, <c>latest</c>, <c>latestMajor</c> or
    /// <c>preview</c>, which give the default version of the target frameworks, like an empty value.
    /// </param>
    /// <param name="targetFrameworks">The target frameworks; C# 14 is the default version when there are none.</param>
    /// <param name="atLeastFrameworkDefault">
    /// Whether a number must be at least the default version of the target frameworks, e.g. C# 15 for .NET 11; otherwise it
    /// must be at least C# 14, the minimum version of the generated code.
    /// </param>
    /// <exception cref="CsComException">A target framework is not supported, or the version is not valid or too low.</exception>
    public static int ResolveLanguageVersion(string? languageVersion, IReadOnlyList<string> targetFrameworks, bool atLeastFrameworkDefault)
    {
        ArgumentNullException.ThrowIfNull(targetFrameworks);
        foreach (string targetFramework in targetFrameworks)
        {
            if (!TryGetMajorVersion(targetFramework, out _, out string? error))
            {
                throw new CsComException(error);
            }
        }

        int defaultVersion = targetFrameworks.Count > 0 ? GetDefaultLanguageVersion(targetFrameworks) : CodeGenerationOptions.MinimumLanguageVersion;
        string value = languageVersion?.Trim() ?? string.Empty;
        if (value.Length == 0 || DefaultLanguageVersions.Contains(value))
        {
            return defaultVersion;
        }

        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number))
        {
            throw new CsComException($"'{value}' is not a valid C# version.");
        }

        int version = (int)number;
        if (atLeastFrameworkDefault && version < defaultVersion)
        {
            throw new CsComException($"C# {value} is not supported for {string.Join(", ", targetFrameworks)}: C# {defaultVersion} or later is required.");
        }

        return version >= CodeGenerationOptions.MinimumLanguageVersion
            ? version
            : throw new CsComException($"The generated code requires C# {CodeGenerationOptions.MinimumLanguageVersion} or later, but C# {value} is specified.");
    }

    /// <summary>
    /// Validates the target framework and gets its major .NET version.
    /// </summary>
    /// <param name="targetFramework">The target framework.</param>
    /// <param name="major">The major .NET version.</param>
    /// <param name="error">Why the target framework is not supported.</param>
    /// <returns>Whether the target framework is a .NET 10 or later TFM.</returns>
    public static bool TryGetMajorVersion(string targetFramework, out int major, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(targetFramework);
        var match = Pattern().Match(targetFramework);
        if (!match.Success)
        {
            major = 0;
            error = $"'{targetFramework}' is not a valid .NET target framework, e.g. '{Default}'.";
            return false;
        }

        major = int.Parse(match.Groups["major"].ValueSpan, CultureInfo.InvariantCulture);
        error = major < MinimumVersion ? $"The target framework '{targetFramework}' is not supported: .NET {MinimumVersion} or later is required." : null;
        return error is null;
    }

    /// <summary>
    /// Whether the target framework is Windows-specific, e.g. <c>net10.0-windows</c>.
    /// </summary>
    public static bool IsWindowsSpecific(string targetFramework) =>
        Pattern().Match(targetFramework) is { Success: true } match && match.Groups["platform"].Value.StartsWith("-windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the default C# version for a target framework: C# 14 for .NET 10, C# 15 for .NET 11, and so on.
    /// </summary>
    public static int GetDefaultLanguageVersion(string targetFramework) => GetMajorVersion(targetFramework) + 4;

    /// <summary>
    /// Gets the default C# version for a set of target frameworks, which is the version supported by all of them.
    /// </summary>
    public static int GetDefaultLanguageVersion(IEnumerable<string> targetFrameworks) => targetFrameworks.Min(GetDefaultLanguageVersion);

    [GeneratedRegex(@"^net(?<major>\d+)\.(?<minor>\d+)(?<platform>-[a-zA-Z]+[\d.]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
