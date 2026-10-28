using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using CsCom.Configuration;

namespace CsCom.Tests;

/// <summary>
/// Tests of the XML schema of the configuration files and of the starter files written by <c>cscom config create</c>.
/// </summary>
[TestClass]
public sealed class ConfigurationSchemaTests
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

    [TestMethod]
    public void SchemaValidatesEveryTransformerAndAttribute()
    {
        var document = XDocument.Parse("""
            <CsCom xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="cscom.xsd">
              <SyntaxTransformers>
                <AddFlagsAttribute TypeName="Color" />
                <AddAttribute Type="^IShape$" Kind="interface" Member="^Move$" Role="Declaration" Attribute="System.Obsolete" />
                <AddAttribute TypeName="IShape" MemberName="Move" Role="EventSink" Attribute="System.Obsolete" />
              </SyntaxTransformers>
              <ModelTransformers>
                <RenameType Type="^I(.*)$" Kind="interface | dispinterface" NewName="$1" />
                <RenameType TypeName="Color" NewName="Colour" />
                <RenameMember Type="Shape" Kind="all" Member="^Move$" Parameter="^d(.)$" NewName="delta$1" />
                <RenameMember TypeName="Point" MemberName="X" NewName="Left" />
                <RenameMember MemberName="Move" ParameterName="dx" NewName="deltaX" />
                <RenameMembersToPascalCase />
                <RenameMembersToPascalCase Type="^I" Kind="interface" Member="^_" />
                <RenameMembersToPascalCase TypeName="Record" MemberName="checked" />
                <SetMarshalling Type="^IArrays$" Kind="interface" Member="Matrix" Parameter="Values"
                                Dates="bcl" Currencies="none" Booleans="wrapper" Bstrs="string" SafeArrays="sysarray" />
                <SetMarshalling TypeName="IArrays" MemberName="Matrix" ParameterName="Values" Bstrs="wrapper" SafeArrays="array" />
                <RemoveEnumPrefix />
                <RemoveEnumPrefix Type="^Anon" />
                <RemoveEnumPrefix TypeName="AnonymousEnum" />
                <ExcludeType Kind="module" />
                <ExcludeType Type="^_" />
                <ExcludeType TypeName="IShapeEventsVt" Kind="interface" />
              </ModelTransformers>
            </CsCom>
            """, LoadOptions.SetLineInfo);

        CollectionAssert.AreEqual(Array.Empty<string>(), Validate(document).ToArray());
        var configuration = PipelineConfiguration.Parse(document, TransformerRegistry.CreateDefault());
        Assert.HasCount(16, configuration.ModelTransformers);
        Assert.HasCount(3, configuration.SyntaxTransformers);
    }

    [TestMethod]
    [DataRow("<CsCom><ModelTransformers><Unknown /></ModelTransformers></CsCom>")]
    [DataRow("<CsCom><ModelTransformers><AddFlagsAttribute TypeName=\"Color\" /></ModelTransformers></CsCom>")]
    [DataRow("<CsCom><ModelTransformers><ExcludeType Kind=\"class\" /></ModelTransformers></CsCom>")]
    [DataRow("<CsCom><ModelTransformers><SetMarshalling SafeArrays=\"vector\" /></ModelTransformers></CsCom>")]
    [DataRow("<CsCom><ModelTransformers><RenameType TypeName=\"Color\" /></ModelTransformers></CsCom>")]
    [DataRow("<CsCom><SyntaxTransformers><AddAttribute TypeName=\"IShape\" Role=\"Nope\" Attribute=\"X\" /></SyntaxTransformers></CsCom>")]
    [DataRow("<CsCom><ModelTransformers /><ModelTransformers /></CsCom>")]
    public void SchemaRejectsInvalidConfigurations(string xml)
    {
        Assert.IsNotEmpty(Validate(XDocument.Parse(xml)));
    }

    [TestMethod]
    public void SchemaDeclaresEveryBuiltInTransformer()
    {
        var registry = TransformerRegistry.CreateDefault();
        var schema = XDocument.Parse(PipelineConfiguration.GetSchema());

        CollectionAssert.AreEquivalent(registry.ModelTransformerNames.ToArray(), DeclaredElements(schema, "ModelTransformers"));
        CollectionAssert.AreEquivalent(registry.SyntaxTransformerNames.ToArray(), DeclaredElements(schema, "SyntaxTransformers"));
    }

    [TestMethod]
    public void WritesStarterFiles()
    {
        string directory = TestEnvironment.CreateDirectory("starter");

        var (path, created) = PipelineConfiguration.WriteStarterFiles(directory);

        Assert.IsTrue(created);
        Assert.AreEqual(Path.Combine(directory, PipelineConfiguration.DefaultFileName), path);
        Assert.AreEqual(PipelineConfiguration.GetSchema().ReplaceLineEndings(), File.ReadAllText(Path.Combine(directory, PipelineConfiguration.SchemaFileName)));
        CollectionAssert.AreEqual(Array.Empty<string>(), Validate(XDocument.Load(path)).ToArray());
        var configuration = PipelineConfiguration.Load(path, TransformerRegistry.CreateDefault());
        Assert.IsEmpty(configuration.ModelTransformers);
        Assert.IsEmpty(configuration.SyntaxTransformers);

        // An existing configuration is kept, and only the schema is written again.
        File.WriteAllText(path, "<CsCom><ModelTransformers><RemoveEnumPrefix /></ModelTransformers></CsCom>");
        File.Delete(Path.Combine(directory, PipelineConfiguration.SchemaFileName));
        Assert.IsFalse(PipelineConfiguration.WriteStarterFiles(directory).Created);
        StringAssert.Contains(File.ReadAllText(path), "RemoveEnumPrefix");
        Assert.IsTrue(File.Exists(Path.Combine(directory, PipelineConfiguration.SchemaFileName)));
    }

    private static List<string> Validate(XDocument document)
    {
        var schemas = new XmlSchemaSet();
        using (var reader = XmlReader.Create(new StringReader(PipelineConfiguration.GetSchema())))
        {
            schemas.Add(null, reader);
        }

        var errors = new List<string>();
        document.Validate(schemas, (_, e) => errors.Add(e.Message));
        return errors;
    }

    private static string[] DeclaredElements(XDocument schema, string section) =>
    [
        .. schema.Root!.Elements(Xs + "complexType")
            .Single(t => (string?)t.Attribute("name") == section)
            .Descendants(Xs + "element")
            .Select(static e => (string)e.Attribute("name")!),
    ];
}
