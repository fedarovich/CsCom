using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Validates the target framework and returns its major .NET version.
    /// </summary>
    /// <exception cref="ArgumentException">The target framework is not a .NET 10 or later TFM.</exception>
    public static int GetMajorVersion(string targetFramework) =>
        TryGetMajorVersion(targetFramework, out int major, out string? error) ? major : throw new ArgumentException(error, nameof(targetFramework));

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
