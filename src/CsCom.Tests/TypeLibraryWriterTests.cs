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

    // A structure of a model that does not tell its alignment, such as one that is read from an assembly, has the default
    // alignment of 8 bytes on both platforms, not that of a pointer: a 32-bit process would see the double at offset 4.
    // OLE Automation converts the layouts of a library of the other platform when it loads it, so the layout of a 32-bit
    // library is compared with that of the same structure with the alignment.
    [TestMethod]
    [DataRow(TypeLibraryPlatform.Win32)]
    [DataRow(TypeLibraryPlatform.Win64)]
    public void AlignsTheFieldsOfStructuresAtEightBytesByDefault(TypeLibraryPlatform platform)
    {
        string directory = TestEnvironment.CreateDirectory("writer");
        var options = new TypeLibraryWriterOptions { Platform = platform };

        new TypeLibraryWriter().Write(CreateLibrary(alignment: 0), Path.Combine(directory, "Default.tlb"), options);
        new TypeLibraryWriter().Write(CreateLibrary(alignment: 8), Path.Combine(directory, "Aligned.tlb"), options);
        new TypeLibraryWriter().Write(CreateLibrary(alignment: 4), Path.Combine(directory, "Packed.tlb"), options);

        byte[] written = File.ReadAllBytes(Path.Combine(directory, "Default.tlb"));
        Assert.IsTrue(written.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "Aligned.tlb"))));
        Assert.IsFalse(written.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "Packed.tlb"))));
        var record = new TypeLibraryReader().Read(Path.Combine(directory, "Default.tlb")).Types.OfType<RecordDefinition>().Single();
        Assert.AreEqual((16, "0 8"), (record.Size, string.Join(" ", record.Fields.Select(static f => f.Offset))));

        static TypeLibrary CreateLibrary(int alignment)
        {
            var library = new TypeLibrary { Name = "Layout", Guid = new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A8000"), MajorVersion = 1 };
            var record = new RecordDefinition { Name = "Sample", OriginalName = "Sample", Alignment = alignment };
            record.Fields.Add(new FieldDefinition { Name = "Number", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4) });
            record.Fields.Add(new FieldDefinition { Name = "Value", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_R8) });
            library.Types.Add(record);
            return library;
        }
    }

    // An optional parameter that is not a VARIANT is optional without being counted among the optional VARIANTs, which
    // the count of a function describes: the last parameters, which a caller can leave out.
    [TestMethod]
    public unsafe void WritesOptionalParametersOfAllTypes()
    {
        var library = new TypeLibrary { Name = "Optional", Guid = new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A8100"), MajorVersion = 1 };
        var hresult = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT);
        var @interface = new InterfaceDefinition { Name = "IOptional", OriginalName = "IOptional", Guid = new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A8101") };
        var method = new MethodDefinition { Name = "Call", OriginalName = "Call", MemberId = 0x60010000, InvokeKind = InvokeKind.Method, ReturnType = hresult };
        foreach (var (name, type) in new (string, TypeReference)[]
        {
            ("text", new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_BSTR)),
            ("unknown", new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_UNKNOWN)),
            ("self", new PointerTypeReference(new UserDefinedTypeReference(@interface))),
            ("any", new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT)),
        })
        {
            method.Parameters.Add(new ParameterDefinition { Name = name, Type = type, Flags = ParameterFlags.In | ParameterFlags.Optional });
        }

        @interface.Methods.Add(method);
        library.Types.Add(@interface);
        string path = Path.Combine(TestEnvironment.CreateDirectory("writer"), "Optional.tlb");

        new TypeLibraryWriter().Write(library, path);

        var written = new TypeLibraryReader().Read(path).Types.OfType<InterfaceDefinition>().Single().Methods.Single();
        Assert.IsTrue(written.Parameters.All(static p => p.Flags == (ParameterFlags.In | ParameterFlags.Optional)), string.Join(", ", written.Parameters.Select(static p => $"{p.Name}: {p.Flags}")));

        Assert.AreEqual(0, TypeLibraries.Native.OleAut32.LoadTypeLibEx(path, TypeLibraries.Native.REGKIND.REGKIND_NONE, out var typeLib));
        typeLib!.GetTypeInfo(0, out var typeInfo);
        typeInfo.GetFuncDesc(0, out TypeLibraries.Native.FUNCDESC* function);
        short optional = function->cParamsOpt;
        typeInfo.ReleaseFuncDesc(function);
        Assert.AreEqual(1, optional);
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
