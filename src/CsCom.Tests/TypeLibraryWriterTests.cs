using CsCom.Model;
using CsCom.TypeLibraries;

namespace CsCom.Tests;

/// <summary>
/// Tests of <see cref="TypeLibraryWriter"/>, which writes the type libraries that <see cref="TypeLibraryReader"/> reads.
/// </summary>
[TestClass]
public sealed class TypeLibraryWriterTests : VerifyBase
{
    // The differences between a type library and the type library written from its model: none, except what the model does
    // not keep.
    [TestMethod]
    [DataRow("CsComTest")]
    [DataRow("CsComDrawing")]
    [DataRow("scrrun")]
    public Task WritesTheTypeLibraryThatWasRead(string name)
    {
        string source = GetSource(name);
        var original = new TypeLibraryReader().Read(source);
        string path = Path.Combine(TestEnvironment.CreateDirectory("writer"), name + ".tlb");

        new TypeLibraryWriter().Write(original, path);
        var written = new TypeLibraryReader().Read(path);

        string report = ModelDescriber.DescribeLibrary(original) == ModelDescriber.DescribeLibrary(written)
            ? string.Empty
            : $"- {ModelDescriber.DescribeLibrary(original)}\n+ {ModelDescriber.DescribeLibrary(written)}\n";
        report += ModelDescriber.Compare(ModelDescriber.Describe(original, detailed: true), ModelDescriber.Describe(written, detailed: true));
        return Verify(report).UseParameters(name);
    }

    [TestMethod]
    public void WritesTheLayoutOfThePlatform()
    {
        var original = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);
        string path = Path.Combine(TestEnvironment.CreateDirectory("writer"), "CsComTest32.tlb");

        new TypeLibraryWriter().Write(original, path, new TypeLibraryWriterOptions { Platform = TypeLibraryPlatform.Win32 });
        var written = new TypeLibraryReader().Read(path);

        // OLE Automation converts the layouts of a library of another platform when it loads it, so only the size of the
        // file tells them apart.
        string path64 = Path.Combine(Path.GetDirectoryName(path)!, "CsComTest64.tlb");
        new TypeLibraryWriter().Write(original, path64);
        Assert.IsFalse(File.ReadAllBytes(path).AsSpan().SequenceEqual(File.ReadAllBytes(path64)));
        var shape = written.Types.OfType<InterfaceDefinition>().Single(static t => t.Name == "IShape");
        Assert.AreEqual(7, shape.BaseVtableSlots);
        Assert.AreEqual(7, shape.Methods[0].VtableSlot);
    }

    [TestMethod]
    public void RejectsTheLibraryOfOleAutomation()
    {
        var original = new TypeLibraryReader().Read(TestEnvironment.StdOle);
        string path = Path.Combine(TestEnvironment.CreateDirectory("writer"), "stdole2.tlb");

        var exception = Assert.ThrowsExactly<TypeLibraryException>(() => new TypeLibraryWriter().Write(original, path));

        StringAssert.Contains(exception.Message, "IUnknown");
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void ReplacesAnExistingFile()
    {
        var original = new TypeLibraryReader().Read(TestEnvironment.DrawingTypeLibrary);
        string path = Path.Combine(TestEnvironment.CreateDirectory("writer"), "CsComDrawing.tlb");
        File.WriteAllText(path, "not a type library");

        new TypeLibraryWriter().Write(original, path);

        Assert.AreEqual(original.Types.Count, new TypeLibraryReader().Read(path).Types.Count);
        Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    private static string GetSource(string name) => name switch
    {
        "CsComTest" => TestEnvironment.TestTypeLibrary,
        "CsComDrawing" => TestEnvironment.DrawingTypeLibrary,
        _ => TestEnvironment.ScriptingRuntime,
    };
}
