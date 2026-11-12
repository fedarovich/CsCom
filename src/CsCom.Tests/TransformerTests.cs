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
