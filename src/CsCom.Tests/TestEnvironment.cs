using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: Parallelize(Scope = ExecutionScope.ClassLevel)]

namespace CsCom.Tests;

/// <summary>
/// The paths and settings shared by the tests.
/// </summary>
[TestClass]
public static class TestEnvironment
{
    private static readonly string RunDirectory = Path.Combine(Path.GetTempPath(), "cscom-tests", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The type library built from TestAssets/CsComTest.idl.</summary>
    public static string TestTypeLibrary { get; } = Path.Combine(AppContext.BaseDirectory, "TestAssets", "CsComTest.tlb");

    /// <summary>The interop assembly that TlbImp imported from TestAssets/CsComTest.tlb.</summary>
    public static string TestInteropAssembly { get; } = Path.Combine(AppContext.BaseDirectory, "TestAssets", "Interop.CsComTest.dll");

    /// <summary>The Microsoft Scripting Runtime, available on all Windows versions.</summary>
    public static string ScriptingRuntime { get; } = Path.Combine(Environment.SystemDirectory, "scrrun.dll");

    /// <summary>The OLE Automation type library.</summary>
    public static string StdOle { get; } = Path.Combine(Environment.SystemDirectory, "stdole2.tlb");

    /// <summary>The local feed with the CsCom.Runtime package built with the tests.</summary>
    public static string LocalPackages { get; } = typeof(TestEnvironment).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(static a => a.Key == "CsComLocalPackagesDir").Value!;

    /// <summary>
    /// The NuGet global packages folder used by the projects built by the tests, so that a CsCom.Runtime package
    /// with the same version restored earlier from another build is never used.
    /// </summary>
    public static string GlobalPackagesFolder { get; } = Path.Combine(RunDirectory, "packages");

    /// <summary>The version written into the headers of the generated files, so that the snapshots do not depend on the tool version.</summary>
    public const string ToolVersion = "1.0.0-test";

    [ModuleInitializer]
    internal static void Initialize()
    {
        Verifier.UseProjectRelativeDirectory("Snapshots");
    }

    /// <summary>Creates a new empty directory for a test.</summary>
    public static string CreateDirectory(string name)
    {
        string path = Path.Combine(RunDirectory, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(path);
        return path;
    }

    [AssemblyCleanup]
    public static void Cleanup()
    {
        try
        {
            if (Directory.Exists(RunDirectory))
            {
                Directory.Delete(RunDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Build servers may still hold some files.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
