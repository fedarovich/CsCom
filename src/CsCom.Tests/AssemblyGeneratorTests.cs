using CsCom.Output;

namespace CsCom.Tests;

[TestClass]
public sealed class AssemblyGeneratorTests
{
    private const string Project = @"C:\Temp\cscom\0123\Interop.Scripting.csproj";

    [TestMethod]
    [DataRow($"{Project} : error NU1603: Interop.Scripting depends on CsCom.Runtime (>= 0.1.0) but CsCom.Runtime 0.1.0 was not found. CsCom.Runtime 1.0.0 was resolved instead.")]
    [DataRow($"{Project} : error NU1101: Unable to find package CsCom.Runtime.Dynamic. No packages exist with this id in source(s): nuget.org")]
    [DataRow($"{Project} : error NU1102: Unable to find package CsCom.Runtime with version (>= 2.0.0)")]
    public void RecognizesMissingRuntimePackages(string error) =>
        Assert.IsTrue(AssemblyGenerator.IsRuntimePackageMissing([error]));

    [TestMethod]
    [DataRow($"{Project} : error NU1101: Unable to find package Contoso.Interop. No packages exist with this id in source(s): nuget.org")]
    [DataRow(@"C:\Temp\cscom\0123\Generated\Dictionary.cs(12,5): error CS0246: The type or namespace name 'CsCom' could not be found")]
    public void IgnoresOtherErrors(string error) =>
        Assert.IsFalse(AssemblyGenerator.IsRuntimePackageMissing([error]));

    [TestMethod]
    public void FindsTheDotNetExecutableByItsFullPath()
    {
        // Windows looks for a bare name in the current directory first, so the executable is never run by its name.
        var variables = new Dictionary<string, string?>
        {
            ["PATH"] = @".;relative;""C:\Tools"";C:\Program Files\dotnet;C:\Other",
        };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\dotnet\dotnet.exe", @"C:\Other\dotnet.exe", @"C:\Sdk\dotnet.exe", @"C:\Host\dotnet.exe" };

        Assert.AreEqual(@"C:\Program Files\dotnet\dotnet.exe", DotNetCli.FindDotNet(variables.GetValueOrDefault, files.Contains));

        // DOTNET_ROOT is where the runtime of the tool is, which can be an installation without the SDK, so the directories
        // of PATH come first and it is only used when they have no dotnet.
        variables["DOTNET_ROOT"] = @"C:\Sdk";
        Assert.AreEqual(@"C:\Program Files\dotnet\dotnet.exe", DotNetCli.FindDotNet(variables.GetValueOrDefault, files.Contains));
        variables["PATH"] = @"C:\Tools";
        Assert.AreEqual(@"C:\Sdk\dotnet.exe", DotNetCli.FindDotNet(variables.GetValueOrDefault, files.Contains));

        // The host of the .NET CLI that runs the tool wins; a host that is not a full path or does not exist is ignored.
        variables["DOTNET_HOST_PATH"] = @"C:\Host\dotnet.exe";
        Assert.AreEqual(@"C:\Host\dotnet.exe", DotNetCli.FindDotNet(variables.GetValueOrDefault, files.Contains));
        variables["DOTNET_HOST_PATH"] = "dotnet.exe";
        Assert.AreEqual(@"C:\Sdk\dotnet.exe", DotNetCli.FindDotNet(variables.GetValueOrDefault, files.Contains));

        Assert.IsNull(DotNetCli.FindDotNet(static _ => null, files.Contains));
    }
}
