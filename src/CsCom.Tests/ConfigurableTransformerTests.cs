using System.Xml.Linq;
using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using CsCom.Transformations;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Tests;

/// <summary>
/// Tests of the transformers that can be added by configuration files.
/// </summary>
[TestClass]
public sealed class ConfigurableTransformerTests
{
    [TestMethod]
    public void RenameTypeUsesSubstitutions()
    {
        var (library, logger) = Apply("""<RenameType Type="^I(Shape|Circle)$" NewName="Contract$1" />""");

        var names = library.Types.Select(static t => t.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "ContractShape", "ContractCircle", "ICalculator" }, names);
        CollectionAssert.DoesNotContain(names, "IShape");
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void RenameTypeReplacesTheMatches()
    {
        var (library, _) = Apply("""<RenameType Type="Shape" NewName="Figure" />""");

        var names = library.Types.Select(static t => t.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "IFigure", "IFigureEventsVt", "DFigureEvents", "_FigureEvents", "Figure", "EventFigure" }, names);
        CollectionAssert.DoesNotContain(names, "Shape");
    }

    [TestMethod]
    public void RenameMemberRenamesAllKindsOfMembers()
    {
        var (library, logger) = Apply("""
            <RenameMember Type="^IShape$" Member="^Name$" NewName="Title" />
            <RenameMember Type="^DShapeEvents$" Member="^(Changed|Counter)$" NewName="On$1" />
            <RenameMember Type="^Record$" Member="^checked$" NewName="IsChecked" />
            <RenameMember Type="^Color$" Member="^Transparent$" NewName="None" />
            <RenameMember Type="^Kernel$" Member="^(Pi|Beep)$" NewName="My$1" />
            """);

        // The accessors of a property are renamed together.
        Assert.AreEqual(2, Get<InterfaceDefinition>(library, "IShape").Methods.Count(static m => m.Name == "Title"));
        var events = Get<InterfaceDefinition>(library, "DShapeEvents");
        Assert.IsTrue(events.Methods.Any(static m => m.Name == "OnChanged"));
        Assert.IsTrue(events.Properties.Any(static p => p.Name == "OnCounter"));
        Assert.IsTrue(Get<RecordDefinition>(library, "Record").Fields.Any(static f => f.Name == "IsChecked"));
        CollectionAssert.AreEqual(new[] { "Red", "Green", "Blue", "None" }, Get<EnumDefinition>(library, "Color").Members.Select(static m => m.Name).ToArray());
        var kernel = Get<ModuleDefinition>(library, "Kernel");
        Assert.IsTrue(kernel.Constants.Any(static c => c.Name == "MyPi"));
        Assert.IsTrue(kernel.Functions.Any(static f => f.Name == "MyBeep"));
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void RenameMemberRenamesParameters()
    {
        var (library, _) = Apply("""<RenameMember Type="^IShape$" Member="^Move$" Parameter="^d(.)$" NewName="delta$1" />""");

        var move = Get<InterfaceDefinition>(library, "IShape").Methods.Single(static m => m.Name == "Move");
        CollectionAssert.AreEqual(
            new[] { "deltax", "deltay", "scale", "label", "Color", "factor", "Flag" },
            move.Parameters.Select(static p => p.Name).ToArray());
    }

    [TestMethod]
    [DataRow("""<RenameType Type="^IShap$" NewName="X" />""", "RenameType: no type matches Type='^IShap$'")]
    [DataRow("""<RenameMember Type="^IShape$" Member="^Nothing$" NewName="X" />""", "RenameMember: no member matches Type='^IShape$', Member='^Nothing$'")]
    [DataRow(
        """<RenameMember Member="^Move$" Parameter="^z$" NewName="X" />""",
        "RenameMember: no parameter matches Member='^Move$', Parameter='^z$'")]
    [DataRow("""<SetMarshalling Type="^Nothing$" Dates="wrapper" />""", "SetMarshalling: nothing matches Type='^Nothing$'")]
    [DataRow("""<RemoveEnumPrefix Type="^IShape$" />""", "RemoveEnumPrefix: no enum matches Type='^IShape$'")]
    [DataRow("""<RenameType TypeName="IShap" NewName="X" />""", "RenameType: no type matches TypeName='IShap'")]
    [DataRow("""<ExcludeType Type="^IShape$" Kind="enum|struct" />""", "ExcludeType: no type matches Type='^IShape$', Kind='enum|struct'")]
    [DataRow("""<RenameMember Kind="coclass" MemberName="Move" NewName="X" />""", "RenameMember: no member matches MemberName='Move', Kind='coclass'")]
    public void WarnsWhenNothingMatches(string transformer, string warning)
    {
        var (_, logger) = Apply(transformer);

        CollectionAssert.AreEqual(new[] { warning }, logger.Warnings.ToArray());
    }

    [TestMethod]
    public void SetMarshallingChangesMethods()
    {
        var (library, _) = Apply("""<SetMarshalling Type="^IArrays$" Member="^Matrix$" SafeArrays="wrapper" />""");

        var arrays = Get<InterfaceDefinition>(library, "IArrays");
        var matrix = arrays.Methods.Single(static m => m.Name == "Matrix");
        var expected = new MarshallingOverrides { SafeArrays = SafeArrayMarshalling.Wrapper };
        Assert.IsTrue(matrix.Parameters.All(p => p.Marshalling == expected));
        Assert.AreEqual(expected, matrix.ReturnMarshalling);
        Assert.IsTrue(arrays.Methods.Where(static m => m.Name != "Matrix").All(static m => m.ReturnMarshalling is null && m.Parameters.All(static p => p.Marshalling is null)));
    }

    [TestMethod]
    public void SetMarshallingChangesParameters()
    {
        var (library, _) = Apply("""<SetMarshalling Type="^IStrings$" Member="^Echo$" Parameter="^Value$" Bstrs="none" />""");

        var echo = Get<InterfaceDefinition>(library, "IStrings").Methods.Single(static m => m.Name == "Echo");
        Assert.AreEqual(new MarshallingOverrides { Bstrs = BstrMarshalling.None }, echo.Parameters[0].Marshalling);
        Assert.IsNull(echo.RetvalParameter!.Marshalling);
        Assert.IsNull(echo.ReturnMarshalling);
    }

    [TestMethod]
    public void SetMarshallingCombinesTheChanges()
    {
        var (library, _) = Apply("""
            <SetMarshalling Type="^Record$" Member="^Date$" Dates="wrapper" Booleans="none" />
            <SetMarshalling Type="^Record$" Member="^Date$" Dates="None" />
            <SetMarshalling Type="^DShapeEvents$" Member="^Counter$" SafeArrays="sysarray" />
            """);

        var fields = Get<RecordDefinition>(library, "Record").Fields;
        Assert.AreEqual(new MarshallingOverrides { Dates = ValueMarshalling.None, Booleans = ValueMarshalling.None }, fields.Single(static f => f.Name == "Date").Marshalling);
        Assert.IsTrue(fields.Where(static f => f.Name != "Date").All(static f => f.Marshalling is null));
        var counter = Get<InterfaceDefinition>(library, "DShapeEvents").Properties.Single(static p => p.Name == "Counter");
        Assert.AreEqual(new MarshallingOverrides { SafeArrays = SafeArrayMarshalling.SysArray }, counter.Marshalling);
    }

    [TestMethod]
    [DataRow(
        """<SetMarshalling Type="^IArrays$" />""",
        "cscom.xml(1,28): At least one of the attributes Dates, Currencies, Booleans, Bstrs or SafeArrays is required by <SetMarshalling>.")]
    [DataRow(
        """<SetMarshalling SafeArrays="vector" />""",
        "cscom.xml(1,43): 'vector' is not a valid value of the attribute 'SafeArrays': one of Array, SysArray, Wrapper, None is expected.")]
    [DataRow("""<RenameType NewName="X" />""", "cscom.xml(1,28): The attribute 'Type' or 'TypeName' is required by <RenameType>.")]
    [DataRow("""<RenameMember Type="^IShape$" NewName="X" />""", "cscom.xml(1,28): The attribute 'Member' or 'MemberName' is required by <RenameMember>.")]
    [DataRow("""<RemoveEnumPrefix Prefix="xl" />""", "cscom.xml(1,45): The attribute 'Prefix' is not supported by <RemoveEnumPrefix>.")]
    [DataRow("""<RemoveEnumPrefix Kind="enum" />""", "cscom.xml(1,45): The attribute 'Kind' is not supported by <RemoveEnumPrefix>.")]
    [DataRow(
        """<RenameType Type="A" TypeName="B" NewName="X" />""",
        "cscom.xml(1,28): The attributes 'Type' and 'TypeName' cannot be combined in <RenameType>.")]
    [DataRow(
        """<RenameType Type="A" Kind="enum|class" NewName="X" />""",
        "cscom.xml(1,48): 'class' is not a valid value of the attribute 'Kind': a list of enum, struct, union, interface, dispinterface, coclass, module, all is expected.")]
    [DataRow("""<ExcludeType />""", "cscom.xml(1,28): The attribute 'Type', 'TypeName' or 'Kind' is required by <ExcludeType>.")]
    public void ReportsConfigurationErrors(string transformer, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => Parse(transformer));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void ExactNamesMatchWholeNames()
    {
        var (library, logger) = Apply("""
            <RenameType TypeName="IShape" NewName="Figure$1" />
            <RenameMember TypeName="Color" MemberName="Red" NewName="Rot" />
            <RemoveEnumPrefix TypeName="AnonymousEnum" />
            """);

        var names = library.Types.Select(static t => t.Name).ToList();
        CollectionAssert.Contains(names, "Figure$1", "The new name of an exact name is literal.");
        CollectionAssert.IsSubsetOf(new[] { "IShapeEventsVt", "IShapeEventsDual", "Shape" }, names);
        Assert.AreEqual("Rot", Get<EnumDefinition>(library, "Color").Members[0].Name);
        CollectionAssert.AreEqual(new[] { "First", "Second" }, Get<EnumDefinition>(library, "AnonymousEnum").Members.Select(static m => m.Name).ToArray());
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void KindsFilterTheTypes()
    {
        var (library, _) = Apply("""
            <RenameType Type="Shape" Kind="interface | DispInterface" NewName="Figure" />
            <RenameMember Kind="enum" Member="^Red$" NewName="Rot" />
            """);

        var names = library.Types.Select(static t => t.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "IFigure", "DFigureEvents", "_FigureEvents", "IFigureEventsVt", "Shape", "EventShape" }, names);
        Assert.AreEqual("Rot", Get<EnumDefinition>(library, "Color").Members[0].Name);
    }

    [TestMethod]
    public void ExcludeTypeReplacesTheReferences()
    {
        var (library, logger) = Apply("""
            <ExcludeType TypeName="IShapeEventsVt" />
            <ExcludeType TypeName="DShapeEvents" />
            <ExcludeType TypeName="Color" />
            <ExcludeType TypeName="Record" />
            <ExcludeType Kind="module" />
            """);

        var names = library.Types.Select(static t => t.Name).ToList();
        CollectionAssert.IsNotSubsetOf(new[] { "IShapeEventsVt" }, names);
        Assert.IsFalse(names.Intersect(["IShapeEventsVt", "DShapeEvents", "Color", "Record", "Kernel"]).Any());

        var unknown = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_UNKNOWN);
        var dispatch = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH);
        var @long = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4);
        Assert.IsTrue(Get<CoClassDefinition>(library, "EventShape").Interfaces.Any(i => i.IsSource && i.Interface == unknown));
        Assert.IsTrue(Get<CoClassDefinition>(library, "Shape").Interfaces.Any(i => i.IsSource && i.Interface == dispatch));

        var shape = Get<InterfaceDefinition>(library, "IShape");
        Assert.AreEqual(new PointerTypeReference(dispatch), shape.Methods.Single(static m => m.Name == "GetEvents").Parameters[0].Type);
        Assert.AreEqual(new PointerTypeReference(@long), shape.Methods.Single(static m => m.Name == "Color" && m.InvokeKind == InvokeKind.PropertyGet).Parameters[0].Type);
        Assert.AreEqual(@long, shape.Methods.Single(static m => m.Name == "Move").Parameters[4].Type);

        var numbers = Get<InterfaceDefinition>(library, "ICalculator").Methods.Single(static m => m.Name == "Numbers");
        Assert.AreEqual(new PointerTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID)), numbers.Parameters[1].Type);
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    [DataRow("""<ExcludeType TypeName="Point" />""", "The structure 'Point' cannot be excluded, because 'Record.Corners' uses it.")]
    [DataRow("""<ExcludeType TypeName="Number" />""", "The union 'Number' cannot be excluded, because 'ICalculator.Numbers' uses it.")]
    [DataRow("""<ExcludeType TypeName="IShape" />""", "The interface 'IShape' cannot be excluded, because 'ICircle' derives from it.")]
    public void ExcludeTypeKeepsTheLayouts(string transformer, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => Apply(transformer));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void AddAttributeAddsTheAttributeToTheSelectedDeclarations()
    {
        var (documents, logger) = Generate("""
            <AddAttribute TypeName="IShape" Attribute='System.Obsolete("x")' />
            <AddAttribute TypeName="IShape" Role="Extensions" Attribute='System.Obsolete( "x" )' />
            <AddAttribute TypeName="IShape" MemberName="Move" Attribute='System.Obsolete("x")' />
            <AddAttribute TypeName="IShape" Attribute='System.Obsolete("x")' />
            <AddAttribute Kind="enum" Attribute="System.CLSCompliant(false)" />
            """);

        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        Assert.AreEqual(3, shape.Split("[System.Obsolete(").Length - 1);
        StringAssert.Contains(shape.ReplaceLineEndings("\n"), "/// </summary>\n[System.Obsolete(\"x\")]\n[global::System.Runtime.InteropServices.Marshalling.GeneratedComInterface(");
        StringAssert.Contains(shape.ReplaceLineEndings("\n"), "/// </summary>\n[System.Obsolete(\"x\")]\npublic static partial class IShapeExtensions");
        StringAssert.Contains(shape.ReplaceLineEndings("\n"), "    [System.Obsolete(\"x\")]\n    void Move(");
        StringAssert.Contains(documents.Single(static d => d.RelativePath == "Color.cs").GetText(), "[System.CLSCompliant(false)]");
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void AddAttributeWarnsWhenNothingMatches()
    {
        var (_, logger) = Generate("""<AddAttribute TypeName="IShape" Role="EventSink" Attribute="System.Obsolete" />""");

        CollectionAssert.AreEqual(
            new[] { "AddAttribute: no declaration matches TypeName='IShape' with the role EventSink" },
            logger.Warnings.ToArray());
    }

    [TestMethod]
    [DataRow("""<AddAttribute TypeName="IShape" Attribute="System.Obsolete(" />""", "cscom.xml(1,29): 'System.Obsolete(' is not a valid attribute:")]
    [DataRow("""<AddAttribute TypeName="IShape" Attribute="return: X" />""", "cscom.xml(1,29): 'return: X' is not a valid attribute: a single attribute list without a target is expected")]
    [DataRow("""<AddAttribute TypeName="IShape" Role="Nope" Attribute="X" />""", "cscom.xml(1,60): 'Nope' is not a valid value of the attribute 'Role':")]
    [DataRow("""<AddAttribute Attribute="X" />""", "cscom.xml(1,29): The attribute 'Type', 'TypeName' or 'Kind' is required by <AddAttribute>.")]
    public void AddAttributeReportsConfigurationErrors(string transformer, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => PipelineConfiguration.Parse(
            XDocument.Parse($"<CsCom><SyntaxTransformers>{transformer}</SyntaxTransformers></CsCom>", LoadOptions.SetLineInfo),
            TransformerRegistry.CreateDefault(),
            "cscom.xml"));

        StringAssert.StartsWith(exception.Message, message);
    }

    [TestMethod]
    public void ReportsInvalidRegularExpressions()
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => Parse("""<SetMarshalling Type="(" Dates="wrapper" />"""));

        StringAssert.StartsWith(exception.Message, "cscom.xml(1,43): '(' is not a valid regular expression in the attribute 'Type':");
    }

    [TestMethod]
    [DataRow("xlArea,xlBar", "Area,Bar")]
    [DataRow("SHAPE_KIND_CIRCLE,SHAPE_KIND_SQUARE", "CIRCLE,SQUARE")]
    [DataRow("ShapeKindCircle,ShapeKindSquare", "Circle,Square")]
    [DataRow("vbRed,vbRose", "Red,Rose")]
    [DataRow("Red,Rose", "Red,Rose")]
    [DataRow("IOError,IOWarning", "IOError,IOWarning")]
    [DataRow("Size16,Size32", "Size16,Size32")]
    [DataRow("SHAPE_1,SHAPE_2", "SHAPE_1,SHAPE_2")]
    [DataRow("sk,skCircle", "sk,skCircle")]
    [DataRow("xlArea", "xlArea")]
    public void RemovesEnumPrefixesAtWordBoundaries(string names, string expected)
    {
        var definition = new EnumDefinition { Name = "Test", OriginalName = "Test" };
        definition.Members.AddRange(names.Split(',').Select(static (n, i) => new EnumMember { Name = n, Value = i }));
        var library = new TypeLibrary { Name = "Test" };
        library.Types.Add(definition);

        new EnumPrefixRemover(null).Transform(library, new TransformationContext { Options = new CodeGenerationOptions { Namespace = "Test" }, Logger = NullLogger.Instance });

        Assert.AreEqual(expected, string.Join(",", definition.Members.Select(static m => m.Name)));
    }

    [TestMethod]
    public void RemovesEnumPrefixesOfTheTypeLibrary()
    {
        var (library, logger) = Apply("<RemoveEnumPrefix />");

        CollectionAssert.AreEqual(new[] { "First", "Second" }, Get<EnumDefinition>(library, "AnonymousEnum").Members.Select(static m => m.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "Red", "Green", "Blue", "Transparent" }, Get<EnumDefinition>(library, "Color").Members.Select(static m => m.Name).ToArray());
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void AddFlagsAttributeKeepsTheLayoutOfSingleFiles()
    {
        var (documents, logger) = Generate("""<AddFlagsAttribute Type="^Color$" />""", singleFile: true);

        string text = documents.Single().GetText().ReplaceLineEndings("\n");
        StringAssert.Contains(text, "    /// </summary>\n    [global::System.Flags]\n    public enum Color\n");
        Assert.AreEqual(1, CountFlags(text));
        Assert.IsEmpty(logger.Warnings);
    }

    [TestMethod]
    public void AddFlagsAttributeIsAddedOnce()
    {
        var (documents, _) = Generate("""
            <AddFlagsAttribute Type="^(Color|AnonymousEnum)$" />
            <AddFlagsAttribute Type="^Color$" />
            """);

        Assert.AreEqual(1, CountFlags(documents.Single(static d => d.RelativePath == "Color.cs").GetText()));
        StringAssert.Contains(documents.Single(static d => d.RelativePath == "AnonymousEnum.cs").GetText(), "\r\n[global::System.Flags]\r\npublic enum AnonymousEnum\r\n");
        Assert.AreEqual(2, documents.Sum(static d => CountFlags(d.GetText())));
    }

    [TestMethod]
    public void AddFlagsAttributeWarnsWhenNothingMatches()
    {
        var (documents, logger) = Generate("""<AddFlagsAttribute Type="^IShape$" />""");

        CollectionAssert.AreEqual(new[] { "AddFlagsAttribute: no enum matches Type='^IShape$'" }, logger.Warnings.ToArray());
        Assert.AreEqual(0, documents.Sum(static d => CountFlags(d.GetText())));
    }

    [TestMethod]
    public void AddFlagsAttributeRequiresAType()
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => PipelineConfiguration.Parse(
            XDocument.Parse("<CsCom><SyntaxTransformers><AddFlagsAttribute /></SyntaxTransformers></CsCom>", LoadOptions.SetLineInfo),
            TransformerRegistry.CreateDefault(),
            "cscom.xml"));

        Assert.AreEqual("cscom.xml(1,29): The attribute 'Type' or 'TypeName' is required by <AddFlagsAttribute>.", exception.Message);
    }

    private static int CountFlags(string text) => text.Split("[global::System.Flags]").Length - 1;

    // Generates the code of the test type library with syntax transformers.
    private static (IReadOnlyList<GeneratedDocument> Documents, ListLogger Logger) Generate(string syntaxTransformers, bool singleFile = false)
    {
        var logger = new ListLogger();
        var pipeline = new InteropPipeline(new ListLoggerFactory(logger));
        var configuration = PipelineConfiguration.Parse(
            XDocument.Parse($"<CsCom><SyntaxTransformers>{syntaxTransformers}</SyntaxTransformers></CsCom>", LoadOptions.SetLineInfo),
            pipeline.Registry);
        var documents = pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            new CodeGenerationOptions { Namespace = "Test", SingleFile = singleFile, SingleFileName = "All.cs" },
            configuration);
        return (documents, logger);
    }

    // Applies the transformers to the test type library, after the aliases are resolved as the pipeline does.
    private static (TypeLibrary Library, ListLogger Logger) Apply(string transformers)
    {
        var configuration = Parse(transformers);
        var logger = new ListLogger();
        var context = new TransformationContext { Options = new CodeGenerationOptions { Namespace = "Test" }, Logger = logger };
        var library = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);
        new AliasResolver().Transform(library, context);
        foreach (var transformer in configuration.ModelTransformers)
        {
            transformer.Transform(library, context);
        }

        return (library, logger);
    }

    private static PipelineConfiguration Parse(string transformers) => PipelineConfiguration.Parse(
        XDocument.Parse($"<CsCom><ModelTransformers>{transformers}</ModelTransformers></CsCom>", LoadOptions.SetLineInfo),
        TransformerRegistry.CreateDefault(),
        "cscom.xml");

    private static T Get<T>(TypeLibrary library, string name)
        where T : TypeDefinition => library.Types.OfType<T>().Single(t => t.Name == name);

    private sealed class ListLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IEnumerable<string> Warnings => _entries.Where(static e => e.Level == LogLevel.Warning).Select(static e => e.Message);

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ListLoggerFactory(ListLogger logger) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => logger;

        public void Dispose()
        {
        }
    }
}
