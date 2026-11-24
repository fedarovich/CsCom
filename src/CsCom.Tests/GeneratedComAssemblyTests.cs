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

    // [assembly: ComVisible(false)], which older project templates have, hides all the types.
    [TestMethod]
    public void TellsWhenComVisibleHidesAllTheTypes()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "Hidden", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2300")]
            [assembly: ComVisible(false)]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2301")]
            public partial interface IHidden { void M(); }

            [GeneratedComClass, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2302")]
            public partial class Hidden : IHidden { public void M() { } }

            [ComVisible(true), GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2303")]
            internal partial interface IInternal { void M(); }

            // A class without a [Guid] is not exported whether it is visible or not.
            [GeneratedComClass]
            public partial class Helper : IHidden { public void M() { } }
            """);
        var warnings = new WarningCollector<TypeLibraryExporter>();

        var result = new TypeLibraryExporter(new CollectorFactory(warnings)).Export(new TypeLibraryExportRequest { AssemblyPath = assembly });

        Assert.IsEmpty(result.Library.Types);
        StringAssert.Contains(warnings.Messages.Single(), "[ComVisible(false)], on the assembly or on the types, hides its 2 public COM interfaces and classes");

        // Without COM types, the library is empty for another reason.
        string empty = Compile(directory, "Empty", """[assembly: System.Runtime.InteropServices.Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2310")]""");
        warnings.Messages.Clear();
        new TypeLibraryExporter(new CollectorFactory(warnings)).Export(new TypeLibraryExportRequest { AssemblyPath = empty });
        StringAssert.Contains(warnings.Messages.Single(), "has no public [GeneratedComInterface] interface");
    }

    // The members of an interface of a type library have different names, so the overloads are numbered, as by TlbExp.
    [TestMethod]
    public void NumbersTheOverloads()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "Overloads", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2400")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2401")]
            public partial interface IMath
            {
                int Add(int a, int b);
                double Add(double a, double b);
                void Reset();
                void add(short value);
            }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2402")]
            public partial interface IMoreMath : IMath
            {
                void Add(long value);
            }
            """);

        var library = new GeneratedComAssemblyReader().Read(assembly);
        string path = Path.Combine(directory, "Overloads.tlb");
        new TypeLibraryWriter().Write(library, path);

        var written = new TypeLibraryReader().Read(path);
        CollectionAssert.AreEqual(
            new[] { "Add", "Add_2", "Reset", "add_3" },
            written.Types.OfType<InterfaceDefinition>().Single(static t => t.Name == "IMath").Methods.Select(static m => m.OriginalName).ToArray());
        Assert.AreEqual("Add", written.Types.OfType<InterfaceDefinition>().Single(static t => t.Name == "IMoreMath").Methods.Single().OriginalName);
    }

    // The type library marshaller is only made the proxy of the interfaces whose parameters it is known to marshal.
    [TestMethod]
    public void MarksTheInterfacesWithStructuresByReferenceForAutomation()
    {
        string assembly = Compile(TestEnvironment.CreateDirectory("export"), "Structures", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2500")]

            public struct Size { public int Width; public int Height; }

            public struct Empty { }

            public struct Info { [MarshalAs(UnmanagedType.LPWStr)] public string Name; public nint Handle; }

            public struct Outer { public int Count; public Info Info; }

            public struct Rectangle { public Size Size; public double Scale; }

            [StructLayout(LayoutKind.Explicit)]
            public struct Either { [FieldOffset(0)] public int Number; [FieldOffset(0)] public float Real; }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2504")]
            public partial interface IInfo { void M(in Info info); }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2505")]
            public partial interface IOuter { void M(in Outer outer); }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2506")]
            public partial interface IEither { void M(in Either either); }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2507")]
            public partial interface IRectangle { void M(in Rectangle rectangle); }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2501")]
            public partial interface IByValue { void M(Size size); }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2502")]
            public partial interface IGuidByValue { void M(System.Guid id); }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2503")]
            public partial interface IByReference { Size M(in Size size, in System.Guid id, ref Empty empty); }
            """);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();

        var library = new GeneratedComAssemblyReader(warnings).Read(assembly);

        CollectionAssert.AreEqual(
            new[] { "IByReference", "IRectangle" },
            library.Types.OfType<InterfaceDefinition>().Where(static t => t.Flags.HasFlag(TypeFlags.OleAutomation)).Select(static t => t.Name).Order().ToArray());

        // A structure without fields has a size of one byte, which is not a layout that was declared.
        StringAssert.Contains(warnings.Messages.Single(), "The structure 'Empty' has no fields");

        // A type library can have a structure without fields.
        string path = Path.Combine(Path.GetDirectoryName(assembly)!, "Structures.tlb");
        new TypeLibraryWriter().Write(library, path);
        Assert.IsEmpty(new TypeLibraryReader().Read(path).Types.OfType<RecordDefinition>().Single(static t => t.Name == "Empty").Fields);
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
            "MemberDispIds",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { [DispId(1)] int get_Count(); void set_Count(int value); [DispId(1)] void Reset(); }
            """,
            "'IOne.Count' and 'IOne.Reset' have the same [DispId] 1");
        AssertRejected(
            "PropertyAndMethod",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { int get_Count(); void Count(); }
            """,
            "'IOne.Count' is both a property and a method");
        AssertRejected(
            "OverloadedAccessors",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { int get_Item(int index); int get_Item(string name); }
            """,
            "The property 'IOne.Item' has overloaded accessors");
        AssertRejected(
            "OverloadDispIds",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { [DispId(1)] void M(); [DispId(1)] void M(int value); }
            """,
            "'IOne.M' and 'IOne.M_2' have the same [DispId] 1");
        AssertRejected(
            "OverloadName",
            """
            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2201")] public partial interface IOne { void M(); void M_2(); void M(int value); }
            """,
            "The overload of 'IOne.M' cannot be exported as 'M_2'");
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

        var catalog = new TypeLibraryCatalog([TestEnvironment.TestTypeLibrary]);
        var library = new GeneratedComAssemblyReader(warnings, [RuntimeAssembly]).Read(assembly, catalog: catalog);
        string path = Path.Combine(directory, "Server.tlb");
        var imported = new TypeLibraryWriter().Write(library, path, new TypeLibraryWriterOptions { Catalog = catalog });

        // IOwnShape and OwnShape are written into the namespace of the generated code, whose type library does not have them.
        CollectionAssert.AreEqual(new[] { TestEnvironment.TestTypeLibrary }, imported.ToArray());
        var written = ModelDescriber.ReadWithImports(path, TestEnvironment.TestTypeLibrary);
        CollectionAssert.AreEquivalent(
            new[] { "IRenderer", "Renderer", "IOwnShape", "OwnShape" },
            written.Types.Where(static t => t.ImportedFrom is null).Select(static t => t.Name).ToArray());
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

    // The registration of a type library can remain when the library was uninstalled.
    [TestMethod]
    public void ToleratesARegisteredTypeLibraryThatCannotBeLoaded()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "Stale", """
            using System.Reflection;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7100")]
            [assembly: AssemblyMetadata("CsCom.TypeLibrary", "Interop.Gone|Gone|{7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7101}|1.0")]

            namespace Interop.Gone
            {
                [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7102")]
                public partial interface IGone { void M(); }
            }

            namespace Stale
            {
                [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7103")]
                public partial interface IUnrelated { void M(); }
            }
            """);
        var warnings = new WarningCollector<TypeLibraryCatalog>();
        string missing = Path.Combine(directory, "Gone.tlb");
        TypeLibraryCatalog Catalog() => new([], warnings, reference => reference.Name == "Gone" ? missing : TypeLibrarySource.FindRegistered(reference.Guid, reference.MajorVersion, reference.MinorVersion));

        // No type of the library is used, so the export does not need it.
        var library = new GeneratedComAssemblyReader().Read(assembly, catalog: Catalog());
        Assert.AreEqual("IUnrelated", library.Types.Single().Name);
        StringAssert.Contains(warnings.Messages.Single(), "The registered type library 'Gone' 1.0 cannot be loaded");
        new TypeLibraryWriter().Write(library, Path.Combine(directory, "Stale.tlb"));

        // A type of it that is used needs it.
        string user = Compile(directory, "User", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7110")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7111")]
            public partial interface IUser { void M(Interop.Gone.IGone gone); }
            """, assembly);
        var catalog = Catalog();
        library = new GeneratedComAssemblyReader().Read(user, catalog: catalog);
        var exception = Assert.ThrowsExactly<TypeLibraryException>(
            () => new TypeLibraryWriter().Write(library, Path.Combine(directory, "User.tlb"), new TypeLibraryWriterOptions { Catalog = catalog }));
        StringAssert.Contains(exception.Message, "is registered, but cannot be loaded (");
    }

    // The type libraries that tell which types are those of the library are inputs of the export, used or not.
    [TestMethod]
    public void ReportsTheTypeLibrariesThatWereConsultedAsInputs()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string assembly = Compile(directory, "Own", """
            using System.Reflection;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7200")]
            [assembly: AssemblyMetadata("CsCom.TypeLibrary", "Interop.CsComTest|CsComTest|{6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1000}|1.2")]

            namespace Interop.CsComTest
            {
                [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7201")]
                public partial interface IOwn { void M(); }
            }
            """);

        var result = new TypeLibraryExporter().Export(new TypeLibraryExportRequest { AssemblyPath = assembly, TypeLibraryPaths = [TestEnvironment.TestTypeLibrary] });

        Assert.AreEqual("IOwn", result.Library.Types.Single().Name);
        Assert.IsEmpty(result.Library.ImportedLibraries);
        CollectionAssert.AreEquivalent(new[] { assembly, TestEnvironment.TestTypeLibrary }, result.InputFiles.ToArray());
    }

    // Many assemblies have a [Guid] that identifies no type library: their types are not taken for types of one, unless
    // the type library with that identifier is found.
    [TestMethod]
    public void RefersToTheTypeLibraryOfAReferencedAssemblyThatHasOne()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7000")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7001")]
            public partial interface ILogger
            {
                void Log(Exported.Level level, Exported.ISimple simple);
            }
            """;
        string directory = TestEnvironment.CreateDirectory("export");
        string referenced = Compile(directory, "Exported", ExportedSource);
        string assembly = Compile(directory, "Logger", source, referenced);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();
        var reader = new GeneratedComAssemblyReader(warnings, [RuntimeAssembly]);

        // Without the type library of the referenced assembly, its types are exported as the closest types.
        var library = reader.Read(assembly, catalog: new TypeLibraryCatalog());
        var method = library.Types.OfType<InterfaceDefinition>().Single().Methods.Single();
        Assert.AreEqual("VT_I4 level, VT_UNKNOWN simple", string.Join(", ", method.Parameters));
        Assert.IsEmpty(library.ImportedLibraries);
        Assert.IsTrue(warnings.Messages.Any(static m => m.Contains("'ILogger.Log(level)' uses Exported.Level, which is not supported; it is exported as long", StringComparison.Ordinal)), string.Join(Environment.NewLine, warnings.Messages));
        new TypeLibraryWriter().Write(library, Path.Combine(directory, "Logger.tlb"));

        // With it, the library refers to them.
        string exported = Path.Combine(directory, "Exported.tlb");
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader(referencePaths: [RuntimeAssembly]).Read(referenced), exported);
        var catalog = new TypeLibraryCatalog([exported]);
        library = reader.Read(assembly, catalog: catalog);
        method = library.Types.OfType<InterfaceDefinition>().Single(static t => t.ImportedFrom is null).Methods.Single();
        Assert.AreEqual("Level level, ISimple* simple", string.Join(", ", method.Parameters));
        Assert.AreEqual("Exported", library.ImportedLibraries.Single().Name);
        CollectionAssert.AreEqual(new[] { exported }, new TypeLibraryWriter().Write(library, Path.Combine(directory, "Logger2.tlb"), new TypeLibraryWriterOptions { Catalog = catalog }).ToArray());
    }

    // A referenced assembly can have both code generated from a type library and COM types of its own, which are in the
    // type library that is exported from it.
    [TestMethod]
    public void RefersToTheOwnTypesOfAReferencedAssemblyWithGeneratedCode()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string referenced = Compile(directory, "Shared", """
            using System.Reflection;
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7300")]
            [assembly: AssemblyMetadata("CsCom.TypeLibrary", "Interop.CsComTest|CsComTest|{6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1000}|1.2")]

            namespace Shared
            {
                public enum Mode { Off, On }

                [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7301")]
                public partial interface IShared { void M(Mode mode); }
            }
            """);
        string assembly = Compile(directory, "Consumer", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7310")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7311")]
            public partial interface IConsumer { void Use(Shared.Mode mode, Shared.IShared shared); }
            """, referenced);
        string exported = Path.Combine(directory, "Shared.tlb");
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader().Read(referenced), exported);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();

        var library = new GeneratedComAssemblyReader(warnings).Read(assembly, catalog: new TypeLibraryCatalog([exported]));

        var method = library.Types.OfType<InterfaceDefinition>().Single(static t => t.ImportedFrom is null).Methods.Single();
        Assert.AreEqual("Mode mode, IShared* shared", string.Join(", ", method.Parameters));
        Assert.AreEqual("Shared", library.ImportedLibraries.Single().Name);
        Assert.IsEmpty(warnings.Messages);
    }

    // The type library of a referenced assembly can have another identifier than the assembly: it is then found among
    // the specified files by the interfaces of the assembly.
    [TestMethod]
    public void FindsTheTypeLibraryOfAReferencedAssemblyByItsInterfaces()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string referenced = Compile(directory, "Shared", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            public enum Mode { Off, On }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7501")]
            public partial interface IShared { void M(Mode mode); }
            """);
        string assembly = Compile(directory, "Consumer", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7510")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7511")]
            public partial interface IConsumer { void Use(Mode mode, IShared shared); }
            """, referenced);
        string exported = Path.Combine(directory, "Other.tlb");
        var identity = new ExportedLibraryIdentity { Name = "Other", Guid = new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7500"), Version = (2, 1) };
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader().Read(referenced, identity), exported);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();
        var catalog = new TypeLibraryCatalog([exported]);

        var library = new GeneratedComAssemblyReader(warnings).Read(assembly, catalog: catalog);

        var method = library.Types.OfType<InterfaceDefinition>().Single(static t => t.ImportedFrom is null).Methods.Single();
        Assert.AreEqual("Mode mode, IShared* shared", string.Join(", ", method.Parameters));
        var imported = library.ImportedLibraries.Single();
        Assert.AreEqual(("Other", identity.Guid, 2, 1), (imported.Name, (Guid?)imported.Guid, (int)imported.MajorVersion, (int)imported.MinorVersion));
        Assert.IsEmpty(warnings.Messages);
        new TypeLibraryWriter().Write(library, Path.Combine(directory, "Consumer.tlb"), new TypeLibraryWriterOptions { Catalog = catalog });
    }

    // A class of a referenced assembly is not the late-bound wrapper of a dispinterface, as a class of generated code is.
    [TestMethod]
    public void DoesNotTakeTheClassesOfAReferencedAssemblyForDispinterfaces()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string referenced = Compile(directory, "Widgets", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7600")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7601")]
            public partial interface IWidget { void M(); }

            [GeneratedComClass, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7602")]
            public partial class Widget : IWidget { public void M() { } }
            """);
        string assembly = Compile(directory, "Source", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7610")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7611")]
            public partial interface ISource { void M(); }

            [GeneratedComClass, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7612"), ComSourceInterfaces(typeof(Widget))]
            public partial class Source : ISource { public void M() { } }
            """, referenced);
        string exported = Path.Combine(directory, "Widgets.tlb");
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader().Read(referenced), exported);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();
        var catalog = new TypeLibraryCatalog([exported]);

        var library = new GeneratedComAssemblyReader(warnings).Read(assembly, catalog: catalog);

        Assert.IsFalse(library.Types.OfType<CoClassDefinition>().Single().Interfaces.Any(static i => i.IsSource));
        StringAssert.Contains(warnings.Messages.Single(), "The source interface 'Widget' of the class 'Source' is not a COM interface");
        new TypeLibraryWriter().Write(library, Path.Combine(directory, "Source.tlb"), new TypeLibraryWriterOptions { Catalog = catalog });
    }

    // What the type library of a referenced assembly has beats the name of the assembly.
    [TestMethod]
    public void RefersToTheTypesOfAReferencedAssemblyNamedLikeThoseOfDotNet()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string referenced = Compile(directory, "Microsoft.Shared", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7700")]

            public enum Level { Low, High }

            public struct Size { public int Width; public int Height; }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7701")]
            public partial interface IShared { void M(Level level, in Size size); }
            """);
        string assembly = Compile(directory, "Consumer", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7710")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7711")]
            public partial interface IConsumer { void Use(Level level, in Size size); }
            """, referenced);
        string exported = Path.Combine(directory, "Microsoft.Shared.tlb");
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader().Read(referenced), exported);

        var library = new GeneratedComAssemblyReader().Read(assembly, catalog: new TypeLibraryCatalog([exported]));

        var method = library.Types.OfType<InterfaceDefinition>().Single(static t => t.ImportedFrom is null).Methods.Single();
        Assert.AreEqual("Level level, Size* size", string.Join(", ", method.Parameters));
        Assert.AreEqual(2, library.Types.Count(static t => t.ImportedFrom?.Name == "Microsoft_Shared"));
    }

    // An interface of a referenced assembly is not defined again in the library: a class does not get it, and an
    // interface cannot derive from it, unless the type library of that assembly is found.
    [TestMethod]
    public void DoesNotDefineTheInterfacesOfAReferencedAssembly()
    {
        const string header = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            """;
        string directory = TestEnvironment.CreateDirectory("export");
        string referenced = Compile(directory, "Bases", header + """
            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7800")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7801")]
            public partial interface IBase { void M(); }
            """);
        string implementer = Compile(directory, "Implementer", header + """
            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7810")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7811")]
            public partial interface ISelf { void N(); }

            [GeneratedComClass, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7812")]
            public partial class Widget : ISelf, IBase { public void M() { } public void N() { } }
            """, referenced);
        string deriver = Compile(directory, "Deriver", header + """
            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7820")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7821")]
            public partial interface IDerived : IBase { void N(); }
            """, referenced);
        var warnings = new WarningCollector<GeneratedComAssemblyReader>();
        var reader = new GeneratedComAssemblyReader(warnings);

        // Without the type library of the referenced assembly.
        var library = reader.Read(implementer, catalog: new TypeLibraryCatalog());
        CollectionAssert.AreEquivalent(new[] { "ISelf", "Widget" }, library.Types.Select(static t => t.Name).ToArray());
        Assert.AreEqual("ISelf", library.Types.OfType<CoClassDefinition>().Single().Interfaces.Single().Interface.ToString());
        Assert.IsTrue(warnings.Messages.Any(static m => m.Contains("'IBase' of the assembly 'Bases'", StringComparison.Ordinal)), string.Join(Environment.NewLine, warnings.Messages));
        var exception = Assert.ThrowsExactly<CsComException>(() => reader.Read(deriver, catalog: new TypeLibraryCatalog()));
        StringAssert.Contains(exception.Message, "The interface 'IDerived' derives from 'IBase' of the assembly 'Bases', whose type library was not found");

        // With it, the library refers to the interface.
        string exported = Path.Combine(directory, "Bases.tlb");
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader().Read(referenced), exported);
        var catalog = new TypeLibraryCatalog([exported]);
        library = reader.Read(implementer, catalog: catalog);
        Assert.AreEqual("Bases", library.Types.Single(static t => t.Name == "IBase").ImportedFrom?.Name);
        Assert.HasCount(2, library.Types.OfType<CoClassDefinition>().Single().Interfaces);
        library = reader.Read(deriver, catalog: catalog);
        Assert.AreEqual("Bases", library.Types.Single(static t => t.Name == "IBase").ImportedFrom?.Name);
        string path = Path.Combine(directory, "Deriver.tlb");
        new TypeLibraryWriter().Write(library, path, new TypeLibraryWriterOptions { Catalog = catalog });
        var written = ModelDescriber.ReadWithImports(path, exported);
        Assert.AreEqual("IBase", written.Types.OfType<InterfaceDefinition>().Single(static t => t.Name == "IDerived").BaseInterface?.ToString());
    }

    // A type library that has a type with the name of a type of a referenced assembly is not its type library.
    [TestMethod]
    public void DoesNotReferToTheTypesOfAnotherLibraryWithTheSameNames()
    {
        const string source = """

            // An interface of the test type library, declared again as the interfaces of COM often are.
            [GeneratedComInterface, Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1011")]
            public partial interface IShape { void M(); }

            // The test type library has an enumeration with this name and other members.
            public enum Color { Cyan, Magenta }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7901")]
            public partial interface IClash { void M(Color color); }
            """;
        const string consumer = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7910")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7911")]
            public partial interface IConsumer { void Use(Color color, IClash clash); }
            """;

        // The assembly has an interface of the library, and then the identifier of the library as well.
        const string usings = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            """;
        foreach (string attribute in new[] { string.Empty, """[assembly: Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1000")]""" })
        {
            string directory = TestEnvironment.CreateDirectory("export");
            string referenced = Compile(directory, "Clash", usings + attribute + source);
            string assembly = Compile(directory, "Consumer", consumer, referenced);

            var library = new GeneratedComAssemblyReader().Read(assembly, catalog: new TypeLibraryCatalog([TestEnvironment.TestTypeLibrary]));

            Assert.AreEqual("VT_I4 color, VT_UNKNOWN clash", string.Join(", ", library.Types.OfType<InterfaceDefinition>().Single().Methods.Single().Parameters));
            Assert.IsEmpty(library.ImportedLibraries);
        }
    }

    // An assembly that is named like those of .NET is only looked up by its identifier, not by its interfaces.
    [TestMethod]
    public void DoesNotSearchTheAssembliesNamedLikeThoseOfDotNetByTheirInterfaces()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string referenced = Compile(directory, "Microsoft.NoGuid", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7A01")]
            public partial interface IShared { void M(); }
            """);
        string assembly = Compile(directory, "Consumer", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7A10")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7A11")]
            public partial interface IConsumer { void Use(IShared shared, System.DayOfWeek day); }
            """, referenced);
        string exported = Path.Combine(directory, "NoGuid.tlb");
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader().Read(referenced, new ExportedLibraryIdentity { Guid = new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7A00") }), exported);

        var library = new GeneratedComAssemblyReader().Read(assembly, catalog: new TypeLibraryCatalog([exported]));

        Assert.AreEqual("VT_UNKNOWN shared, VT_I4 day", string.Join(", ", library.Types.OfType<InterfaceDefinition>().Single().Methods.Single().Parameters));
    }

    // A type library refers to another one by its identifier, so two files with one identifier cannot both be given.
    [TestMethod]
    public void RejectsTwoFilesWithOneIdentifier()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7B01")]
            public partial interface IShared { void M(); }
            """;
        string directory = TestEnvironment.CreateDirectory("export");
        var identity = new ExportedLibraryIdentity { Guid = new Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7B00") };
        string referenced = Compile(directory, "Shared", source);
        string first = Path.Combine(directory, "Shared.tlb");
        string second = Path.Combine(directory, "Copy.tlb");
        new TypeLibraryWriter().Write(new GeneratedComAssemblyReader().Read(referenced, identity), first);
        File.Copy(first, second);
        string assembly = Compile(directory, "Consumer", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7B10")]

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7B11")]
            public partial interface IConsumer { void Use(IShared shared); }
            """, referenced);

        // The same file twice is one file.
        var library = new GeneratedComAssemblyReader().Read(assembly, catalog: new TypeLibraryCatalog([first, first]));
        Assert.AreEqual("IShared* shared", string.Join(", ", library.Types.OfType<InterfaceDefinition>().Single(static t => t.ImportedFrom is null).Methods.Single().Parameters));

        var exception = Assert.ThrowsExactly<TypeLibraryException>(() => new GeneratedComAssemblyReader().Read(assembly, catalog: new TypeLibraryCatalog([first, second])));
        StringAssert.Contains(exception.Message, "have the same identifier {7c1f2d2a-4c1e-4b71-9d0b-7b7c2e0a7b00}: specify only one of them");
    }

    // Two versions of a type library are two libraries.
    [TestMethod]
    public void KeepsTheVersionsOfATypeLibraryApart()
    {
        string directory = TestEnvironment.CreateDirectory("export");
        string first = Path.Combine(directory, "First.tlb");
        string second = Path.Combine(directory, "Second.tlb");
        File.Copy(TestEnvironment.TestTypeLibrary, first);
        File.Copy(TestEnvironment.TestTypeLibrary, second);
        var catalog = new TypeLibraryCatalog([], null, reference => reference.MajorVersion == 1 ? first : second);
        var libraryId = Guid.NewGuid();

        Assert.IsTrue(catalog.TryGetLibrary(new TypeLibraryReference("Library", libraryId, 1, 0), out var one));
        Assert.IsTrue(catalog.TryGetLibrary(new TypeLibraryReference("Library", libraryId, 2, 0), out var two));

        Assert.AreEqual((first, second), (one.Path, two.Path));

        // A file that was given is its library in every version.
        catalog = new TypeLibraryCatalog([TestEnvironment.TestTypeLibrary]);
        libraryId = new Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1000");
        Assert.IsTrue(catalog.TryGetLibrary(new TypeLibraryReference("CsComTest", libraryId, 1, 2), out one));
        Assert.IsTrue(catalog.TryGetLibrary(new TypeLibraryReference("CsComTest", libraryId, 9, 9), out two));
        Assert.AreSame(one, two);
    }

    // An assembly whose name is like those of .NET exports its own enumerations and structures.
    [TestMethod]
    public void ExportsTheTypesOfAnAssemblyNamedLikeThoseOfDotNet()
    {
        string assembly = Compile(TestEnvironment.CreateDirectory("export"), "Microsoft.Sample", """
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [assembly: Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7400")]

            public enum Level { Low, High }

            public struct Size { public int Width; public int Height; }

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A7401")]
            public partial interface ISample { void M(Level level, in Size size); }
            """);

        var library = new GeneratedComAssemblyReader().Read(assembly);

        CollectionAssert.AreEquivalent(new[] { "ISample", "Level", "Size" }, library.Types.Select(static t => t.Name).ToArray());
        Assert.AreEqual("Level level, Size* size", string.Join(", ", library.Types.OfType<InterfaceDefinition>().Single().Methods.Single().Parameters));
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

        namespace Interop.CsComTest
        {
            using System.Runtime.InteropServices;
            using System.Runtime.InteropServices.Marshalling;

            [GeneratedComInterface, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A3002")]
            public partial interface IOwnShape
            {
                void Draw(Color color);
            }

            [GeneratedComClass, Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A3011")]
            public partial class OwnShape : IOwnShape
            {
                public void Draw(Color color) { }
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

            public record struct Pair(int First, int Second);

            public struct Counter
            {
                public int Count { get; set; }
            }

            [StructLayout(LayoutKind.Explicit)]
            public struct Sparse
            {
                [FieldOffset(0)] public int First;
                [FieldOffset(8)] public int Second;
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
                    [MarshalUsing(typeof(CurrencyMarshaller))] decimal money = 2.5m,
                    string missing = null,
                    [MarshalUsing(typeof(ComObjectMarshaller<object>))] object parent = null);

                Item GetItem();

                void Structures(Pair pair, in Counter counter, Sparse sparse);
            }

            // An interface whose members all have Automation types, unlike those of ICalculator.
            [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2008")]
            public partial interface IAutomation
            {
                string Convert(
                    [MarshalUsing(typeof(VariantMarshaller))] ref object value,
                    Level level,
                    in Size size,
                    [MarshalUsing(typeof(SafeArrayMarshaller<int>))] out int[] numbers,
                    [MarshalUsing(typeof(ComObjectMarshaller<IAutomation>))] ref IAutomation other);
            }

            // An interface that derives from one that is not compatible is not compatible either.
            [GeneratedComInterface]
            [Guid("7C1F2D2A-4C1E-4B71-9D0B-7B7C2E0A2009")]
            public partial interface IDerivedCalculator : ICalculator
            {
                void Reset();
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

                void Label([MarshalAs(UnmanagedType.LPWStr)] string text);

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
