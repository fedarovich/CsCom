using CsCom.InteropAssemblies;
using CsCom.LibrarySets;
using CsCom.Model;
using CsCom.Output;
using CsCom.TypeLibraries;

namespace CsCom.Tests;

/// <summary>
/// Tests of <see cref="GeneratedComAssemblyReader"/>, which reads the type library to export from an assembly with
/// source-generated COM types. The test assemblies are compiled without the COM generator, which does not change the
/// metadata that is read, except for the members that it adds to derived interfaces.
/// </summary>
[TestClass]
public sealed class GeneratedComAssemblyTests : VerifyBase
{
    private static readonly string RuntimeAssembly = Path.Combine(AppContext.BaseDirectory, "CsCom.Runtime.dll");

    // The type library that is written for the assembly, as it is read back.
    [TestMethod]
    public Task ExportsTheTypesOfAnAssembly()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "Exported", ExportedSource);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();

        var library = new GeneratedComAssemblyReader(warnings, [RuntimeAssembly]).Read(assembly);
        string path = Path.Combine(directory, "Exported.tlb");
        new TypeLibraryWriter().Write(library, path);

        return Verify(Describe(new TypeLibraryReader().Read(path)) + Environment.NewLine + string.Join(Environment.NewLine, warnings.Messages));
    }

    [TestMethod]
    public void TakesTheIdentityOfTheLibraryFromTheAssemblyOrTheOptions()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "My.Library-1", """
            [assembly: System.Reflection.AssemblyVersion("3.4.5.6")]
            [assembly: System.Runtime.InteropServices.Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2100")]
            """);
        var reader = new GeneratedComAssemblyReader();

        var library = reader.Read(assembly);
        var overridden = reader.Read(assembly, new ExportedLibraryIdentity { Name = "Other", Guid = new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2101"), Version = (7, 1) });

        Assert.AreEqual(("My_Library_1", new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2100"), 3, 4), (library.Name, library.Guid, (int)library.MajorVersion, (int)library.MinorVersion));
        Assert.AreEqual(("Other", new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2101"), 7, 1), (overridden.Name, overridden.Guid, (int)overridden.MajorVersion, (int)overridden.MinorVersion));
        Assert.IsEmpty(library.Types);
    }

    [TestMethod]
    public void RequiresAnIdentifierOfTheLibrary()
    {
        string assembly = Compile(TestEnvironment.CreateDirectory("export"), "NoGuid", "public class C { }");

        var exception = Assert.ThrowsExactly<CsComException>(() => new GeneratedComAssemblyReader().Read(assembly));

        StringAssert.Contains(exception.Message, "[assembly: Guid(");
        Assert.AreEqual(1, new GeneratedComAssemblyReader().Read(assembly, new ExportedLibraryIdentity { Guid = Guid.NewGuid() }).MajorVersion, "0.0 is 1.0.");
    }

    [TestMethod]
    public void RejectsWhatATypeLibraryCannotDescribe()
    {
        const string header = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2200")]
            """;
        string directory = TestEnvironment.CreateDirectory("export");

        AssertRejected("NoInterfaceGuid", "[GeneratedComInterface] public partial interface INoGuid { void M(); }", "'INoGuid' has no [Guid]");
        AssertRejected(
            "SameNames",
            """
            namespace A { [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IThing { void M(); } }
            namespace B { [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2202")] public partial interface ITHING { void M(); } }
            """,
            "'A.IThing' have the same name in the type library");
        AssertRejected(
            "SameGuids",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { void M(); }
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface ITwo { void M(); }
            """,
            "'IOne' and 'ITwo' have the same [Guid]");
        AssertRejected(
            "SameDispIds",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { [DispId(1)] void M(); [DispId(1)] void N(); }
            """,
            "'IOne.M' and 'IOne.N' have the same [DispId] 1");
        AssertRejected(
            "PropertyDispIds",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { [DispId(1)] int get_P(); [DispId(2)] void set_P(int value); }
            """,
            "The accessors of the property 'IOne.P' have different [DispId] values");
        AssertRejected(
            "FrameworkStructure",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { void M(System.TimeSpan time); }
            """,
            "'IOne.M(time)' uses System.TimeSpan, which cannot be described in a type library");

        void AssertRejected(string name, string source, string expected)
        {
            string assembly = Compile(directory, name, header + source);
            var exception = Assert.ThrowsExactly<CsComException>(() => new GeneratedComAssemblyReader().Read(assembly));
            StringAssert.Contains(exception.Message, expected);
        }
    }

    // A class of the assembly that has the code generated from a type library implements an interface of the type library,
    // and an interface of the assembly uses its types: the exported library refers to them.
    [TestMethod]
    public Task RefersToTheTypesOfTheGeneratedCode()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "Server", ServerHeader + GenerateInterop(directory) + ServerSource);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();

        var library = new GeneratedComAssemblyReader(warnings, [RuntimeAssembly]).Read(assembly);
        string path = Path.Combine(directory, "Server.tlb");
        var imported = new TypeLibraryWriter().Write(library, path, new TypeLibraryWriterOptions { TypeLibraryPaths = [TestEnvironment.TestTypeLibrary] });

        CollectionAssert.AreEqual(new[] { TestEnvironment.TestTypeLibrary }, imported.ToArray());
        var written = ModelDescriber.ReadWithImports(path, TestEnvironment.TestTypeLibrary);
        CollectionAssert.AreEquivalent(new[] { "IRenderer", "Renderer" }, written.Types.Where(static t => t.ImportedFrom is null).Select(static t => t.Name).ToArray());
        return Verify(DescribeOwn(written) + Environment.NewLine + string.Join(Environment.NewLine, warnings.Messages));
    }

    [TestMethod]
    public void RequiresTheTypeLibrariesOfTheGeneratedCode()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "Server", ServerHeader + GenerateInterop(directory) + ServerSource);
        var library = new GeneratedComAssemblyReader(referencePaths: [RuntimeAssembly]).Read(assembly);
        string path = Path.Combine(directory, "Server.tlb");

        var exception = Assert.ThrowsExactly<TypeLibraryException>(() => new TypeLibraryWriter().Write(library, path));

        StringAssert.Contains(exception.Message, "The type library 'CsComTest' 1.2 ({6b1f2d2a-4c1e-4b71-9d0b-7b7c2e0a1000})");
        StringAssert.Contains(exception.Message, "is not registered");
        Assert.IsFalse(File.Exists(path));
    }

    // An assembly with the metadata of generated code whose types were renamed and whose support code is public, and an
    // assembly that references it.
    [TestMethod]
    public Task RefersToTheTypesOfAReferencedAssembly()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string interop = Compile(directory, "Interop.Fake", FakeInteropSource);
        string assembly = Compile(directory, "Client", ClientSource, interop);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();
        var reader = new GeneratedComAssemblyReader(warnings, [RuntimeAssembly]);

        var library = reader.Read(assembly);
        string path = Path.Combine(directory, "Client.tlb");
        new TypeLibraryWriter().Write(library, path, new TypeLibraryWriterOptions { TypeLibraryPaths = [TestEnvironment.TestTypeLibrary] });

        CollectionAssert.AreEqual(new[] { interop }, library.ImportedFiles.ToArray(), "The referenced assembly is an input of the export.");
        var written = ModelDescriber.ReadWithImports(path, TestEnvironment.TestTypeLibrary);
        Assert.AreEqual("CsComTest", written.Types.Single(static t => t.Name == "Color").ImportedFrom?.Name, "The renamed type is found by its original name.");

        // The assembly of the generated code has no types of its own to export: its interfaces are those of the type library
        // and of the support code.
        var own = reader.Read(interop, new ExportedLibraryIdentity { Guid = Guid.NewGuid() });
        Assert.IsEmpty(own.Types);
        return Verify(DescribeOwn(written) + Environment.NewLine + string.Join(Environment.NewLine, warnings.Messages));
    }

    // The code that is generated from the test type library, in a single file. The functions of its module are left out,
    // because they are only implemented by a source generator.
    private static string GenerateInterop(string directory)
    {
        string configuration = Path.Combine(directory, "cscom.xml");
        File.WriteAllText(configuration, """
            <CsCom>
              <ModelTransformers>
                <ExcludeType Type="^Kernel$" />
              </ModelTransformers>
            </CsCom>
            """);
        var result = new LibrarySetGenerator(new InteropPipeline()).Generate(
            [new LibrarySetReference { Source = TestEnvironment.TestTypeLibrary, ConfigurationPath = configuration }],
            new LibrarySetOptions { SingleFile = true, SingleFileName = "Interop.cs", ToolVersion = TestEnvironment.ToolVersion });
        return result.Documents.Single().GetText();
    }

    // The types of the library itself, and the names of the types that it refers to in other libraries.
    private static string DescribeOwn(TypeLibrary library)
    {
        using var writer = new StringWriter();
        writer.WriteLine(ModelDescriber.DescribeLibrary(library));
        var blocks = ModelDescriber.Describe(library, detailed: true);
        foreach (var type in library.Types.Where(static t => t.ImportedFrom is null))
        {
            blocks[type.Name].ForEach(writer.WriteLine);
        }

        writer.WriteLine("imported:");
        foreach (var type in library.Types.Where(static t => t.ImportedFrom is not null).OrderBy(static t => t.Name, StringComparer.Ordinal))
        {
            writer.WriteLine($"    {type.ImportedFrom!.Name}: {type.Kind} {type.Name}");
        }

        return writer.ToString();
    }

    internal static string Compile(string directory, string name, string source, params string[] references)
    {
        string path = Path.Combine(directory, name + ".dll");
        TestCompiler.Compile(name, source, path, referencePaths: [RuntimeAssembly, .. references]);
        return path;
    }

    // The listing of the library with everything that the export determines.
    internal static string Describe(TypeLibrary library)
    {
        using var writer = new StringWriter();
        writer.WriteLine(ModelDescriber.DescribeLibrary(library));
        foreach (var block in ModelDescriber.Describe(library, detailed: true).Values)
        {
            block.ForEach(writer.WriteLine);
        }

        return writer.ToString();
    }

    // The attributes of the assembly come first in a file, before the generated code.
    private const string ServerHeader = """
        [assembly: System.Runtime.InteropServices.Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A3000")]

        """;

    private const string ServerSource = """

        namespace Server
        {
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;
            using Interop.CsComTest;

            [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A3001")]
            public partial interface IRenderer
            {
                void Draw(
                    [MarshalUsing(typeof(global::CsCom.Runtime.ComObjectMarshaller<IShape>))] IShape shape,
                    Color color,
                    in Point point,
                    DShapeEvents events,
                    [MarshalUsing(typeof(global::CsCom.Runtime.SafeArrayMarshaller<Color>))] Color[] colors);

                [return: MarshalUsing(typeof(global::CsCom.Runtime.ComObjectMarshaller<ICircle>))]
                ICircle GetCircle(out global::Interop.stdole.Font font, Record record);
            }

            [GeneratedComClass]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A3010")]
            [ComSourceInterfaces(typeof(DShapeEvents), typeof(IShapeEventsDual))]
            public partial class Renderer : IRenderer, IShapeEventsVt
            {
                public void Draw(IShape shape, Color color, in Point point, DShapeEvents events, Color[] colors) { }
                public ICircle GetCircle(out global::Interop.stdole.Font font, Record record) => throw null;
                public void Moved(int X, int Y) { }
                public void Closing(ref bool Cancel) { }
                public void Renamed(string Name) { }
                public string Describe(object Value, out int length) => throw null;
            }
        }
        """;

    private const string FakeInteropSource = """
        using System;
        using System.Reflection;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;

        [assembly: AssemblyMetadata("CsCom.TypeLibrary", "Fake.Library|CsComTest|{6b1f2d2a-4c1e-4b71-9d0b-7b7c2e0a1000}|1.2")]
        [assembly: AssemblyMetadata("CsCom.TypeLibrary.Type", "Fake.Library.ShapeColor|Color")]
        [assembly: AssemblyMetadata("CsCom.TypeLibrary.Type", "Fake.Library.IFigure|IShape")]
        [assembly: AssemblyMetadata("CsCom.SupportNamespace", "Fake.ComSupport")]

        namespace Fake.ComSupport
        {
            [GeneratedComInterface, Guid("00020400-0000-0000-C000-000000000046")]
            public partial interface IDispatch
            {
                void GetTypeInfoCount(out uint count);
            }

            [GeneratedComInterface, Guid("B196B286-BAB4-101A-B69C-00AA00341D07")]
            public partial interface IConnectionPoint
            {
                void Unadvise(uint cookie);
            }

            public static class ComObjectMarshaller<T>
            {
            }
        }

        namespace Fake.Library
        {
            public enum ShapeColor { Red }

            [GeneratedComInterface, Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1011")]
            public partial interface IFigure : Fake.ComSupport.IDispatch
            {
                void Move();
            }
        }
        """;

    private const string ClientSource = """
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;
        using Fake.ComSupport;
        using Fake.Library;

        [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A4000")]

        namespace Client
        {
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A4001")]
            public partial interface IPainter : IFigure
            {
                void Paint(ShapeColor color, [MarshalUsing(typeof(ComObjectMarshaller<IFigure>))] IFigure figure, IConnectionPoint point);

                [return: MarshalUsing(typeof(ComObjectMarshaller<IDispatch>))]
                IDispatch GetDispatch();
            }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A4002")]
            public partial interface IAutomation : IDispatch
            {
                void Run();
            }
        }
        """;

    private const string ExportedSource = """
        using System;
        using System.ComponentModel;
        using System.Reflection;
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;
        using CsCom.Runtime;

        [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2000")]
        [assembly: TypeLibVersion(2, 5)]
        [assembly: AssemblyDescription("The exported test library")]

        namespace Exported
        {
            [Description("The levels")]
            public enum Level
            {
                Low,
                [Description("The high level")] High = 5,
            }

            public enum Small : byte { One = 1 }

            public enum Unused { None }

            public struct Size
            {
                public int Width;
                public int Height;
            }

            [StructLayout(LayoutKind.Explicit)]
            public struct Number
            {
                [FieldOffset(0)] public int AsInteger;
                [FieldOffset(0)] public double AsDouble;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            public unsafe struct Item
            {
                public Size Size;
                public ComBstrValue Name;
                public ComVariant Data;
                public fixed short Codes[4];
                public Size* Next;
                public Guid Id;
                [MarshalAs(UnmanagedType.VariantBool)] public bool Flag;
                public Number Number;
                public Buffer Values;
                public nint Handle;
                public decimal Amount;
                public Level Level;
            }

            [System.Runtime.CompilerServices.InlineArray(3)]
            public struct Buffer
            {
                private double _element0;
            }

            [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2001")]
            [Description("A calculator")]
            public unsafe partial interface ICalculator
            {
                int Add(int a, int b);

                [PreserveSig]
                int Raw(int value);

                [PreserveSig]
                [return: MarshalAs(UnmanagedType.Error)]
                int RawResult();

                void Sizes(Size size, in Size input, ref Size both, out Size result);

                string Text(string bstr, [MarshalAs(UnmanagedType.LPWStr)] string wide, [MarshalUsing(typeof(Utf8StringMarshaller))] string utf8, ComBstr wrapper);

                void Values(
                    [MarshalUsing(typeof(VariantMarshaller))] object variant,
                    [MarshalUsing(typeof(ComObjectMarshaller<object>))] object unknown,
                    [MarshalUsing(typeof(CurrencyMarshaller))] decimal money,
                    decimal number,
                    [MarshalUsing(typeof(DateMarshaller))] DateTime date,
                    [MarshalAs(UnmanagedType.VariantBool)] bool flag,
                    bool raw,
                    ComVariant value,
                    Guid id,
                    in Guid reference);

                void Others(Level level, Small small, nint handle, void* pointer, int* numbers, [MarshalAs(UnmanagedType.Error)] int code, ComBool flag, ComDate date, ComCurrency money);

                [return: MarshalUsing(typeof(SafeArrayMarshaller<string>))]
                string[] Arrays(
                    [MarshalUsing(typeof(SafeArrayMarshaller<int>))] int[] numbers,
                    [MarshalUsing(typeof(UnknownSafeArrayMarshaller))] object[] objects,
                    [MarshalUsing(typeof(SafeArrayMarshaller))] Array any,
                    ComSafeArray<double> wrapped,
                    [MarshalUsing(typeof(SafeArrayMarshaller<Level>))] ref Level[] levels,
                    [MarshalUsing(CountElementName = nameof(count))] int[] counted,
                    int count);

                void Defaults(
                    int a = 5,
                    string text = "x",
                    Level level = Level.High,
                    [MarshalUsing(typeof(OptionalVariantMarshaller))] object any = null,
                    double factor = 1.5,
                    [MarshalAs(UnmanagedType.VariantBool)] bool flag = true,
                    [MarshalUsing(typeof(CurrencyMarshaller))] decimal money = 2.5m);

                Item GetItem();
            }

            [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2002")]
            public partial interface IShape : IDispatch
            {
                [DispId(0)]
                string get_Name();

                void set_Name(string value);

                [return: MarshalUsing(typeof(ComObjectMarshaller<IShape>))]
                IShape get_Parent();

                void putref_Parent([MarshalUsing(typeof(UniqueComObjectMarshaller<IShape>))] IShape value);

                [DispId(42), TypeLibFunc(TypeLibFuncFlags.FHidden), Description("Moves the shape")]
                void Move(int dx, int dy);

                [return: MarshalUsing(typeof(ComObjectMarshaller<object>))]
                object get__NewEnum();

                ICalculator GetCalculator();

                void get_Bad();

                [return: MarshalUsing(typeof(ComObjectMarshaller<IDispatch>))]
                IDispatch Dispatch([MarshalUsing(typeof(ComInterfaceMarshaller<IEnumVARIANT>))] IEnumVARIANT enumerator, IComparable comparable);
            }

            [GeneratedComInterface]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2003")]
            public partial interface ICircle : IShape
            {
                double get_Radius();

                void set_Radius(double value);

                // The COM generator adds the members of the base interfaces like this one.
                new string get_Name() => throw null;
            }

            [GeneratedComInterface]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2004")]
            [TypeLibType(TypeLibTypeFlags.FHidden)]
            public partial interface IShapeEvents
            {
                void Changed(int kind);
            }

            [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2005")]
            public partial interface ISimple : IShapeEvents
            {
                void Run(string wide);
            }

            [GeneratedComInterface]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2006")]
            internal partial interface IHidden
            {
                void Hide();
            }

            [ComVisible(false)]
            [GeneratedComInterface]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2007")]
            public partial interface IInvisible
            {
                void Hide();
            }

            [GeneratedComClass]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2010")]
            [ComDefaultInterface(typeof(ISimple))]
            [ComSourceInterfaces(typeof(IShapeEvents), typeof(ICircle))]
            [Description("A sink")]
            public partial class Sink : IInvisible, ISimple, IHidden
            {
                public void Changed(int kind) { }
                public void Run(string wide) { }
                public void Hide() { }
            }

            [GeneratedComClass]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2011")]
            [ComSourceInterfaces("Exported.IShapeEvents\0Exported.Missing")]
            public partial class Factory : IShapeEvents
            {
                private Factory() { }
                public void Changed(int kind) { }
            }

            [GeneratedComClass]
            public partial class Unidentified : IShapeEvents
            {
                public void Changed(int kind) { }
            }
        }
        """;
}
