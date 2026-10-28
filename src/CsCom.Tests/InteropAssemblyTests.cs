using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCom.CodeGeneration;
using CsCom.InteropAssemblies;
using CsCom.Model;
using CsCom.Output;

namespace CsCom.Tests;

/// <summary>
/// Tests reading interop assemblies, mainly by comparing the test type library with the interop assembly that TlbImp
/// imported from it (TestAssets/Interop.CsComTest.dll): the snapshots list the differences, which are the information that
/// interop assemblies do not keep.
/// </summary>
[TestClass]
public sealed partial class InteropAssemblyTests : VerifyBase
{
    [TestMethod]
    public Task ReadsTheModelOfTheTypeLibrary()
    {
        var pipeline = new InteropPipeline();
        var fromTypeLibrary = Describe(pipeline, pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary));
        var fromAssembly = Describe(pipeline, pipeline.ReadTypeLibrary(TestEnvironment.TestInteropAssembly));

        return Verify(Compare(fromTypeLibrary, fromAssembly));
    }

    [TestMethod]
    public Task GeneratesTheCodeOfTheTypeLibrary()
    {
        var pipeline = new InteropPipeline();
        var options = new CodeGenerationOptions { Namespace = "Interop.CsComTest" };
        var fromTypeLibrary = Generate(pipeline, TestEnvironment.TestTypeLibrary, options);
        var fromAssembly = Generate(pipeline, TestEnvironment.TestInteropAssembly, options);

        return Verify(Compare(fromTypeLibrary, fromAssembly));
    }

    [TestMethod]
    public void RebuildsTheConventionsOfTlbImp()
    {
        var library = new InteropAssemblyReader().Read(TestEnvironment.TestInteropAssembly);

        Assert.AreEqual("CsComTest", library.Name);
        Assert.AreEqual(new Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1000"), library.Guid);
        Assert.AreEqual((1, 2), (library.MajorVersion, library.MinorVersion));
        Assert.AreEqual("stdole", library.ImportedLibraries.Single().Name);

        var calculator = Get<InterfaceDefinition>(library, "ICalculator");
        var locale = calculator.Methods.Single(static m => m.Name == "Locale");
        CollectionAssert.AreEqual(new[] { ParameterFlags.In | ParameterFlags.Lcid, ParameterFlags.Out | ParameterFlags.Retval }, locale.Parameters.Select(static p => p.Flags).ToList());

        var shape = Get<InterfaceDefinition>(library, "IShape");
        Assert.AreEqual(InterfaceKind.Dual, shape.InterfaceKind);
        Assert.AreEqual(7, shape.BaseVtableSlots);
        CollectionAssert.AreEquivalent(new[] { InvokeKind.PropertyGet, InvokeKind.PropertyPutRef }, shape.Methods.Where(static m => m.Name == "Parent").Select(static m => m.InvokeKind).ToList());
        CollectionAssert.AreEquivalent(new[] { InvokeKind.PropertyGet, InvokeKind.PropertyPut, InvokeKind.PropertyPutRef }, shape.Methods.Where(static m => m.Name == "Tag").Select(static m => m.InvokeKind).ToList());
        var newEnum = shape.Methods.Single(static m => m.Name == "_NewEnum");
        Assert.AreEqual((-4, InvokeKind.PropertyGet, MemberFlags.Restricted), (newEnum.MemberId, newEnum.InvokeKind, newEnum.Flags));

        var circle = Get<InterfaceDefinition>(library, "ICircle");
        Assert.AreSame(shape, ((UserDefinedTypeReference)circle.BaseInterface!).Definition);
        Assert.AreEqual(7 + shape.Methods.Count, circle.BaseVtableSlots);
        Assert.AreEqual("Radius", circle.Methods[0].Name);

        var events = Get<InterfaceDefinition>(library, "DShapeEvents");
        Assert.AreEqual(InterfaceKind.Dispatch, events.InterfaceKind);
        CollectionAssert.AreEqual(new[] { "Counter", "Description", "Color" }, events.Properties.Select(static p => p.Name).ToList());
        Assert.IsTrue(events.Properties.Single(static p => p.Name == "Description").IsReadOnly);

        var shapeClass = Get<CoClassDefinition>(library, "Shape");
        Assert.IsTrue(shapeClass.Flags.HasFlag(Model.TypeFlags.CanCreate));
        Assert.IsFalse(Get<CoClassDefinition>(library, "Calculator").Flags.HasFlag(Model.TypeFlags.CanCreate));
        CollectionAssert.AreEqual(
            new[] { ("IShape", true, false), ("ICircle", false, false), ("DShapeEvents", true, true) },
            shapeClass.Interfaces.Select(static i => (((UserDefinedTypeReference)i.Interface).Definition.Name, i.IsDefault, i.IsSource)).ToList());

        var number = Get<RecordDefinition>(library, "Number");
        Assert.IsTrue(number.IsUnion);
        Assert.AreEqual((8, 8), (number.Size, number.Alignment));
        var point = Get<RecordDefinition>(library, "Point");
        Assert.AreEqual((8, 4), (point.Size, point.Alignment));
    }

    [TestMethod]
    public void ReportsMissingReferences()
    {
        // An interop assembly whose structure uses an enum of another assembly, which is not next to it.
        string directory = TestEnvironment.CreateDirectory("interop-missing-reference");
        var enums = Compile("Enums", "namespace Enums { public enum Kind { First } }");
        string path = Path.Combine(directory, "Interop.Missing.dll");
        Compile(
            "Interop.Missing",
            """
            [assembly: System.Runtime.InteropServices.ImportedFromTypeLib("Missing")]
            namespace Missing { public struct Item { public Enums.Kind Kind; } }
            """,
            path,
            enums);

        var exception = Assert.ThrowsExactly<CsComException>(() => new InteropAssemblyReader().Read(path));
        StringAssert.StartsWith(exception.Message, "The assembly 'Enums, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null', which 'Interop.Missing.dll' references, was not found.");

        string references = TestEnvironment.CreateDirectory("interop-references");
        File.WriteAllBytes(Path.Combine(references, "Enums.dll"), enums.ToArray());
        var library = new InteropAssemblyReader(referencePaths: [references]).Read(path);
        Assert.AreEqual("Kind", ((UserDefinedTypeReference)Get<RecordDefinition>(library, "Item").Fields.Single().Type).Definition.Name);
    }

    [TestMethod]
    public Task ReadsHandWrittenDeclarations()
    {
        string path = Path.Combine(TestEnvironment.CreateDirectory("interop-hand-written"), "HandWritten.dll");
        Compile("HandWritten", HandWrittenSource, path);
        var logger = new WarningCollector<InteropAssemblyReader>();

        var library = new InteropAssemblyReader(logger).Read(path);

        using var writer = new StringWriter();
        ModelPrinter.Write(library, writer);
        writer.WriteLine();
        writer.WriteLine("Warnings:");
        logger.Messages.ForEach(writer.WriteLine);
        return Verify(writer.ToString());
    }

    [TestMethod]
    public void RejectsEmbeddedInteropTypes()
    {
        string path = Path.Combine(TestEnvironment.CreateDirectory("interop-embedded"), "Application.dll");
        Compile(
            "Application",
            """
            using System.Runtime.InteropServices;
            [ComImport, Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1011"), TypeIdentifier]
            public interface IShape { void _VtblGap1_7(); string Name { get; } }
            """,
            path);

        var exception = Assert.ThrowsExactly<CsComException>(() => new InteropAssemblyReader().Read(path));
        Assert.AreEqual(
            "'Application.dll' contains embedded interop types (Embed Interop Types), which have only the members its code uses: generate from the primary interop assembly or the type library of IShape.",
            exception.Message);
    }

    [TestMethod]
    public void RecognizesAssemblies()
    {
        Assert.IsTrue(InteropAssembly.IsAssembly(TestEnvironment.TestInteropAssembly));
        Assert.IsFalse(InteropAssembly.IsAssembly(TestEnvironment.TestTypeLibrary));
        Assert.IsFalse(InteropAssembly.IsAssembly(TestEnvironment.ScriptingRuntime));
        Assert.IsFalse(InteropAssembly.IsAssembly(Path.Combine(TestEnvironment.CreateDirectory("interop-none"), "missing.dll")));
    }

    // Hand-written declarations of the built-in COM interop, nested and internal as usual, with the defaults and the
    // constructs that type libraries cannot describe. Unused is not used by the COM types, so it is not imported.
    internal const string HandWrittenSource = """
        using System;
        using System.Runtime.InteropServices;
        using System.Text;

        namespace HandWritten
        {
            internal static class NativeMethods
            {
                [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000001"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
                internal interface IStorage
                {
                    void Open([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
                    [PreserveSig] int Read(IntPtr buffer, uint size, out uint read);
                    void Fill([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, int size);
                    void GetName(StringBuilder buffer, int size);
                    void Notify(Action callback, [MarshalAs(UnmanagedType.FunctionPtr)] Action function);
                    void Store(System.Collections.Generic.List<int> values, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Marshaler))] object custom);
                    void Configure(ref Settings settings, ref WideSettings wide, ref Offsets offsets, Mode mode, Size size);
                }

                [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000002"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
                internal interface IStorage2 : IStorage
                {
                    new void Open([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
                    [PreserveSig] new int Read(IntPtr buffer, uint size, out uint read);
                    new void Fill([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] data, int size);
                    new void GetName(StringBuilder buffer, int size);
                    new void Notify(Action callback, [MarshalAs(UnmanagedType.FunctionPtr)] Action function);
                    new void Store(System.Collections.Generic.List<int> values, [MarshalAs(UnmanagedType.CustomMarshaler, MarshalTypeRef = typeof(Marshaler))] object custom);
                    new void Configure(ref Settings settings, ref WideSettings wide, ref Offsets offsets, Mode mode, Size size);
                    void Flush([MarshalAs(UnmanagedType.U4)] bool force);
                }

                [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000003")]
                internal interface IShape
                {
                    string Name { get; set; }
                    double Area { get; }
                    bool Visible { get; set; }
                    void Move(int dx, [Optional] object scale, int dy = 0);
                    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)] string[] Names();
                    double[,] Matrix(int[] values);
                    [return: MarshalAs(UnmanagedType.IUnknown)] object Unknown();
                    IStorage2 Storage { get; }
                }

                [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000004"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
                internal interface DShapeEvents
                {
                    [DispId(1)] void Changed(string property);
                    [DispId(2)] int Count { get; }
                }

                [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000005")]
                internal class ShapeClass
                {
                }

                [ComImport, Guid("0F1D2C3B-0000-4000-8000-000000000003"), CoClass(typeof(ShapeClass))]
                internal interface Shape : IShape
                {
                }
            }

            public struct Settings
            {
                public string Name;
                public char Letter;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Code;
                [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public int[] Values;
                public bool Enabled;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct WideSettings
            {
                public string Name;
                public char Letter;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string Code;
            }

            [StructLayout(LayoutKind.Explicit)]
            public struct Offsets
            {
                [FieldOffset(0)] public int First;
                [FieldOffset(8)] public int Second;
            }

            [Flags]
            public enum Mode { None = 0, Read = 1, Write = 2 }

            public enum Size : byte { Small, Large }

            public struct Unused { public int Value; }

            public sealed class Marshaler : ICustomMarshaler
            {
                public static ICustomMarshaler GetInstance(string cookie) => new Marshaler();
                public void CleanUpManagedData(object managedObj) { }
                public void CleanUpNativeData(IntPtr pNativeData) { }
                public int GetNativeDataSize() => -1;
                public IntPtr MarshalManagedToNative(object managedObj) => IntPtr.Zero;
                public object MarshalNativeToManaged(IntPtr pNativeData) => null!;
            }
        }
        """;

    private static T Get<T>(TypeLibrary library, string name)
        where T : TypeDefinition => library.Types.OfType<T>().Single(t => t.Name == name);

    // The listing of `cscom list` by type, with the identifiers, flags and layouts that it does not show. The names of the
    // [retval] parameters, which interop assemblies do not keep, are replaced.
    private static Dictionary<string, List<string>> Describe(InteropPipeline pipeline, TypeLibrary library)
    {
        pipeline.TransformModel(library, new CodeGenerationOptions { Namespace = "Interop" }, sanitizeNames: false);
        using var writer = new StringWriter();
        ModelPrinter.Write(library, writer);

        var blocks = new Dictionary<string, List<string>>();
        List<string>? block = null;
        foreach (string line in writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(' '))
            {
                blocks[HeaderName().Match(line).Groups[1].Value] = block = [line];
            }
            else
            {
                block!.Add(RetvalName().Replace(line, "[out, retval] $1 retval)"));
            }
        }

        foreach (var type in library.Types)
        {
            var lines = blocks[type.Name];
            lines.Add($"    ~ guid {type.Guid?.ToString() ?? "none"}, flags {type.Flags}");
            switch (type)
            {
                case InterfaceDefinition @interface:
                    lines.Add($"    ~ base slots {@interface.BaseVtableSlots}");
                    lines.AddRange(@interface.Methods.Select(m => @interface.InterfaceKind == InterfaceKind.Vtable
                        ? $"    ~ {m.InvokeKind} {m.Name}: slot {m.VtableSlot}, {m.Flags}"
                        : $"    ~ {m.InvokeKind} {m.Name}: id {m.MemberId.ToString(CultureInfo.InvariantCulture)}, slot {m.VtableSlot}, {m.Flags}"));
                    lines.AddRange(@interface.Properties.Select(static p => $"    ~ property {p.Name}: id {p.DispId.ToString(CultureInfo.InvariantCulture)}, {p.Flags}"));
                    break;
                case RecordDefinition record:
                    lines.Add($"    ~ size {record.Size}, alignment {record.Alignment}, offsets {string.Join(" ", record.Fields.Select(static f => f.Offset))}");
                    break;
            }
        }

        return blocks;
    }

    // The generated files without the documentation and the blank lines.
    private static Dictionary<string, List<string>> Generate(InteropPipeline pipeline, string source, CodeGenerationOptions options) =>
        pipeline.GenerateCode(pipeline.ReadTypeLibrary(source), options).ToDictionary(
            static d => d.RelativePath.Replace('\\', '/'),
            static d => d.GetText().Split('\n').Select(static l => l.TrimEnd('\r')).Where(static l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)).ToList());

    // Lists the lines that only the type library (-) or only the interop assembly (+) has.
    private static string Compare(Dictionary<string, List<string>> fromTypeLibrary, Dictionary<string, List<string>> fromAssembly)
    {
        var report = new StringBuilder();
        foreach (string key in fromTypeLibrary.Keys.Union(fromAssembly.Keys).Order(StringComparer.Ordinal))
        {
            var removed = Except(fromTypeLibrary.GetValueOrDefault(key) ?? [], fromAssembly.GetValueOrDefault(key) ?? []);
            var added = Except(fromAssembly.GetValueOrDefault(key) ?? [], fromTypeLibrary.GetValueOrDefault(key) ?? []);
            if (removed.Count == 0 && added.Count == 0)
            {
                continue;
            }

            report.AppendLine(key);
            removed.ForEach(line => report.AppendLine("- " + line.Trim()));
            added.ForEach(line => report.AppendLine("+ " + line.Trim()));
        }

        return report.ToString();
    }

    private static List<string> Except(List<string> lines, List<string> others)
    {
        var remaining = others.GroupBy(static l => l).ToDictionary(static g => g.Key, static g => g.Count());
        var result = new List<string>();
        foreach (string line in lines)
        {
            if (remaining.TryGetValue(line, out int count) && count > 0)
            {
                remaining[line] = count - 1;
            }
            else
            {
                result.Add(line);
            }
        }

        return result;
    }

    private static MemoryStream Compile(string name, string source, string? path = null, MemoryStream? reference = null) =>
        TestCompiler.Compile(name, source, path, reference);

    [GeneratedRegex(@"^\S+ ([^\s:(]+)")]
    private static partial Regex HeaderName();

    [GeneratedRegex(@"\[out, retval\] (\S+) \w+\)")]
    private static partial Regex RetvalName();
}
