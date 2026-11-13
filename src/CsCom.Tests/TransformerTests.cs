using System.Runtime.InteropServices;
using CsCom.CodeGeneration;
using CsCom.Model;
using CsCom.Transformations;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Tests;

[TestClass]
public sealed class TransformerTests
{
    private static readonly TransformationContext Context = new()
    {
        Options = new CodeGenerationOptions { Namespace = "Test" },
        Logger = NullLogger.Instance,
    };

    [TestMethod]
    public void AliasResolverRenamesAnonymousTypesAndRemovesAliases()
    {
        var library = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);

        new AliasResolver().Transform(library, Context);

        Assert.IsFalse(library.Types.OfType<AliasDefinition>().Any());
        var anonymous = library.Types.OfType<EnumDefinition>().Single(static e => e.Name == "AnonymousEnum");
        Assert.IsTrue(anonymous.OriginalName.StartsWith("__MIDL", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AliasResolverResolvesAliasChains()
    {
        var library = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);

        new AliasResolver().Transform(library, Context);

        var getHandle = library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "ICalculator").Methods.Single(static m => m.Name == "GetHandle");
        Assert.AreEqual(new PointerTypeReference(new PrimitiveTypeReference(VarEnum.VT_I4)), getHandle.Parameters[0].Type);
    }

    [TestMethod]
    public void AliasResolverResolvesAliasesOfPointersToAliases()
    {
        // typedef struct tagItem Item; typedef Item* PItem; the interface that uses PItem precedes it in the library.
        var record = new RecordDefinition { Name = "tagItem", OriginalName = "tagItem" };
        var item = new AliasDefinition { Name = "Item", OriginalName = "Item", Target = new UserDefinedTypeReference(record) };
        var pointer = new AliasDefinition { Name = "PItem", OriginalName = "PItem", Target = new PointerTypeReference(new UserDefinedTypeReference(item)) };
        var items = new InterfaceDefinition { Name = "IItems", OriginalName = "IItems", Guid = Guid.NewGuid(), BaseVtableSlots = 3 };
        var add = new MethodDefinition { Name = "Add", OriginalName = "Add", VtableSlot = 3, ReturnType = new PrimitiveTypeReference(VarEnum.VT_HRESULT) };
        add.Parameters.Add(new ParameterDefinition { Name = "item", Type = new UserDefinedTypeReference(pointer), Flags = ParameterFlags.In });
        add.Parameters.Add(new ParameterDefinition { Name = "items", Type = new SafeArrayTypeReference(new UserDefinedTypeReference(pointer)), Flags = ParameterFlags.In });
        items.Methods.Add(add);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([record, items, pointer, item]);

        new AliasResolver().Transform(library, Context);

        var resolved = new PointerTypeReference(new UserDefinedTypeReference(record));
        Assert.AreEqual(resolved, add.Parameters[0].Type);
        Assert.AreEqual(new SafeArrayTypeReference(resolved), add.Parameters[1].Type);
        CollectionAssert.AreEqual(new TypeDefinition[] { record, items }, library.Types);
    }

    [TestMethod]
    public void TypeExcluderKeepsTheStructuresOfArraysPassedByPointer()
    {
        // A SAFEARRAY(Item)* retval holds the structures by value, unlike an Item*, which becomes a void*.
        var item = new RecordDefinition { Name = "Item", OriginalName = "Item" };
        var items = new InterfaceDefinition { Name = "IItems", OriginalName = "IItems", Guid = Guid.NewGuid(), BaseVtableSlots = 3 };
        var all = new MethodDefinition { Name = "All", OriginalName = "All", VtableSlot = 3, ReturnType = new PrimitiveTypeReference(VarEnum.VT_HRESULT) };
        all.Parameters.Add(new ParameterDefinition
        {
            Name = "result",
            Type = new PointerTypeReference(new SafeArrayTypeReference(new UserDefinedTypeReference(item))),
            Flags = ParameterFlags.Out | ParameterFlags.Retval,
        });
        var first = new MethodDefinition { Name = "First", OriginalName = "First", VtableSlot = 4, ReturnType = new PrimitiveTypeReference(VarEnum.VT_HRESULT) };
        first.Parameters.Add(new ParameterDefinition { Name = "item", Type = new PointerTypeReference(new UserDefinedTypeReference(item)), Flags = ParameterFlags.In });
        items.Methods.Add(first);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange([item, items]);
        var excluder = new TypeExcluder(new TypeSelector(NameSelector.FromName("Item")));

        // The pointer to the structure alone does not keep it.
        excluder.Transform(library, Context);
        Assert.AreEqual(new PointerTypeReference(new PrimitiveTypeReference(VarEnum.VT_VOID)), first.Parameters[0].Type);

        library.Types.Insert(0, item);
        items.Methods.Add(all);
        var exception = Assert.ThrowsExactly<CsComException>(() => excluder.Transform(library, Context));
        Assert.AreEqual("The structure 'Item' cannot be excluded, because 'IItems.All' uses it.", exception.Message);
    }

