using System.Xml.Linq;
using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using CsCom.Transformations;

namespace CsCom.Tests;

/// <summary>
/// Tests of the configuration files that add transformers to the pipeline.
/// </summary>
[TestClass]
public sealed class ConfigurationTests
{
    private static readonly TransformerRegistry Registry = TestTransformers.Register(new TransformerRegistry());

    [TestMethod]
    [DataRow("<CsCom />")]
    [DataRow("<CsCom><ModelTransformers /><SyntaxTransformers /></CsCom>")]
    public void EmptyConfigurationsHaveNoTransformers(string xml)
    {
        var configuration = Parse(xml);

        Assert.IsEmpty(configuration.ModelTransformers);
        Assert.IsEmpty(configuration.SyntaxTransformers);
    }

    [TestMethod]
    public void CreatesTheTransformersInDocumentOrder()
    {
        var configuration = Parse("""
            <?xml version="1.0" encoding="utf-8"?>
            <CsCom xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="cscom.xsd">
              <!-- The syntax transformers can come first. -->
              <SyntaxTransformers>
                <RemoveFile Name="IShape.cs" />
              </SyntaxTransformers>
              <ModelTransformers>
                <Rename Name="A" NewName="B" />
                <Options Enabled="TRUE" Mode="auto">
                  <Item>x</Item>
                  <Item><![CDATA[y]]></Item>
                </Options>
                <Rename Name="C" NewName="D" />
              </ModelTransformers>
            </CsCom>
            """);

        Assert.HasCount(3, configuration.ModelTransformers);
        var first = (TestRenameTransformer)configuration.ModelTransformers[0];
        Assert.AreEqual(("A", "B"), (first.Name, first.NewName));
        var options = (OptionsTransformer)configuration.ModelTransformers[1];
        Assert.IsTrue(options.Enabled);
        Assert.AreEqual(StructProperties.Auto, options.Mode);
        CollectionAssert.AreEqual(new[] { "x", "y" }, options.Items.ToArray());
        Assert.AreEqual("C", ((TestRenameTransformer)configuration.ModelTransformers[2]).Name);
        Assert.AreEqual("IShape.cs", ((RemoveFileTransformer)configuration.SyntaxTransformers.Single()).Name);
    }

    [TestMethod]
    public void OptionalAttributesHaveDefaults()
    {
        var options = (OptionsTransformer)Parse("<CsCom><ModelTransformers><Options /></ModelTransformers></CsCom>").ModelTransformers.Single();

        Assert.IsFalse(options.Enabled);
        Assert.AreEqual(StructProperties.Never, options.Mode);
        Assert.IsEmpty(options.Items);
    }

    [TestMethod]
    public void ConfiguredTransformersRunBetweenTheBuiltInOnes()
    {
        var pipeline = new InteropPipeline();
        TestTransformers.Register(pipeline.Registry);

        // AliasResolver names the anonymous enum, the transformer of the pipeline renames it, the configured one renames it
        // again, and NameSanitizer makes the name an identifier.
        pipeline.ModelTransformers.Add(new TestRenameTransformer("AnonymousEnum", "ApiEnum"));
        var configuration = PipelineConfiguration.Parse(XDocument.Parse("""
            <CsCom>
              <ModelTransformers>
                <Rename Name="ApiEnum" NewName="Renamed Enum" />
              </ModelTransformers>
              <SyntaxTransformers>
                <RemoveFile Name="IShape.cs" />
              </SyntaxTransformers>
            </CsCom>
            """), pipeline.Registry);

        var documents = pipeline.GenerateCode(pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary), new CodeGenerationOptions { Namespace = "Test" }, configuration);

