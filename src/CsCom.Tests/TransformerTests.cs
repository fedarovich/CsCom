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
