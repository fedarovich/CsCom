using System.Xml.Linq;
using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;

namespace CsCom.Tests;

/// <summary>
/// Snapshot tests of the code generated for TestAssets/CsComTest.idl.
/// </summary>
[TestClass]
public sealed class CodeGenerationTests : VerifyBase
{
    [TestMethod]
    public Task GeneratesFilePerType()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion });

        return Verify(ToTargets(documents));
    }

    [TestMethod]
    public Task GeneratesExtensionIndexersForCSharp15()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", LanguageVersion = 15, ToolVersion = TestEnvironment.ToolVersion });

        return Verify(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "cs");
    }

    [TestMethod]
    public Task GeneratesInternalTypesWithInlineSupportCode()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SupportCode = SupportCodeMode.Internal,
            ToolVersion = TestEnvironment.ToolVersion,
        });

        // The support code is a copy of CsCom.Runtime; only its location and accessibility are checked.
        var support = documents.Where(static d => d.RelativePath.StartsWith("ComSupport/", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(support.Any(static d => d.RelativePath == "ComSupport/IDispatch.cs"));
        foreach (var document in support)
        {
            string text = document.GetText();
            StringAssert.Contains(text, "namespace Interop.CsComTest.ComSupport;");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, @"^public ", System.Text.RegularExpressions.RegexOptions.Multiline), document.RelativePath);
        }

        return Verify(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "cs");
    }

    [TestMethod]
    public Task GeneratesSingleFile()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SingleFile = true,
            SingleFileName = "Interop.CsComTest.cs",
            ToolVersion = TestEnvironment.ToolVersion,
        });

        Assert.AreEqual("Interop.CsComTest.cs", documents.Single().RelativePath);
        return Verify(documents.Single().GetText(), "cs");
    }

    [TestMethod]
    public void GeneratesTheSupportCodeIntoTheSingleFile()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SingleFile = true,
            SupportCode = SupportCodeMode.Public,
            ToolVersion = TestEnvironment.ToolVersion,
        });

        string text = documents.Single().GetText();
        StringAssert.Contains(text, "namespace Interop.CsComTest.ComSupport");
        StringAssert.Contains(text, "public unsafe partial interface IDispatch");
        StringAssert.Contains(text, ": global::Interop.CsComTest.ComSupport.IDispatch");
    }

    [TestMethod]
    [DataRow(ValueMarshalling.None)]
    [DataRow(ValueMarshalling.Wrapper)]
    public Task GeneratesValueMarshalling(ValueMarshalling mode)
    {
        // The default Bcl marshalling is covered by GeneratesFilePerType.
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions { Dates = mode, Currencies = mode, Booleans = mode },
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IMarshalling.cs" or "IShape.cs" or "Record.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    [DataRow(BstrMarshalling.Wrapper)]
    [DataRow(BstrMarshalling.None)]
    public Task GeneratesBstrMarshalling(BstrMarshalling mode)
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions { Bstrs = mode },
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IStrings.cs" or "ICalculator.cs" or "IShape.cs" or "IShapeEventsDual.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    public void BstrFieldsArePointersWithoutMarshalling()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            Marshalling = new MarshallingOptions { Bstrs = BstrMarshalling.None },
        });

        string record = documents.Single(static d => d.RelativePath == "Record.cs").GetText();
        StringAssert.Contains(record, "public nint Name;");
        Assert.IsFalse(record.Contains("ComBstrValue", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(StructProperties.Always)]
    [DataRow(StructProperties.Never)]
    public Task GeneratesStructProperties(StructProperties mode)
    {
        // The default NonBlittable mode is covered by GeneratesFilePerType.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion, StructProperties = mode });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "Record.cs" or "Number.cs" or "Point.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    [DataRow(SafeArrayMarshalling.SysArray)]
    [DataRow(SafeArrayMarshalling.Wrapper)]
    [DataRow(SafeArrayMarshalling.None)]
    public Task GeneratesSafeArrayMarshalling(SafeArrayMarshalling mode)
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions { SafeArrays = mode },
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IArrays.cs" or "IMarshalling.cs" or "DShapeEvents.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    [DataRow(EventStyle.Observable)]
    [DataRow(EventStyle.Interface)]
    public Task GeneratesEventCode(EventStyle style)
    {
        // The events style is covered by GeneratesFilePerType.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion, EventStyle = style });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "_ShapeEvents.cs" or "IShapeEventsVt.cs" or "IShapeEventsDual.cs")))
            .UseParameters(style);
    }

    [TestMethod]
    public void GeneratesNoEventCodeForTheNoneStyle()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion, EventStyle = EventStyle.None });

        Assert.IsFalse(documents.Any(static d => d.GetText().Contains("Sink", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void UsesTheNamespaceForImportedTypes()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "My.Interop", ToolVersion = TestEnvironment.ToolVersion });

        var font = documents.Single(static d => d.RelativePath == "stdole/Font.cs");
        StringAssert.Contains(font.GetText(), "namespace My.Interop.stdole;");
        StringAssert.Contains(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "global::My.Interop.stdole.Font? Font();");
    }

    [TestMethod]
    public Task GeneratesPerMemberMarshalling()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <SetMarshalling Type="^IArrays$" Member="^Matrix$" SafeArrays="wrapper" />
                <SetMarshalling Type="^IStrings$" Member="^Echo$" Parameter="^Value$" Bstrs="none" />
                <SetMarshalling Type="^Record$" Member="^Date$" Dates="wrapper" />
                <SetMarshalling Type="^IShapeEventsDual$" Member="^Renamed$" Bstrs="wrapper" />
                <SetMarshalling Type="^DShapeEvents$" Member="^Names$" SafeArrays="sysarray" />
              </ModelTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IArrays.cs" or "IStrings.cs" or "Record.cs" or "IShapeEventsDual.cs" or "DShapeEvents.cs")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GeneratesWindowsLineEndings(bool singleFile)
    {
        // The snapshots do not catch mixed line endings, because Verify normalizes them.
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            SupportCode = SupportCodeMode.Internal,
            SingleFile = singleFile,
            SingleFileName = "Interop.CsComTest.cs",
        });

        foreach (var document in documents)
        {
            Assert.IsFalse(document.GetText().Replace("\r\n", string.Empty, StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal), document.RelativePath);
        }
    }

    [TestMethod]
    public Task GeneratesFlagsAttribute()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <SyntaxTransformers>
                <AddFlagsAttribute Type="^Color$" />
              </SyntaxTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "Color.cs")));
    }

    [TestMethod]
    public Task GeneratesSafeArrayRanks()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <SetSafeArrayRank TypeName="IArrays" MemberName="Matrix" Rank="2" />
                <SetSafeArrayRank TypeName="IMarshalling" MemberName="Colors" Rank="3" />
                <SetSafeArrayRank TypeName="IMarshalling" MemberName="Strings" ParameterName="Values" Rank="2" />
                <SetSafeArrayRank TypeName="DShapeEvents" MemberName="Names" Rank="2" />
              </ModelTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IArrays.cs" or "IMarshalling.cs" or "DShapeEvents.cs" or "CsComSafeArrayMarshallers.cs")));
    }

    [TestMethod]
    public Task GeneratesDefaultOverloads()
    {
        // With the wrappers, the default values of the label (BSTR) and flag (VARIANT_BOOL) parameters of IShape.Move have no C#
        // constants, so the overloads pass them.
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            ToolVersion = TestEnvironment.ToolVersion,
            Marshalling = new MarshallingOptions
            {
                Dates = ValueMarshalling.Wrapper,
                Currencies = ValueMarshalling.Wrapper,
                Booleans = ValueMarshalling.Wrapper,
                Bstrs = BstrMarshalling.Wrapper,
            },
            OverloadsForDefaultValues = true,
        });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IShape.cs")));
    }

    [TestMethod]
    public void NamesIndexersOfDispinterfacesWithItemMembers()
    {
        // Like Excel's collections, whose _Default indexer conflicts with the Item method, and Excel's Range, whose _Default
        // getter has optional indexes, so that only its setter is an indexer, next to an Item property.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var library = new TypeLibrary { Name = "Synthetic" };
        var collection = Dispinterface("Collection");
        collection.Methods.Add(DispatchMethod("_Default", 0, InvokeKind.PropertyGet, variant, Parameter("Index", variant)));
        collection.Methods.Add(DispatchMethod("Item", 170, InvokeKind.Method, variant, Parameter("Index", variant)));
        collection.Methods.Add(DispatchMethod(
            "Render",
            6,
            InvokeKind.Method,
            new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID),
            Parameter("hdc", new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4)),
            Parameter("bounds", new PointerTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID)))));
        var range = Dispinterface("Range");
        range.Methods.Add(DispatchMethod("_Default", 0, InvokeKind.PropertyGet, variant, Parameter("Row", variant, optional: true), Parameter("Column", variant, optional: true)));
        range.Methods.Add(DispatchMethod("_Default", 0, InvokeKind.PropertyPut, new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID), Parameter("Row", variant, optional: true), Parameter("Column", variant, optional: true), Parameter("value", variant)));
        range.Methods.Add(DispatchMethod("Item", 170, InvokeKind.PropertyGet, variant, Parameter("Row", variant)));
        library.Types.Add(collection);
        library.Types.Add(range);

        var documents = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" });
        string collectionText = documents.Single(static d => d.RelativePath == "Collection.cs").GetText();
        string rangeText = documents.Single(static d => d.RelativePath == "Range.cs").GetText();

        StringAssert.Contains(collectionText, "[global::System.Runtime.CompilerServices.IndexerName(\"_Default\")]\r\n    public object? this[object? Index]");
        StringAssert.Contains(collectionText, "public object? Item(object? Index)");
        StringAssert.Contains(collectionText, "public void Render(int hdc, nint bounds)");
        StringAssert.Contains(rangeText, "[global::System.Runtime.CompilerServices.IndexerName(\"_DefaultIndexer\")]\r\n    public object? this[object? Row, object? Column] { set");
        StringAssert.Contains(rangeText, "public object? get__Default(object? Row = null, object? Column = null)");

        static InterfaceDefinition Dispinterface(string name) => new()
        {
            Name = name,
            OriginalName = name,
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };

        static MethodDefinition DispatchMethod(string name, int dispId, InvokeKind kind, TypeReference returnType, params ParameterDefinition[] parameters)
        {
            var method = new MethodDefinition { Name = name, OriginalName = name, MemberId = dispId, InvokeKind = kind, ReturnType = returnType };
            method.Parameters.AddRange(parameters);
            return method;
        }

        static ParameterDefinition Parameter(string name, TypeReference type, bool optional = false) =>
            new() { Name = name, Type = type, Flags = ParameterFlags.In | (optional ? ParameterFlags.Optional : ParameterFlags.None) };
    }

    [TestMethod]
    public void LimitsTheDefaultOverloads()
    {
        var library = new TypeLibrary { Name = "Synthetic" };
        var @interface = new InterfaceDefinition
        {
            Name = "IOptions",
            OriginalName = "IOptions",
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Vtable,
            BaseVtableSlots = 3,
        };
        var method = new MethodDefinition
        {
            Name = "Configure",
            OriginalName = "Configure",
            InvokeKind = InvokeKind.Method,
            VtableSlot = 3,
            ReturnType = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT),
        };
        method.Parameters.Add(new ParameterDefinition { Name = "id", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4), Flags = ParameterFlags.In });
        for (int i = 1; i <= 6; i++)
        {
            method.Parameters.Add(new ParameterDefinition
            {
                Name = $"flag{i}",
                Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_BOOL),
                Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault,
                DefaultValue = (short)-1,
            });
        }

        @interface.Methods.Add(method);
        library.Types.Add(@interface);

        var documents = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions
        {
            Namespace = "Test",
            Marshalling = new MarshallingOptions { Booleans = ValueMarshalling.Wrapper },
            OverloadsForDefaultValues = true,
        });

        string text = documents.Single(static d => d.RelativePath == "IOptions.cs").GetText();
        Assert.AreEqual(SignatureBuilder.MaxDefaultOverloads, text.Split("public void Configure(").Length - 1);
        StringAssert.Contains(text, "public void Configure(int id) => @this.Configure(id, global::CsCom.Runtime.ComBool.True,");
    }

    [TestMethod]
    public Task GeneratesChangedTypes()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <SetType TypeName="ICalculator" MemberName="Add" To="Color" />
                <SetType TypeName="ICalculator" MemberName="Swap" To="Color" Parameter="." />
                <SetType TypeName="IShape" MemberName="Dispatch" To="IShape" />
                <SetType TypeName="IShape" MemberName="Coordinate" To="Color" />
              </ModelTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "ICalculator.cs" or "IShape.cs")));
    }

    [TestMethod]
    public Task GeneratesExcludedTypes()
    {
        // The references to DShapeEvents become IDispatch pointers and those to Color become int; EventShape has no sink for
        // IShapeEventsVt.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <ModelTransformers>
                <ExcludeType Type="^(DShapeEvents|Color|IShapeEventsVt)$" />
              </ModelTransformers>
            </CsCom>
            """);

        Assert.IsFalse(documents.Any(static d => d.RelativePath is "DShapeEvents.cs" or "Color.cs" or "IShapeEventsVt.cs"));
        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IShape.cs")));
    }

    [TestMethod]
    public Task GeneratesAttributes()
    {
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", ToolVersion = TestEnvironment.ToolVersion }, """
            <CsCom>
              <SyntaxTransformers>
                <AddAttribute TypeName="ICircle" Attribute="System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)" />
                <AddAttribute TypeName="ICircle" Role="Extensions" Attribute='System.Obsolete("Use IShape")' />
                <AddAttribute TypeName="ICircle" MemberName="Radius" Attribute="System.CLSCompliant(false)" />
              </SyntaxTransformers>
            </CsCom>
            """);

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "ICircle.cs")));
    }

    private static IReadOnlyList<GeneratedDocument> Generate(CodeGenerationOptions options, string? configuration = null)
    {
        var pipeline = new InteropPipeline();
        return pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            options,
            configuration is null ? null : PipelineConfiguration.Parse(XDocument.Parse(configuration), pipeline.Registry));
    }

    private static IEnumerable<Target> ToTargets(IEnumerable<GeneratedDocument> documents) =>
        documents.Select(static d => new Target("cs", d.GetText(), Path.ChangeExtension(d.RelativePath, null).Replace('/', '_')));
}