        var files = documents.Select(static d => Path.GetFileName(d.RelativePath)).ToList();
        CollectionAssert.Contains(files, "Renamed_Enum.cs");
        CollectionAssert.DoesNotContain(files, "AnonymousEnum.cs");
        CollectionAssert.DoesNotContain(files, "IShape.cs");
        CollectionAssert.Contains(files, "ICircle.cs");
    }

    [TestMethod]
    [DataRow("<Config />", "cscom.xml(1,2): The root element must be <CsCom> without a namespace.")]
    [DataRow("<CsCom xmlns='urn:cscom' />", "cscom.xml(1,2): The root element must be <CsCom> without a namespace.")]
    [DataRow("<CsCom Version='1' />", "cscom.xml(1,8): The attribute 'Version' is not supported by <CsCom>.")]
    [DataRow("<CsCom><Transformers /></CsCom>", "cscom.xml(1,9): The element <Transformers> is not supported by <CsCom>.")]
    [DataRow("<CsCom>text</CsCom>", "cscom.xml(1,8): <CsCom> does not support text.")]
    [DataRow("<CsCom><ModelTransformers /><ModelTransformers /></CsCom>", "cscom.xml(1,30): <ModelTransformers> can only be specified once.")]
    [DataRow("<CsCom><ModelTransformers Order='1' /></CsCom>", "cscom.xml(1,27): The attribute 'Order' is not supported by <ModelTransformers>.")]
    [DataRow(
        "<CsCom><ModelTransformers><Renamer /></ModelTransformers></CsCom>",
        "cscom.xml(1,28): Unknown model transformer <Renamer>. Available: Options, Rename.")]
    [DataRow(
        "<CsCom xmlns:x='urn:x'><ModelTransformers><x:RenameType Name='A' NewName='B' /></ModelTransformers></CsCom>",
        "cscom.xml(1,44): Unknown model transformer <RenameType>. Available: Options, Rename.")]
    [DataRow(
        "<CsCom><SyntaxTransformers><Rename /></SyntaxTransformers></CsCom>",
        "cscom.xml(1,29): Unknown syntax transformer <Rename>. Available: RemoveFile.")]
    [DataRow(
        "<CsCom><ModelTransformers><Rename Name='A' /></ModelTransformers></CsCom>",
        "cscom.xml(1,28): The attribute 'NewName' is required by <Rename>.")]
    [DataRow(
        "<CsCom><ModelTransformers><Rename Name='A' NewName='B' NewNmae='C' /></ModelTransformers></CsCom>",
        "cscom.xml(1,56): The attribute 'NewNmae' is not supported by <Rename>.")]
    [DataRow(
        "<CsCom><ModelTransformers><Rename Name='A' NewName='B'><Extra /></Rename></ModelTransformers></CsCom>",
        "cscom.xml(1,57): The element <Extra> is not supported by <Rename>.")]
    [DataRow(
        "<CsCom><ModelTransformers><Rename Name='A' NewName='B'>C</Rename></ModelTransformers></CsCom>",
        "cscom.xml(1,56): <Rename> does not support text.")]
    [DataRow(
        "<CsCom><ModelTransformers><Rename Name='A' NewName='' /></ModelTransformers></CsCom>",
        "cscom.xml(1,28): The new name must not be empty.")]
    [DataRow(
        "<CsCom><ModelTransformers><Options><Item Kind='x'>a</Item></Options></ModelTransformers></CsCom>",
        "cscom.xml(1,42): The attribute 'Kind' is not supported by <Item>.")]
    [DataRow(
        "<CsCom><ModelTransformers><Options Enabled='yes' /></ModelTransformers></CsCom>",
        "cscom.xml(1,36): 'yes' is not a valid value of the attribute 'Enabled': true or false is expected.")]
    [DataRow(
        "<CsCom><ModelTransformers><Options Mode='1' /></ModelTransformers></CsCom>",
        "cscom.xml(1,36): '1' is not a valid value of the attribute 'Mode': one of Auto, Never is expected.")]
    [DataRow(
        "<CsCom><ModelTransformers><Options Mode='Auto, Never' /></ModelTransformers></CsCom>",
        "cscom.xml(1,36): 'Auto, Never' is not a valid value of the attribute 'Mode': one of Auto, Never is expected.")]
    public void ReportsErrorsWithTheirLocation(string xml, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => Parse(xml, "cscom.xml"));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void ReportsThatNoTransformersAreAvailable()
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => PipelineConfiguration.Parse(
            XDocument.Parse("<CsCom><ModelTransformers><Renamer /></ModelTransformers></CsCom>", LoadOptions.SetLineInfo),
            new TransformerRegistry()));

        Assert.AreEqual("Line 1, position 28: Unknown model transformer <Renamer>. No model transformers are available.", exception.Message);
    }

    [TestMethod]
    public void LoadsFiles()
    {
        string directory = TestEnvironment.CreateDirectory("configuration-files");
        string path = Path.Combine(directory, "cscom.xml");
        File.WriteAllText(path, """
            <CsCom>
              <ModelTransformers>
                <Rename Name="A" NewName="B" />
              </ModelTransformers>
            </CsCom>
            """);
        Assert.HasCount(1, PipelineConfiguration.Load(path, Registry).ModelTransformers);

        File.WriteAllText(path, """
            <CsCom>
              <ModelTransformers>
                <Renamer />
              </ModelTransformers>
            </CsCom>
            """);
        var exception = Assert.ThrowsExactly<CsComException>(() => PipelineConfiguration.Load(path, Registry));
        StringAssert.StartsWith(exception.Message, $"{path}(3,6): Unknown model transformer <Renamer>.");

        File.WriteAllText(path, "<CsCom><ModelTransformers></CsCom>");
        exception = Assert.ThrowsExactly<CsComException>(() => PipelineConfiguration.Load(path, Registry));
        StringAssert.StartsWith(exception.Message, $"'{path}' is not a valid XML file:");

        File.WriteAllText(path, "<!DOCTYPE CsCom [<!ENTITY name 'value'>]><CsCom />");
        exception = Assert.ThrowsExactly<CsComException>(() => PipelineConfiguration.Load(path, Registry));
        StringAssert.StartsWith(exception.Message, $"'{path}' is not a valid XML file:");

        string missing = Path.Combine(directory, "missing.xml");
        exception = Assert.ThrowsExactly<CsComException>(() => PipelineConfiguration.Load(missing, Registry));
        Assert.AreEqual($"The configuration file '{missing}' was not found.", exception.Message);
    }

    [TestMethod]
    public void RegistryValidatesNames()
    {
        var registry = new TransformerRegistry().AddModelTransformer("Rename", static _ => new TestRenameTransformer("A", "B"));

        Assert.ThrowsExactly<ArgumentException>(() => registry.AddModelTransformer("Rename", static _ => new TestRenameTransformer("A", "B")));
        Assert.ThrowsExactly<ArgumentException>(() => registry.AddModelTransformer("Rename Type", static _ => new TestRenameTransformer("A", "B")));
        Assert.ThrowsExactly<ArgumentException>(() => registry.AddModelTransformer("x:Rename", static _ => new TestRenameTransformer("A", "B")));
        registry.AddSyntaxTransformer("Rename", static _ => new RemoveFileTransformer("A.cs"));
        CollectionAssert.AreEqual(new[] { "Rename" }, registry.ModelTransformerNames.ToArray());
        CollectionAssert.AreEqual(new[] { "Rename" }, registry.SyntaxTransformerNames.ToArray());
    }

    private static PipelineConfiguration Parse(string xml, string? path = null) =>
        PipelineConfiguration.Parse(XDocument.Parse(xml, LoadOptions.SetLineInfo), Registry, path);
}

