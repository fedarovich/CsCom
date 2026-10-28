using System.Reflection;

namespace CsCom;

/// <summary>
/// Information about the CsCom tool.
/// </summary>
public static class ToolInfo
{
    /// <summary>
    /// The version of the tool without the source revision, e.g. <c>0.1.0</c>. It is also the version of the CsCom.Runtime package.
    /// </summary>
    public static string Version { get; } = GetVersion();

    private static string GetVersion()
    {
        string? version = typeof(ToolInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            return typeof(ToolInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? version[..plus] : version;
    }
}