    [TestMethod]
    [DataRow("(.*)", "$1Async", "Load", "LoadAsync")]
    [DataRow(".*", "X", "Load", "X")]
    [DataRow("^", "I", "Load", "ILoad")]
    [DataRow("$", "Async", "Load", "LoadAsync")]
    [DataRow("_", "", "a_b_c", "abc")]
    [DataRow("^_(.*)$", "$1", "_Application", "Application")]
    [DataRow("x*", "-", "xab", "-a-b-")]
    public void RegularExpressionsDoNotReplaceTheEmptyMatchAfterAMatch(string pattern, string newName, string name, string expected)
    {
        // (.*) matches the name and then the empty string at its end, which would add the new name twice.
        Assert.AreEqual(expected, NameSelector.FromPattern(new System.Text.RegularExpressions.Regex(pattern)).Replace(name, newName));
    }

    [TestMethod]
    public void ReportsTheRegularExpressionThatTakesTooLong()
    {
        // The regular expressions of configuration files have a time limit.
        var pipeline = new InteropPipeline();
        pipeline.ModelTransformers.Add(new SlowTransformer());

        var exception = Assert.ThrowsExactly<CsComException>(() => pipeline.TransformModel(new TypeLibrary { Name = "Synthetic" }, Context.Options));

        StringAssert.StartsWith(exception.Message, "The regular expression '(a+)+$' takes too long to match 'aaaa");
    }

    private sealed class SlowTransformer : IModelTransformer
    {
        public void Transform(TypeLibrary library, TransformationContext context) =>
            new System.Text.RegularExpressions.Regex("(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(20)).IsMatch(new string('a', 60) + "b");
    }

    [TestMethod]
    public void NameSanitizerKeepsAPropertyNamedLikeTheFormerNameOfAMethod()
    {
        // The method Refresh was renamed to Reload, and the property Value to Refresh: the property is not the method.
        var events = new InterfaceDefinition { Name = "IView", OriginalName = "IView", Guid = Guid.NewGuid(), InterfaceKind = InterfaceKind.Dispatch };
        var method = new MethodDefinition { Name = "Reload", OriginalName = "Refresh", ReturnType = new PrimitiveTypeReference(VarEnum.VT_VOID) };
        var property = new DispatchPropertyDefinition { Name = "Refresh", Type = new PrimitiveTypeReference(VarEnum.VT_I4) };
        events.Methods.Add(method);
        events.Properties.Add(property);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(events);

        new NameSanitizer().Transform(library, Context);

        Assert.AreEqual(("Reload", "Refresh"), (method.Name, property.Name));
    }

    [TestMethod]
    public void NameSanitizerReportsTheTypesThatItRenames()
    {
        // The copy that another version of the library imported is generated into the namespace of the library too.
        var library = new TypeLibrary { Name = "Shapes", Guid = Guid.NewGuid(), MajorVersion = 2 };
        var other = new TypeLibraryReference("Shapes", library.Guid, 1, 0);
        var first = new EnumDefinition { Name = "Kind", OriginalName = "Kind" };
        var second = new EnumDefinition { Name = "Kind", OriginalName = "_Kind" };
        var imported = new EnumDefinition { Name = "Kind", OriginalName = "Kind", ImportedFrom = other };
        var elsewhere = new EnumDefinition { Name = "Kind", OriginalName = "Kind", ImportedFrom = new TypeLibraryReference("Other", Guid.NewGuid(), 1, 0) };
        library.Types.AddRange([first, second, imported, elsewhere]);
        var logger = new WarningCollector<NameSanitizer>();

        new NameSanitizer().Transform(library, new TransformationContext { Options = Context.Options, Logger = logger });

        CollectionAssert.AreEqual(new[] { "Kind", "Kind_", "Kind__", "Kind" }, library.Types.Select(static t => t.Name).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "The type 'Kind' is renamed to 'Kind_', because another type of the library has the name",
                "The type 'Kind' is renamed to 'Kind__', because another type of the library has the name",
            },
            logger.Messages);
    }

    [TestMethod]
    public void NameSanitizerRenamesMembersThatClashWithTheWrapperBaseClass()
    {
        var library = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);

        new NameSanitizer().Transform(library, Context);

        var events = library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "DShapeEvents");
        Assert.IsTrue(events.Methods.Any(static m => m.Name == "Create_"));
        Assert.IsFalse(events.Methods.Any(static m => m.Name == "Create"));
    }

    [TestMethod]
    public void NameSanitizerKeepsAccessorsOfTheSamePropertyTogether()
    {
        var library = new TypeLibraryReader().Read(TestEnvironment.TestTypeLibrary);

        new NameSanitizer().Transform(library, Context);

        var shape = library.Types.OfType<InterfaceDefinition>().Single(static i => i.Name == "IShape");
        Assert.AreEqual(2, shape.Methods.Count(static m => m.Name == "Name"));
        Assert.AreEqual(3, shape.Methods.Count(static m => m.Name == "Tag"));
    }

    [TestMethod]
    [DataRow("Valid_Name1", "Valid_Name1")]
    [DataRow("1abc", "_1abc")]
    [DataRow("with space", "with_space")]
    [DataRow("a.b-c", "a_b_c")]
    [DataRow("", "_")]
    public void ToIdentifierProducesValidIdentifiers(string name, string expected)
    {
        Assert.AreEqual(expected, NameSanitizer.ToIdentifier(name));
    }
}
