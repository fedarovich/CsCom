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
