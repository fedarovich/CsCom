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
    [DataRow(SupportCodeMode.Package)]
    [DataRow(SupportCodeMode.Public)]
    [DataRow(SupportCodeMode.Internal)]
    public void GeneratesTheLateBindingWithDynamicOnlyWithTheOption(SupportCodeMode mode)
    {
        var withoutDynamic = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = mode, ToolVersion = TestEnvironment.ToolVersion });
        var withDynamic = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", SupportCode = mode, Dynamic = true, ToolVersion = TestEnvironment.ToolVersion });

        // The activation through the extension of ComActivator in ComDynamic is called in its qualified form.
        string support = mode == SupportCodeMode.Package ? "global::CsCom.Runtime" : "global::Interop.CsComTest.ComSupport";
        Assert.IsFalse(withoutDynamic.Single(static d => d.RelativePath == "Shape.cs").GetText().Contains("CreateDynamic", StringComparison.Ordinal));
        StringAssert.Contains(
            withDynamic.Single(static d => d.RelativePath == "Shape.cs").GetText(),
            $"public static dynamic CreateDynamic({support}.ClassContext context = {support}.ComActivator.DefaultContext) => {support}.ComDynamic.CreateDynamicInstance(Clsid, context);");

        // The support code of CsCom.Runtime.Dynamic is only emitted with the option.
        Assert.IsFalse(withoutDynamic.Any(static d => d.RelativePath.EndsWith("DynamicDispatchObject.cs", StringComparison.Ordinal)));
        Assert.AreEqual(
            mode != SupportCodeMode.Package,
            withDynamic.Any(static d => d.RelativePath == "ComSupport/DynamicDispatchObject.cs"));
        Assert.AreEqual(
            mode != SupportCodeMode.Package,
            withDynamic.Any(static d => d.RelativePath == "ComSupport/DynamicEventSink.cs"));
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
    [DataRow(StructProperties.Never)]
    public Task GeneratesStructProperties(StructProperties mode)
    {
        // The default Auto mode is covered by GeneratesFilePerType.
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
    [DataRow(EventStyle.Observable)]
    [DataRow(EventStyle.Events)]
    [DataRow(EventStyle.Interface)]
    public void NamesTheFieldsOfEventSinksWithoutClashes(EventStyle style)
    {
        // The observables are stored in fields named after the events, next to the __connection field: the event named
        // connection, then connection_2, get the next free names. The event named __connection is renamed so that it does not
        // clash with the field itself. The generated code is compiled to catch duplicate members.
        var events = new InterfaceDefinition
        {
            Name = "DPortEvents",
            OriginalName = "DPortEvents",
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };
        string[] names = ["connection", "connection_2", "Closed", "__connection"];
        for (int i = 0; i < names.Length; i++)
        {
            events.Methods.Add(new MethodDefinition
            {
                Name = names[i],
                OriginalName = names[i],
                MemberId = i + 1,
                InvokeKind = InvokeKind.Method,
                ReturnType = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID),
            });
        }

        var port = new CoClassDefinition { Name = "Port", OriginalName = "Port", Guid = Guid.NewGuid() };
        port.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(events), IsDefault: true, IsSource: true, IsRestricted: false));
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(events);
        library.Types.Add(port);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", EventStyle = style, SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("Port", text, reference: runtime);

        if (style == EventStyle.Observable)
        {
            StringAssert.Contains(text, "private readonly global::CsCom.Runtime.EventConnection __connection;");
            StringAssert.Contains(text, "global::CsCom.Runtime.EventSubject<DPortEvents_connectionEventArgs> __connection_2 = new();");
            StringAssert.Contains(text, "global::CsCom.Runtime.EventSubject<DPortEvents_connection_2EventArgs> __connection_2_2 = new();");
            StringAssert.Contains(text, "global::CsCom.Runtime.EventSubject<DPortEvents_ClosedEventArgs> __Closed = new();");
        }

        if (style != EventStyle.Interface)
        {
            StringAssert.Contains(text, " __connectionEvent");
        }
    }

    [TestMethod]
    public void NamesIndexParametersWithoutClashes()
    {
        // Index parameters named value, or like @this or _target that the accessors use, get a trailing _, and more while the
        // name is taken by another index parameter. The generated code is compiled to catch duplicate parameters.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var library = new TypeLibrary { Name = "Synthetic" };

        // The default member is the extension indexer with C# 15 and a proxy like Cell with C# 14.
        var vtable = new InterfaceDefinition { Name = "IGrid", OriginalName = "IGrid", Guid = Guid.NewGuid(), InterfaceKind = InterfaceKind.Vtable, BaseVtableSlots = 3 };
        var retval = new ParameterDefinition { Name = "result", Type = new PointerTypeReference(variant), Flags = ParameterFlags.Out | ParameterFlags.Retval };
        AddAccessors(vtable, "Item", 0, new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT), retval, "value", "value_", "this", "this_");
        AddAccessors(vtable, "Cell", 1, new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT), retval, "value", "value_", "_target", "_target_");
        library.Types.Add(vtable);

        // The default member of a dispinterface is the indexer of its wrapper.
        var dispatch = new InterfaceDefinition
        {
            Name = "DGrid",
            OriginalName = "DGrid",
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };
        AddAccessors(dispatch, "_Default", 0, variant, null, "value", "value_");
        library.Types.Add(dispatch);

        string text = Generate(14);
        StringAssert.Contains(text, "public object? this[object? value__, object? value_, object? @this, object? this_]");
        StringAssert.Contains(text, "public object? this[object? value__, object? value_, object? _target__, object? _target_]");
        StringAssert.Contains(text, "get => _target.get_Cell(value__, value_, _target__, _target_);");
        StringAssert.Contains(text, "set => _target.set_Cell(value__, value_, _target__, _target_, value);");
        StringAssert.Contains(text, "public object? this[object? value__, object? value_]");
        StringAssert.Contains(text, "InvokePut(0, value, value__, value_)");

        text = Generate(15);
        StringAssert.Contains(text, "public object? this[object? value__, object? value_, object? this__, object? this_] { get => @this.get_Item(value__, value_, this__, this_); set => @this.set_Item(value__, value_, this__, this_, value); }");

        string Generate(int languageVersion)
        {
            var options = new CodeGenerationOptions
            {
                Namespace = "Test",
                LanguageVersion = languageVersion,
                NamedIndexers = NamedIndexers.ClassProperty,
                SingleFile = true,
                SingleFileName = "Test.cs",
            };
            string text = new InteropPipeline().GenerateCode(library, options).Single().GetText();
            using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
            TestCompiler.Compile("Grid", text, reference: runtime, languageVersion: languageVersion >= 15 ? Microsoft.CodeAnalysis.CSharp.LanguageVersion.Preview : Microsoft.CodeAnalysis.CSharp.LanguageVersion.Default);
            return text;
        }

        // A getter that returns its value through retval (vtable) or as the result (dispinterface), and a setter.
        static void AddAccessors(InterfaceDefinition definition, string name, int dispId, TypeReference returnType, ParameterDefinition? retval, params string[] index)
        {
            var parameters = index.Select(n => new ParameterDefinition { Name = n, Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT), Flags = ParameterFlags.In }).ToList();
            bool isVtable = definition.InterfaceKind == InterfaceKind.Vtable;
            var getter = new MethodDefinition
            {
                Name = name,
                OriginalName = name,
                MemberId = dispId,
                InvokeKind = InvokeKind.PropertyGet,
                VtableSlot = isVtable ? definition.BaseVtableSlots + definition.Methods.Count : -1,
                ReturnType = returnType,
            };
            getter.Parameters.AddRange(retval is null ? parameters : [.. parameters, retval]);
            definition.Methods.Add(getter);

            var setter = new MethodDefinition
            {
                Name = name,
                OriginalName = name,
                MemberId = dispId,
                InvokeKind = InvokeKind.PropertyPut,
                VtableSlot = isVtable ? definition.BaseVtableSlots + definition.Methods.Count : -1,
                ReturnType = isVtable ? returnType : new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID),
            };
            setter.Parameters.AddRange([.. parameters, new ParameterDefinition { Name = "item", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT), Flags = ParameterFlags.In }]);
            definition.Methods.Add(setter);
        }
    }

    [TestMethod]
    public void TypesUnsignedVariantConstantsLikeIntegerLiterals()
    {
        // The VT_UI4 and VT_UI8 values of VARIANT constants get the first of int, uint, long and ulong that can hold them, like
        // C# integer literals; the other integers stay int. The generated code is compiled to catch constants that do not fit.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var module = new ModuleDefinition { Name = "Limits", OriginalName = "Limits" };
        (string Name, object Value)[] constants = [("Small", 5u), ("Mask", 0xFFFFFFFFu), ("Large", 5_000_000_000ul), ("All", ulong.MaxValue), ("Byte", (byte)255), ("Signed", -1)];
        foreach (var (name, value) in constants)
        {
            module.Constants.Add(new ConstantDefinition { Name = name, Type = variant, Value = value });
        }

        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(module);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();
        TestCompiler.Compile("Limits", text);

        StringAssert.Contains(text, "public const int Small = 5;");
        StringAssert.Contains(text, "public const uint Mask = 4294967295;");
        StringAssert.Contains(text, "public const long Large = 5000000000;");
        StringAssert.Contains(text, "public const ulong All = 18446744073709551615;");
        StringAssert.Contains(text, "public const int Byte = 255;");
        StringAssert.Contains(text, "public const int Signed = -1;");
    }

    [TestMethod]
    [DataRow(InterfaceKind.Vtable)]
    [DataRow(InterfaceKind.Dispatch)]
    public void DefaultOverloadsPassMissingOnlyForOptionalVariants(InterfaceKind kind)
    {
        // The DATE default value of date has no C# constant, so an overload omits it. The optional VARIANTs before it are
        // passed as Missing.Value when null, but the required VARIANT keeps its null value (VT_EMPTY) and the VARIANT passed
        // by reference is passed as is: `ref state ?? Missing.Value` does not compile.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var library = new TypeLibrary { Name = "Synthetic" };
        var @interface = new InterfaceDefinition
        {
            Name = "IRecorder",
            OriginalName = "IRecorder",
            Guid = Guid.NewGuid(),
            InterfaceKind = kind,
            BaseInterface = kind == InterfaceKind.Dispatch ? new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH) : null,
            BaseVtableSlots = kind == InterfaceKind.Dispatch ? 7 : 3,
        };
        var method = new MethodDefinition
        {
            Name = "Record",
            OriginalName = "Record",
            InvokeKind = InvokeKind.Method,
            MemberId = 1,
            VtableSlot = 3,
            ReturnType = new PrimitiveTypeReference(kind == InterfaceKind.Dispatch ? System.Runtime.InteropServices.VarEnum.VT_VOID : System.Runtime.InteropServices.VarEnum.VT_HRESULT),
        };
        method.Parameters.Add(new ParameterDefinition { Name = "state", Type = new PointerTypeReference(variant), Flags = ParameterFlags.In | ParameterFlags.Out });
        method.Parameters.Add(new ParameterDefinition { Name = "key", Type = variant, Flags = ParameterFlags.In });
        method.Parameters.Add(new ParameterDefinition { Name = "hint", Type = new PointerTypeReference(variant), Flags = ParameterFlags.In | ParameterFlags.Optional });
        method.Parameters.Add(new ParameterDefinition { Name = "extra", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional });
        method.Parameters.Add(new ParameterDefinition
        {
            Name = "date",
            Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DATE),
            Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault,
            DefaultValue = new DateTime(2000, 1, 1),
        });
        @interface.Methods.Add(method);
        library.Types.Add(@interface);

        var documents = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", OverloadsForDefaultValues = true });

        // The early-bound [in] VARIANT* is an `in` parameter; the call omits the modifier, which an expression cannot have.
        string text = documents.Single(static d => d.RelativePath == "IRecorder.cs").GetText();
        string hint = kind == InterfaceKind.Dispatch ? "object? hint" : "in object? hint";
        string callee = kind == InterfaceKind.Dispatch ? "Record" : "@this.Record";
        StringAssert.Contains(
            text,
            $"public void Record(ref object? state, object? key, {hint} = null, object? extra = null) => {callee}(ref state, key, " +
            "hint ?? global::System.Reflection.Missing.Value, extra ?? global::System.Reflection.Missing.Value, global::System.DateTime.FromOADate(36526D));");
    }

    [TestMethod]
    public void VariantsWithDefaultValuesAreNotOmittedInVtableCalls()
    {
        // Nothing applies the default value of an omitted VARIANT in a call through the vtable, and C# cannot express it as
        // the default value of an object parameter, so the parameter is required and an overload passes the value.
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var find = Method("Find", 3);
        find.Parameters.Add(new ParameterDefinition { Name = "text", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_BSTR), Flags = ParameterFlags.In });
        find.Parameters.Add(new ParameterDefinition { Name = "start", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault, DefaultValue = 1 });
        find.Parameters.Add(new ParameterDefinition { Name = "options", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional });

        string text = GenerateInterface("IFinder", null, 3, find);

        StringAssert.Contains(text, "VariantMarshaller))] object? start, ");
        StringAssert.Contains(text, "OptionalVariantMarshaller))] object? options = null);");
        StringAssert.Contains(text, "public void Find(string? text) => @this.Find(text, 1, null);");
    }

    [TestMethod]
    [DataRow(15, NamedIndexers.None, "@this")]
    [DataRow(14, NamedIndexers.StructMethod, "_target")]
    public void LcidParametersHaveADefaultValueAndAreNotIndexParameters(int languageVersion, NamedIndexers namedIndexers, string target)
    {
        var @long = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4);
        var variant = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT);
        var lcid = new ParameterDefinition { Name = "lcid", Type = @long, Flags = ParameterFlags.In | ParameterFlags.Lcid };
        ParameterDefinition Retval(TypeReference type) => new() { Name = "result", Type = new PointerTypeReference(type), Flags = ParameterFlags.Out | ParameterFlags.Retval };

        // The optional parameter before the [lcid] parameter keeps its default value.
        var open = Method("Open", 3);
        open.Parameters.AddRange(
        [
            new ParameterDefinition { Name = "name", Type = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_BSTR), Flags = ParameterFlags.In },
            new ParameterDefinition { Name = "mode", Type = variant, Flags = ParameterFlags.In | ParameterFlags.Optional },
            lcid,
            Retval(@long),
        ]);

        // A property and a parameterized property, whose setter has the [lcid] parameter before the value.
        var getVisible = Method("Visible", 4, InvokeKind.PropertyGet);
        getVisible.Parameters.AddRange([lcid, Retval(@long)]);
        var getItem = Method("Item", 5, InvokeKind.PropertyGet, memberId: 0);
        getItem.Parameters.AddRange([new ParameterDefinition { Name = "index", Type = @long, Flags = ParameterFlags.In }, lcid, Retval(@long)]);
        var setItem = Method("Item", 6, InvokeKind.PropertyPut, memberId: 0);
        setItem.Parameters.AddRange(
        [
            new ParameterDefinition { Name = "index", Type = @long, Flags = ParameterFlags.In },
            lcid,
            new ParameterDefinition { Name = "value", Type = @long, Flags = ParameterFlags.In },
        ]);

        // The indexer of C# 15, or the proxy of the parameterized property.
        string text = GenerateInterface("IWorkbooks", null, 3, languageVersion, namedIndexers, open, getVisible, getItem, setItem);

        StringAssert.Contains(text, "OptionalVariantMarshaller))] object? mode = null, int lcid = 0x0400);");
        StringAssert.Contains(text, "public int Visible => @this.get_Visible(0x0400);");
        StringAssert.Contains(text, $"public int this[int index]");
        StringAssert.Contains(text, $"get => {target}.get_Item(index, 0x0400);");
        StringAssert.Contains(text, $"set => {target}.set_Item(index, 0x0400, value);");
    }

    [TestMethod]
    [DataRow(InterfaceKind.Vtable, "] ref object? result, [|] in object? source);")]
    [DataRow(InterfaceKind.Dispatch, "public void Read(ref object? result, object? source)")]
    public void PointersWithoutADirectionArePassedByReference(InterfaceKind kind, string parameters)
    {
        // Old type libraries have pointers without [in] and [out], which can return values, so they are ref parameters like
        // those of TlbImp; an [in] pointer to a VARIANT is not.
        var pointer = new PointerTypeReference(new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VARIANT));
        bool isDispatch = kind == InterfaceKind.Dispatch;
        var read = Method("Read", 3);
        read.ReturnType = new PrimitiveTypeReference(isDispatch ? System.Runtime.InteropServices.VarEnum.VT_VOID : System.Runtime.InteropServices.VarEnum.VT_HRESULT);
        read.Parameters.Add(new ParameterDefinition { Name = "result", Type = pointer, Flags = ParameterFlags.None });
        read.Parameters.Add(new ParameterDefinition { Name = "source", Type = pointer, Flags = ParameterFlags.In });
        var @interface = new InterfaceDefinition
        {
            Name = "IReader",
            OriginalName = "IReader",
            Guid = Guid.NewGuid(),
            InterfaceKind = kind,
            BaseInterface = isDispatch ? new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_DISPATCH) : null,
            BaseVtableSlots = isDispatch ? 7 : 3,
        };
        @interface.Methods.Add(read);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(@interface);

        string text = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" }).Single(static d => d.RelativePath == "IReader.cs").GetText();

        foreach (string expected in parameters.Split('|'))
        {
            StringAssert.Contains(text, expected);
        }
    }

    [TestMethod]
    public void VoidPointerRetvalsStayParameters()
    {
        // HRESULT Read([out, retval] void* buffer): the method fills the buffer of the caller, so it is not a return value,
        // for which the generated code would pass the address of a local variable.
        var @void = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_VOID);
        var read = Method("Read", 3);
        read.Parameters.Add(new ParameterDefinition { Name = "buffer", Type = new PointerTypeReference(@void), Flags = ParameterFlags.Out | ParameterFlags.Retval });

        // A void** is the address of a pointer, which is returned.
        var get = Method("Get", 4);
        get.Parameters.Add(new ParameterDefinition { Name = "result", Type = new PointerTypeReference(new PointerTypeReference(@void)), Flags = ParameterFlags.Out | ParameterFlags.Retval });

        string text = GenerateInterface("IBuffers", null, 3, read, get);

        StringAssert.Contains(text, "void Read(void* buffer);");
        StringAssert.Contains(text, "void* Get();");
    }

    [TestMethod]
    public void DefaultValuesThatDoNotFitTheirTypeAreNotGenerated()
    {
        // A type library can store the default value of a long or of an enum as an unsigned number with the same bits; a
        // value that does not fit the type has no C# constant, so the parameter is required.
        var @long = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I4);
        var @short = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_I2);
        var color = new EnumDefinition { Name = "Color", OriginalName = "Color" };
        ParameterDefinition Optional(string name, TypeReference type, object value) =>
            new() { Name = name, Type = type, Flags = ParameterFlags.In | ParameterFlags.Optional | ParameterFlags.HasDefault, DefaultValue = value };
        MethodDefinition With(string name, int slot, ParameterDefinition parameter)
        {
            var method = Method(name, slot);
            method.Parameters.Add(parameter);
            return method;
        }

        var @interface = new InterfaceDefinition { Name = "IDefaults", OriginalName = "IDefaults", Guid = Guid.NewGuid(), BaseVtableSlots = 3 };
        @interface.Methods.AddRange(
        [
            With("Bits", 3, Optional("value", @long, 0xFFFFFFFFu)),
            With("Large", 4, Optional("value", @long, 5_000_000_000L)),
            With("Small", 5, Optional("value", @short, 70000)),
            With("Flag", 6, Optional("value", new UserDefinedTypeReference(color), 0x80000000u)),
            With("Huge", 7, Optional("value", new UserDefinedTypeReference(color), 5_000_000_000L)),
        ]);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([color, @interface]);

        string text = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" }).Single(static d => d.RelativePath == "IDefaults.cs").GetText();

        StringAssert.Contains(text, "void Bits(int value = -1);");
        StringAssert.Contains(text, "void Large(int value);");
        StringAssert.Contains(text, "void Small(short value);");
        StringAssert.Contains(text, "void Flag(Color value = (Color)(-2147483648));");
        StringAssert.Contains(text, "void Huge(Color value);");
    }

    [TestMethod]
    public void InterfacesWithoutAGuidAreReplacedByIUnknown()
    {
        // An interface of a library block without the uuid attribute cannot be generated: the pointers to it are IUnknown
        // pointers, and an interface that derives from it has placeholders for its vtable slots.
        var hidden = new InterfaceDefinition { Name = "IHidden", OriginalName = "IHidden", BaseVtableSlots = 3 };
        hidden.Methods.AddRange([Method("First", 3), Method("Second", 4)]);
        var hiddenPointer = new PointerTypeReference(new UserDefinedTypeReference(hidden));

        var visible = new InterfaceDefinition
        {
            Name = "IVisible",
            OriginalName = "IVisible",
            Guid = Guid.NewGuid(),
            BaseInterface = new UserDefinedTypeReference(hidden),
            BaseVtableSlots = 5,
        };
        var use = Method("Use", 5);
        use.Parameters.Add(new ParameterDefinition { Name = "item", Type = hiddenPointer, Flags = ParameterFlags.In });
        var get = Method("Get", 6);
        get.Parameters.Add(new ParameterDefinition { Name = "result", Type = new PointerTypeReference(hiddenPointer), Flags = ParameterFlags.Out | ParameterFlags.Retval });
        visible.Methods.AddRange([use, get]);

        var thing = new CoClassDefinition { Name = "Thing", OriginalName = "Thing", Guid = Guid.NewGuid() };
        thing.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(hidden), IsDefault: true, IsSource: false, IsRestricted: false));

        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([hidden, visible, thing]);

        string text = new InteropPipeline()
            .GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();

        Assert.IsFalse(text.Contains("IHidden", StringComparison.Ordinal), text);
        StringAssert.Contains(text, "public partial interface IVisible\r\n");
        StringAssert.Contains(text, "int _VtblGap3();");
        StringAssert.Contains(text, "int _VtblGap4();");
        StringAssert.Contains(text, "object? item);");
        Assert.IsFalse(text.Contains("_VtblGap5", StringComparison.Ordinal));

        // The code compiles: nothing refers to the interface that is not generated.
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(CsCom.Runtime.DispatchObject).Assembly.Location));
        TestCompiler.Compile("Unidentified", text, reference: runtime);
    }

    [TestMethod]
    public void InterfacesDerivedFromIEnumVariantHaveItAsTheBase()
    {
        // The methods follow those of IEnumVARIANT in the vtable, so the interface derives from it.
        string text = GenerateInterface("IEnumShapes", new WellKnownTypeReference(WellKnownType.IEnumVariant), 7, Method("Extra", 7));

        StringAssert.Contains(text, "public unsafe partial interface IEnumShapes : global::CsCom.Runtime.IEnumVARIANT");
        Assert.IsFalse(text.Contains("_VtblGap", StringComparison.Ordinal));
    }

    private static MethodDefinition Method(string name, int slot, InvokeKind kind = InvokeKind.Method, int? memberId = null) => new()
    {
        Name = name,
        OriginalName = name,
        InvokeKind = kind,
        MemberId = memberId ?? slot,
        VtableSlot = slot,
        ReturnType = new PrimitiveTypeReference(System.Runtime.InteropServices.VarEnum.VT_HRESULT),
    };

    private static string GenerateInterface(string name, TypeReference? baseInterface, int baseSlots, params MethodDefinition[] methods) =>
        GenerateInterface(name, baseInterface, baseSlots, CodeGenerationOptions.MinimumLanguageVersion, NamedIndexers.None, methods);

    // Generates a vtable interface of a synthetic library with the overloads for default values.
    private static string GenerateInterface(
        string name, TypeReference? baseInterface, int baseSlots, int languageVersion, NamedIndexers namedIndexers, params MethodDefinition[] methods)
    {
        var @interface = new InterfaceDefinition { Name = name, OriginalName = name, Guid = Guid.NewGuid(), BaseInterface = baseInterface, BaseVtableSlots = baseSlots };
        @interface.Methods.AddRange(methods);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(@interface);

        var documents = new InteropPipeline().GenerateCode(
            library,
            new CodeGenerationOptions { Namespace = "Test", OverloadsForDefaultValues = true, LanguageVersion = languageVersion, NamedIndexers = namedIndexers });
        return documents.Single(d => d.RelativePath == name + ".cs").GetText();
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

    [TestMethod]
    [DataRow(14)]
    [DataRow(15)]
    public void ExtensionsOfDerivedInterfacesTakeTheInheritedAccessors(int languageVersion)
    {
        // ICircle adds setters to the read-only Name and Item of IShape. Its extension block wins for ICircle receivers, so it
        // has the getters of IShape too; the extension block of IShape keeps the getters only.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new AccessorsInDerivedInterfaceTransformer());
        var documents = pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            new CodeGenerationOptions { Namespace = "Interop.CsComTest", LanguageVersion = languageVersion, ToolVersion = TestEnvironment.ToolVersion });

        string circle = documents.Single(static d => d.RelativePath == "ICircle.cs").GetText();
        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(circle, "public string? Name { get => @this.get_Name(); set => @this.set_Name(value); }");
        StringAssert.Contains(shape, "public string? Name => @this.get_Name();");
        if (languageVersion >= 15)
        {
            StringAssert.Contains(circle, "public object? this[object? index] { get => @this.get_Item(index); set => @this.set_Item(index, value); }");
            StringAssert.Contains(shape, "public object? this[object? index] => @this.get_Item(index);");
        }
        else
        {
            Assert.IsFalse(circle.Contains("this[", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    [DataRow(NamedIndexers.ClassProperty)]
    [DataRow(NamedIndexers.StructMethod)]
    public Task GeneratesNamedIndexers(NamedIndexers mode)
    {
        // With C# 14, the default member of IShape has no extension indexer, so Item gets a proxy like Coordinate. The default
        // member of DShapeEvents is the indexer of its wrapper, and Cell gets a proxy.
        var documents = Generate(new CodeGenerationOptions { Namespace = "Interop.CsComTest", NamedIndexers = mode, ToolVersion = TestEnvironment.ToolVersion });

        return Verify(ToTargets(documents.Where(static d => d.RelativePath is "IShape.cs" or "DShapeEvents.cs")))
            .UseParameters(mode);
    }

    [TestMethod]
    public void NamedIndexersLeaveTheDefaultMemberToTheExtensionIndexer()
    {
        var documents = Generate(new CodeGenerationOptions
        {
            Namespace = "Interop.CsComTest",
            LanguageVersion = 15,
            NamedIndexers = NamedIndexers.ClassProperty,
            ToolVersion = TestEnvironment.ToolVersion,
        });

        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(shape, "public object? this[object? index] { get => @this.get_Item(index); set => @this.set_Item(index, value); }");
        StringAssert.Contains(shape, "public CoordinateIndexer Coordinate");
        Assert.IsFalse(shape.Contains("ItemIndexer", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(NamedIndexers.ClassProperty)]
    [DataRow(NamedIndexers.StructMethod)]
    public void NamedIndexersHaveTheDefaultValuesOfOptionalIndexParameters(NamedIndexers mode)
    {
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new OptionalCoordinateIndexTransformer());
        var documents = pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            new CodeGenerationOptions { Namespace = "Interop.CsComTest", NamedIndexers = mode, ToolVersion = TestEnvironment.ToolVersion });

        StringAssert.Contains(documents.Single(static d => d.RelativePath == "IShape.cs").GetText(), "public int this[int index = 0]");
    }

    [TestMethod]
    public void NamedIndexersOfDerivedInterfacesTakeTheInheritedAccessors()
    {
        // ICircle adds the setter of the default member Item of IShape, which has no extension indexer with C# 14.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new AccessorsInDerivedInterfaceTransformer());
        var documents = pipeline.GenerateCode(
            pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary),
            new CodeGenerationOptions { Namespace = "Interop.CsComTest", NamedIndexers = NamedIndexers.ClassProperty, ToolVersion = TestEnvironment.ToolVersion });

        string circle = documents.Single(static d => d.RelativePath == "ICircle.cs").GetText();
        string shape = documents.Single(static d => d.RelativePath == "IShape.cs").GetText();
        StringAssert.Contains(circle, "get => _target.get_Item(index);");
        StringAssert.Contains(circle, "set => _target.set_Item(index, value);");
        StringAssert.Contains(shape, "get => _target.get_Item(index);");
        Assert.IsFalse(shape.Contains("set_Item(index, value)", StringComparison.Ordinal));
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

/// <summary>
/// Makes the index of <c>IShape.Coordinate</c> optional with the default value 0.
/// </summary>
internal sealed class OptionalCoordinateIndexTransformer : CsCom.Transformations.IModelTransformer
{
    public void Transform(TypeLibrary library, CsCom.Transformations.TransformationContext context)
    {
        if (library.Types.OfType<InterfaceDefinition>().SingleOrDefault(static i => i.Name == "IShape") is not { } shape)
        {
            return;
        }

        var index = shape.Methods.Single(static m => m.Name == "Coordinate").Parameters[0];
        index.Flags |= ParameterFlags.Optional | ParameterFlags.HasDefault;
        index.DefaultValue = 0;
    }
}
