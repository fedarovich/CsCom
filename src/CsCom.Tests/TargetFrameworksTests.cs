using CsCom.Output;

namespace CsCom.Tests;

[TestClass]
public sealed class TargetFrameworksTests
{
    [TestMethod]
    [DataRow("net10.0-windows", 14)]
    [DataRow("net10.0", 14)]
    [DataRow("net11.0-windows", 15)]
    [DataRow("net11.0-windows10.0.19041.0", 15)]
    [DataRow("net12.0-windows", 16)]
    public void DerivesTheDefaultLanguageVersion(string targetFramework, int expected)
    {
        Assert.AreEqual(expected, TargetFrameworks.GetDefaultLanguageVersion(targetFramework));
    }

    [TestMethod]
    [DataRow(null, "net10.0-windows", true, 14)]
    [DataRow("", "net11.0-windows", true, 15)]
    [DataRow("latest", "net10.0-windows", true, 14)]
    [DataRow("Preview", "net11.0", false, 15)]
    [DataRow("latestMajor", null, false, 14)]
    [DataRow("14.0", "net10.0-windows", false, 14)]
    [DataRow("15", "net10.0-windows", true, 15)]
    [DataRow("14", "net11.0-windows", false, 14)]
    public void ResolvesTheLanguageVersion(string? languageVersion, string? targetFramework, bool atLeastFrameworkDefault, int expected) =>
        Assert.AreEqual(expected, TargetFrameworks.ResolveLanguageVersion(languageVersion, targetFramework is null ? [] : [targetFramework], atLeastFrameworkDefault));

    [TestMethod]
    [DataRow("14", "net11.0-windows", true, "C# 14 is not supported for net11.0-windows: C# 15 or later is required.")]
    [DataRow("13", "net10.0-windows", false, "The generated code requires C# 14 or later, but C# 13 is specified.")]
    [DataRow("fourteen", "net10.0-windows", false, "'fourteen' is not a valid C# version.")]
    [DataRow("14", "net8.0-windows", false, "The target framework 'net8.0-windows' is not supported: .NET 10 or later is required.")]
    public void RejectsInvalidLanguageVersions(string languageVersion, string targetFramework, bool atLeastFrameworkDefault, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => TargetFrameworks.ResolveLanguageVersion(languageVersion, [targetFramework], atLeastFrameworkDefault));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void UsesTheLanguageVersionSupportedByAllTargetFrameworks()
    {
        Assert.AreEqual(14, TargetFrameworks.GetDefaultLanguageVersion(["net11.0-windows", "net10.0-windows"]));
    }

    [TestMethod]
    [DataRow("net8.0-windows")]
    [DataRow("net48")]
    [DataRow("netstandard2.0")]
    [DataRow("windows")]
    public void RejectsUnsupportedTargetFrameworks(string targetFramework)
    {
        Assert.ThrowsExactly<ArgumentException>(() => TargetFrameworks.GetMajorVersion(targetFramework));
    }

    [TestMethod]
    [DataRow("net10.0-windows", true)]
    [DataRow("net10.0-windows10.0.19041.0", true)]
    [DataRow("net10.0", false)]
    [DataRow("net10.0-android", false)]
    public void DetectsWindowsSpecificTargetFrameworks(string targetFramework, bool expected)
    {
        Assert.AreEqual(expected, TargetFrameworks.IsWindowsSpecific(targetFramework));
    }
}
