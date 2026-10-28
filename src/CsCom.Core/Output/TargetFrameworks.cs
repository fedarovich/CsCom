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
    public static int GetMajorVersion(string targetFramework)
    {
        var match = Pattern().Match(targetFramework);
        if (!match.Success)
        {
            throw new ArgumentException($"'{targetFramework}' is not a valid .NET target framework, e.g. '{Default}'.", nameof(targetFramework));
        }

        int major = int.Parse(match.Groups["major"].ValueSpan, CultureInfo.InvariantCulture);
        if (major < MinimumVersion)
        {
            throw new ArgumentException($"The target framework '{targetFramework}' is not supported: .NET {MinimumVersion} or later is required.", nameof(targetFramework));
        }

        return major;
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