/// <summary>
/// The transformers registered by the tests.
/// </summary>
internal static class TestTransformers
{
    public static TransformerRegistry Register(TransformerRegistry registry) => registry
        .AddModelTransformer("Rename", static e =>
        {
            string name = e.GetRequiredAttribute("Name");
            string newName = e.GetRequiredAttribute("NewName");
            return newName.Length > 0 ? new TestRenameTransformer(name, newName) : throw new ArgumentException("The new name must not be empty.");
        })
        .AddModelTransformer("Options", static e => new OptionsTransformer(
            e.GetBoolean("Enabled", defaultValue: false),
            e.GetEnum("Mode", StructProperties.Never),
            [.. e.Elements("Item").Select(static i => i.Value)]))
        .AddSyntaxTransformer("RemoveFile", static e => new RemoveFileTransformer(e.GetRequiredAttribute("Name")));
}

/// <summary>Renames the types with the specified name.</summary>
internal sealed class TestRenameTransformer(string name, string newName) : IModelTransformer
{
    public string Name { get; } = name;

    public string NewName { get; } = newName;

    public void Transform(TypeLibrary library, TransformationContext context)
    {
        foreach (var type in library.Types.Where(t => t.Name == Name))
        {
            type.Name = NewName;
        }
    }
}

/// <summary>Records its options without changing the model.</summary>
internal sealed class OptionsTransformer(bool enabled, StructProperties mode, IReadOnlyList<string> items) : IModelTransformer
{
    public bool Enabled { get; } = enabled;

    public StructProperties Mode { get; } = mode;

    public IReadOnlyList<string> Items { get; } = items;

    public void Transform(TypeLibrary library, TransformationContext context)
    {
    }
}

/// <summary>Removes the generated files with the specified name.</summary>
internal sealed class RemoveFileTransformer(string name) : ISyntaxTransformer
{
    public string Name { get; } = name;

    public IReadOnlyList<GeneratedDocument> Transform(IReadOnlyList<GeneratedDocument> documents, TransformationContext context) =>
        [.. documents.Where(d => !string.Equals(Path.GetFileName(d.RelativePath), Name, StringComparison.OrdinalIgnoreCase))];
}
