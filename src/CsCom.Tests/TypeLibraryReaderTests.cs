using System.Runtime.InteropServices;
using CsCom.Model;
using CsCom.TypeLibraries;

namespace CsCom.Tests;

[TestClass]
public sealed class TypeLibraryReaderTests
{
    private static TypeLibrary s_library = null!;

    [ClassInitialize]
    public static void ReadTestLibrary(TestContext context) => s_library = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);

    [TestMethod]
    public void ReadsLibraryAttributes()
    {
        Assert.AreEqual("CsComTest", s_library.Name);
        Assert.AreEqual(new Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1000"), s_library.Guid);
        Assert.AreEqual((ushort)1, s_library.MajorVersion);
        Assert.AreEqual((ushort)2, s_library.MinorVersion);
        Assert.AreEqual("CsCom test type library", s_library.Documentation);
    }

    [TestMethod]
    public void ReadsEnums()
    {
        var color = Get<EnumDefinition>("Color");

        Assert.AreEqual("The colors of a shape", color.Documentation);
        CollectionAssert.AreEqual(new[] { "Red", "Green", "Blue", "Transparent" }, color.Members.Select(static m => m.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 0L, 1L, 2L, (long)int.MinValue }, color.Members.Select(static m => m.Value).ToArray());
        Assert.AreEqual("The red color", color.Members[0].Documentation);
    }

    [TestMethod]
    public void ReadsAnonymousEnumsAndAliases()
    {
        var alias = Get<AliasDefinition>("AnonymousEnum");
        var target = (UserDefinedTypeReference)alias.Target;

        Assert.IsInstanceOfType<EnumDefinition>(target.Definition);
        Assert.IsTrue(target.Definition.Name.StartsWith("__MIDL", StringComparison.Ordinal));
        Assert.AreEqual(new PrimitiveTypeReference(VarEnum.VT_I4), Get<AliasDefinition>("Handle").Target);
        Assert.AreEqual("Handle", ((UserDefinedTypeReference)Get<AliasDefinition>("HandleAlias").Target).Definition.Name);
    }

    [TestMethod]
    public void ReadsRecords()
    {
        var record = Get<RecordDefinition>("Record");
        var fields = record.Fields.ToDictionary(static f => f.Name, static f => f.Type);

        Assert.IsFalse(record.IsUnion);
        Assert.AreEqual(8, record.Alignment);
        Assert.AreEqual(new PrimitiveTypeReference(VarEnum.VT_BSTR), fields["Name"]);
        Assert.AreEqual(new PrimitiveTypeReference(VarEnum.VT_VARIANT), fields["Value"]);
        Assert.AreEqual(new PrimitiveTypeReference(VarEnum.VT_UNKNOWN), fields["Unknown"]);
        Assert.AreEqual(new FixedArrayTypeReference(new PrimitiveTypeReference(VarEnum.VT_R8), [4]), fields["Values"]);
        Assert.AreEqual(new WellKnownTypeReference(WellKnownType.Guid), fields["Id"]);
        Assert.IsInstanceOfType<PointerTypeReference>(fields["Next"]);
        Assert.IsTrue(Get<RecordDefinition>("Number").IsUnion);
    }

    [TestMethod]
    public void ReadsVtableInterfaces()
    {
        var calculator = Get<InterfaceDefinition>("ICalculator");

        Assert.AreEqual(InterfaceKind.Vtable, calculator.InterfaceKind);
        Assert.IsNull(calculator.BaseInterface);
        Assert.AreEqual(3, calculator.BaseVtableSlots);
        CollectionAssert.AreEqual(Enumerable.Range(3, calculator.Methods.Count).ToArray(), calculator.Methods.Select(static m => m.VtableSlot).ToArray());

        var add = calculator.Methods.Single(static m => m.Name == "Add");
        Assert.IsTrue(add.ReturnsHResult);
        Assert.AreEqual("Adds two numbers", add.Documentation);
        Assert.AreEqual(ParameterFlags.Out | ParameterFlags.Retval, add.RetvalParameter!.Flags);

        var noHResult = calculator.Methods.Single(static m => m.Name == "NoHResult");
        Assert.AreEqual(new PrimitiveTypeReference(VarEnum.VT_I4), noHResult.ReturnType);
    }

    [TestMethod]
    public void ReadsDualInterfaces()
    {
        var shape = Get<InterfaceDefinition>("IShape");
        var circle = Get<InterfaceDefinition>("ICircle");

        Assert.AreEqual(InterfaceKind.Dual, shape.InterfaceKind);
        Assert.AreEqual(new PrimitiveTypeReference(VarEnum.VT_DISPATCH), shape.BaseInterface);
        Assert.AreEqual(7, shape.BaseVtableSlots);
        Assert.AreEqual(shape, ((UserDefinedTypeReference)circle.BaseInterface!).Definition);
        Assert.AreEqual(7 + shape.Methods.Count, circle.BaseVtableSlots);

        var name = shape.Methods.Where(static m => m.Name == "Name").Select(static m => m.InvokeKind).ToArray();
        CollectionAssert.AreEqual(new[] { InvokeKind.PropertyGet, InvokeKind.PropertyPut }, name);
    }

    [TestMethod]
    public void ReadsDefaultValues()
    {
        var move = Get<InterfaceDefinition>("IShape").Methods.Single(static m => m.Name == "Move");
        var parameters = move.Parameters.ToDictionary(static p => p.Name);

        Assert.AreEqual(ParameterFlags.In, parameters["dx"].Flags);
        Assert.AreEqual(0, parameters["dy"].DefaultValue);
        Assert.IsTrue(parameters["scale"].Flags.HasFlag(ParameterFlags.Optional));
        Assert.AreEqual("label", parameters["label"].DefaultValue);
        Assert.AreEqual(2, parameters["Color"].DefaultValue);
        Assert.AreEqual(true, parameters["Flag"].DefaultValue);
    }

    [TestMethod]
    public void ReadsDispInterfaces()
    {
        var events = Get<InterfaceDefinition>("DShapeEvents");

        Assert.AreEqual(InterfaceKind.Dispatch, events.InterfaceKind);
        CollectionAssert.AreEqual(new[] { "Counter", "Description", "Color" }, events.Properties.Select(static p => p.Name).ToArray());
        Assert.IsTrue(events.Properties.Single(static p => p.Name == "Description").IsReadOnly);
        Assert.IsFalse(events.Methods.Any(static m => m.Name is "QueryInterface" or "Invoke"));
        Assert.AreEqual(11, events.Methods.Single(static m => m.Name == "Compute").MemberId);
    }

    [TestMethod]
    public void ReadsCoClasses()
    {
        var shape = Get<CoClassDefinition>("Shape");

        Assert.IsTrue(shape.Flags.HasFlag(TypeFlags.CanCreate));
        Assert.AreEqual("IShape", ((UserDefinedTypeReference)shape.DefaultInterface!.Interface).Definition.Name);
        Assert.IsTrue(shape.Interfaces.Any(static i => i.IsSource && ((UserDefinedTypeReference)i.Interface).Definition.Name == "DShapeEvents"));
        Assert.IsFalse(Get<CoClassDefinition>("Calculator").Flags.HasFlag(TypeFlags.CanCreate));
    }

    [TestMethod]
    public void ReadsModules()
    {
        var kernel = Get<ModuleDefinition>("Kernel");
        var tickCount = kernel.Functions.Single(static f => f.Name == "GetTickCount");

        Assert.AreEqual("kernel32.dll", tickCount.DllName);
        Assert.AreEqual("GetTickCount", tickCount.EntryPoint);
        Assert.AreEqual(new PrimitiveTypeReference(VarEnum.VT_UI4), tickCount.ReturnType);
        Assert.AreEqual(100, kernel.Constants.Single(static c => c.Name == "MaxValue").Value);
        Assert.AreEqual("Hello", kernel.Constants.Single(static c => c.Name == "Greeting").Value);
        Assert.AreEqual((short)-5, kernel.Constants.Single(static c => c.Name == "Small").Value);
    }

    [TestMethod]
    public void ImportsTypesFromOtherLibraries()
    {
        var font = s_library.Types.Single(static t => t.ImportedFrom is not null && t is InterfaceDefinition);

        Assert.AreEqual("stdole", font.ImportedFrom!.Name);
        Assert.AreEqual("Font", font.Name);
        Assert.AreEqual("stdole", s_library.ImportedLibraries.Single().Name);
    }

    [TestMethod]
    public void MapsWellKnownTypes()
    {
        Assert.IsFalse(s_library.Types.Any(static t => t.Name is "IUnknown" or "IDispatch" or "IEnumVARIANT" or "GUID"));
        var enumerator = Get<InterfaceDefinition>("ICalculator").Methods.Single(static m => m.Name == "enumerator");
        Assert.AreEqual(new PointerTypeReference(new PointerTypeReference(new WellKnownTypeReference(WellKnownType.IEnumVariant))), enumerator.Parameters[0].Type);
    }

    [TestMethod]
    public void ReadsStdOle()
    {
        var library = new TypeLibraryReader().Read(TestEnvironment.StdOle);

        Assert.AreEqual("stdole", library.Name);
        Assert.IsFalse(library.Types.Any(static t => t.Name is "IUnknown" or "IDispatch" or "IEnumVARIANT" or "GUID"));
        Assert.IsTrue(library.Types.OfType<InterfaceDefinition>().Any(static t => t.Name == "IFont" && t.InterfaceKind == InterfaceKind.Vtable));
    }

    [TestMethod]
    public void ReadsTypeLibrariesEmbeddedIntoDlls()
    {
        var library = new TypeLibraryReader().Read(TestEnvironment.ScriptingRuntime);

        Assert.AreEqual("Scripting", library.Name);
        var dictionary = library.Types.OfType<InterfaceDefinition>().Single(static t => t.Name == "IDictionary");
        Assert.AreEqual(InterfaceKind.Dual, dictionary.InterfaceKind);
    }

    [TestMethod]
    public void ThrowsForMissingFiles()
    {
        Assert.ThrowsExactly<TypeLibraryException>(() => new TypeLibraryReader().Read(Path.Combine(TestEnvironment.CreateDirectory("missing"), "missing.tlb")));
    }

    private static T Get<T>(string name) where T : TypeDefinition =>
        s_library.Types.OfType<T>().Single(t => t.Name == name);
}
