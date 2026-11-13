using CsCom.TypeLibraries;

namespace CsCom.Tests;

/// <summary>
/// Tests of finding type libraries by file, LIBID and ProgID, with the Scripting Runtime registered on all Windows versions.
/// </summary>
[TestClass]
public sealed class TypeLibrarySourceTests
{
    [TestMethod]
    [DataRow("{420B2830-E718-11CF-893D-00A0C9054228}")]
    [DataRow("420b2830-e718-11cf-893d-00a0c9054228")]
    [DataRow("{420B2830-E718-11CF-893D-00A0C9054228}:1.0")]
    [DataRow("Scripting.Dictionary")]
    [DataRow("Scripting.FileSystemObject")]
    public void ResolvesRegisteredTypeLibraries(string source)
    {
        Assert.AreEqual(TestEnvironment.ScriptingRuntime, TypeLibrarySource.Resolve(source), ignoreCase: true);
        Assert.IsFalse(TypeLibrarySource.IsFile(source));
    }

    [TestMethod]
    [DataRow(@"C:\Program Files\Microsoft Office\Root\Office16\EXCEL.EXE /automation", @"C:\Program Files\Microsoft Office\Root\Office16\EXCEL.EXE")]
    [DataRow(@"""C:\Program Files\App\app.exe"" -Embedding", @"C:\Program Files\App\app.exe")]
    [DataRow(@"C:\Windows\System32\scrrun.dll", @"C:\Windows\System32\scrrun.dll")]
    [DataRow(@"server", "server")]
    public void GetsThePathsOfClassServers(string command, string path) => Assert.AreEqual(path, TypeLibrarySource.GetServerPath(command));

    [TestMethod]
    public void ChoosesTheHighestRegisteredVersionWhoseFileExists()
    {
        // The registrations of the 64-bit view of the registry come first, then those of the 32-bit view.
        (int, int, string)[] registrations = [(1, 0, "x64-1.0"), (2, 0, "x64-2.0"), (2, 0, "x86-2.0"), (3, 0, "x86-3.0"), (4, 0, "gone-4.0")];

        // The highest version wins whichever view it is in; the registration of an uninstalled library is skipped.
        Assert.AreEqual("x86-3.0", TypeLibrarySource.ChooseRegistration(registrations, static path => !path.StartsWith("gone", StringComparison.Ordinal)));
        Assert.AreEqual("x64-2.0", TypeLibrarySource.ChooseRegistration(registrations, static path => path.EndsWith("2.0", StringComparison.Ordinal)));

        // Without an existing file, loading the highest version reports the missing file.
        Assert.AreEqual("gone-4.0", TypeLibrarySource.ChooseRegistration(registrations, static _ => false));
        Assert.IsNull(TypeLibrarySource.ChooseRegistration([], static _ => true));
    }

    [TestMethod]
    public void ResolvesFiles()
    {
        string directory = Path.GetDirectoryName(TestEnvironment.TestTypeLibrary)!;
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, TestEnvironment.TestTypeLibrary);

        Assert.AreEqual(TestEnvironment.TestTypeLibrary, TypeLibrarySource.Resolve(relative));
        Assert.AreEqual(TestEnvironment.ScriptingRuntime + @"\1", TypeLibrarySource.Resolve(TestEnvironment.ScriptingRuntime + @"\1"), "A resource index is kept.");
        Assert.IsTrue(TypeLibrarySource.IsFile(Path.Combine(directory, "CsComTest.tlb")));
    }

    [TestMethod]
    [DataRow(@"C:\does\not\exist.tlb", @"'C:\does\not\exist.tlb' is not a type library file, a registered LIBID or a ProgID.")]
    [DataRow("CsCom.Does.Not.Exist", "'CsCom.Does.Not.Exist' is not a type library file, a registered LIBID or a ProgID.")]
    [DataRow("{00000000-1111-2222-3333-444444444444}", "The type library {00000000-1111-2222-3333-444444444444} is not registered.")]
    [DataRow("{420B2830-E718-11CF-893D-00A0C9054228}:9.9", "The type library {420B2830-E718-11CF-893D-00A0C9054228} version 9.9 is not registered.")]
    [DataRow("{420B2830-E718-11CF-893D-00A0C9054228}:x", "'x' is not a valid type library version: major.minor is expected, e.g. 1.9.")]
    public void ReportsMissingTypeLibraries(string source, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => TypeLibrarySource.Resolve(source));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void NormalizesTheRecordedSources()
    {
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, TestEnvironment.TestTypeLibrary);

        Assert.AreEqual(TestEnvironment.TestTypeLibrary, TypeLibrarySource.Normalize(relative));
        Assert.AreEqual("{420B2830-E718-11CF-893D-00A0C9054228}", TypeLibrarySource.Normalize("420b2830-e718-11cf-893d-00a0c9054228"));
        Assert.AreEqual("{420B2830-E718-11CF-893D-00A0C9054228}:1.0", TypeLibrarySource.Normalize("{420b2830-e718-11cf-893d-00a0c9054228}:1.0"));
        Assert.AreEqual("Scripting.Dictionary", TypeLibrarySource.Normalize("Scripting.Dictionary"));
    }
}
