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
}
