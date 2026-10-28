using CsCom.CodeGeneration;
using CsCom.Model;
using CsCom.Transformations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsCom.Tests;

/// <summary>
/// Tests of the annotations that associate the generated syntax nodes with the model.
/// </summary>
[TestClass]
public sealed class ModelAnnotationTests
{
    [TestMethod]
    public void AnnotationsIdentifyElementsAndRoles()
    {
        var annotations = new ModelAnnotations();
        var member = new EnumMember { Name = "A", Value = 1 };

        var declaration = annotations.GetAnnotation(member);

        Assert.AreEqual(ModelAnnotations.AnnotationKind, declaration.Kind);
        Assert.AreSame(declaration, annotations.GetAnnotation(member, SyntaxRole.Declaration));
        Assert.AreNotSame(declaration, annotations.GetAnnotation(member, SyntaxRole.Property));
        Assert.AreNotSame(declaration, annotations.GetAnnotation(new EnumMember { Name = "A", Value = 1 }), "Elements are identified by reference.");
    }

    [TestMethod]
    public void AnnotationsSurviveFormattingAndRewriting()
    {
        var annotations = new ModelAnnotations();
        var definition = new EnumDefinition { Name = "E", OriginalName = "E" };
        var member = new EnumMember { Name = "A", Value = 1 };
        var root = SyntaxFactory.CompilationUnit().AddMembers(annotations.Annotate(
            SyntaxFactory.EnumDeclaration("E").AddMembers(annotations.Annotate(SyntaxFactory.EnumMemberDeclaration("A"), member)),
            definition));

        var rewritten = new RenamingRewriter().Visit(root.NormalizeWhitespace());

        var node = annotations.GetNodes(rewritten, member).Single();
        Assert.AreEqual("B", ((EnumMemberDeclarationSyntax)node).Identifier.Text);
        Assert.AreEqual(new ModelElement(member, SyntaxRole.Declaration), annotations.GetElement(node));
        Assert.IsInstanceOfType<EnumDeclarationSyntax>(annotations.GetNodes(rewritten, definition, SyntaxRole.Declaration).Single());
        Assert.IsEmpty(annotations.GetNodes(rewritten, definition, SyntaxRole.Extensions));
        Assert.IsNull(annotations.GetElement(rewritten));
        Assert.IsEmpty(annotations.GetNodes(rewritten, new EnumMember { Name = "A", Value = 1 }));
    }

    [TestMethod]
    public void AnnotatesInterfaces()
    {
        var (library, documents, annotations) = Generate();
        var root = Root(documents, "IShape.cs");
        var shape = Get<InterfaceDefinition>(library, "IShape");

        Assert.IsInstanceOfType<InterfaceDeclarationSyntax>(annotations.GetNodes(root, shape, SyntaxRole.Declaration).Single());
        Assert.AreEqual("IShapeExtensions", ((ClassDeclarationSyntax)annotations.GetNodes(root, shape, SyntaxRole.Extensions).Single()).Identifier.Text);

        var getter = shape.Methods.Single(static m => m.Name == "Name" && m.InvokeKind == InvokeKind.PropertyGet);
        var setter = shape.Methods.Single(static m => m.Name == "Name" && m.InvokeKind == InvokeKind.PropertyPut);
        Assert.AreEqual("get_Name", ((MethodDeclarationSyntax)annotations.GetNodes(root, getter, SyntaxRole.Declaration).Single()).Identifier.Text);
        var property = (PropertyDeclarationSyntax)annotations.GetNodes(root, getter, SyntaxRole.Property).Single();
        Assert.AreEqual("Name", property.Identifier.Text);
        CollectionAssert.AreEqual(
            new[] { new ModelElement(getter, SyntaxRole.Property), new ModelElement(setter, SyntaxRole.Property) },
            annotations.GetElements(property).ToArray());

        var dx = shape.Methods.Single(static m => m.Name == "Move").Parameters[0];
        Assert.AreEqual("dx", ((ParameterSyntax)annotations.GetNodes(root, dx).Single()).Identifier.Text);

        var newEnum = shape.Methods.Single(static m => m.Name == "_NewEnum");
        Assert.AreEqual("GetEnumerator", ((MethodDeclarationSyntax)annotations.GetNodes(root, newEnum, SyntaxRole.Enumerator).Single()).Identifier.Text);
    }

    [TestMethod]
    public void AnnotatesEnumsStructuresAndModules()
    {
        var (library, documents, annotations) = Generate();

        var red = Get<EnumDefinition>(library, "Color").Members[0];
        Assert.AreEqual("Red", ((EnumMemberDeclarationSyntax)annotations.GetNodes(Root(documents, "Color.cs"), red).Single()).Identifier.Text);

        var record = Get<RecordDefinition>(library, "Record");
        var root = Root(documents, "Record.cs");
        var date = record.Fields.Single(static f => f.Name == "Date");
        Assert.AreEqual("Date", ((PropertyDeclarationSyntax)annotations.GetNodes(root, date, SyntaxRole.Declaration).Single()).Identifier.Text);
        Assert.AreEqual("_date", ((FieldDeclarationSyntax)annotations.GetNodes(root, date, SyntaxRole.BackingField).Single()).Declaration.Variables.Single().Identifier.Text);
        var values = record.Fields.Single(static f => f.Name == "Values");
        Assert.AreEqual("__Values_Buffer", ((StructDeclarationSyntax)annotations.GetNodes(root, values, SyntaxRole.InlineArray).Single()).Identifier.Text);
        Assert.IsInstanceOfType<FieldDeclarationSyntax>(annotations.GetNodes(root, values, SyntaxRole.Declaration).Single());

        var kernel = Get<ModuleDefinition>(library, "Kernel");
        root = Root(documents, "Kernel.cs");
        Assert.IsInstanceOfType<FieldDeclarationSyntax>(annotations.GetNodes(root, kernel.Constants.Single(static c => c.Name == "MaxValue")).Single());
        var beep = kernel.Functions.Single(static f => f.Name == "Beep");
        Assert.AreEqual("Beep", ((MethodDeclarationSyntax)annotations.GetNodes(root, beep).Single()).Identifier.Text);
        Assert.AreEqual("frequency", ((ParameterSyntax)annotations.GetNodes(root, beep.Parameters[0]).Single()).Identifier.Text);
    }

    [TestMethod]
    public void AnnotatesDispatchWrappersAndEvents()
    {
        var (library, documents, annotations) = Generate();
        var root = Root(documents, "DShapeEvents.cs");
        var events = Get<InterfaceDefinition>(library, "DShapeEvents");

        Assert.AreEqual("DShapeEvents", ((ClassDeclarationSyntax)annotations.GetNodes(root, events, SyntaxRole.Declaration).Single()).Identifier.Text);
        Assert.AreEqual("IDShapeEventsHandler", ((InterfaceDeclarationSyntax)annotations.GetNodes(root, events, SyntaxRole.EventHandler).Single()).Identifier.Text);
        Assert.AreEqual("DShapeEventsSink", ((ClassDeclarationSyntax)annotations.GetNodes(root, events, SyntaxRole.EventSink).Single()).Identifier.Text);

        var counter = events.Properties.Single(static p => p.Name == "Counter");
        Assert.AreEqual("Counter", ((PropertyDeclarationSyntax)annotations.GetNodes(root, counter).Single()).Identifier.Text);

        var changed = events.Methods.Single(static m => m.Name == "Changed");
        Assert.IsInstanceOfType<MethodDeclarationSyntax>(annotations.GetNodes(root, changed, SyntaxRole.Declaration).Single());
        Assert.IsInstanceOfType<MethodDeclarationSyntax>(annotations.GetNodes(root, changed, SyntaxRole.EventHandler).Single());
        Assert.AreEqual("DShapeEvents_ChangedEventHandler", ((DelegateDeclarationSyntax)annotations.GetNodes(root, changed, SyntaxRole.EventDelegate).Single()).Identifier.Text);
        Assert.IsInstanceOfType<EventFieldDeclarationSyntax>(annotations.GetNodes(root, changed, SyntaxRole.Event).Single());

        // The wrapper method, the handler method and the delegate declare the parameter.
        var parameters = annotations.GetNodes(root, changed.Parameters[0]).ToList();
        Assert.HasCount(3, parameters);
        Assert.IsTrue(parameters.All(static p => p is ParameterSyntax));
        Assert.IsTrue(parameters.Any(static p => p.Ancestors().OfType<InterfaceDeclarationSyntax>().Any()));
    }

    [TestMethod]
    public void AnnotatesEventArguments()
    {
        var (library, documents, annotations) = Generate(new CodeGenerationOptions { Namespace = "Test", EventStyle = EventStyle.Observable });
        var root = Root(documents, "_ShapeEvents.cs");
        var moved = Get<InterfaceDefinition>(library, "_ShapeEvents").Methods.Single(static m => m.Name == "Moved");

        var arguments = (ClassDeclarationSyntax)annotations.GetNodes(root, moved, SyntaxRole.EventArgs).Single();
        Assert.AreEqual("_ShapeEvents_MovedEventArgs", arguments.Identifier.Text);
        Assert.AreEqual("X", ((PropertyDeclarationSyntax)annotations.GetNodes(root, moved.Parameters[0], SyntaxRole.Property).Single()).Identifier.Text);
        Assert.IsInstanceOfType<PropertyDeclarationSyntax>(annotations.GetNodes(root, moved, SyntaxRole.Event).Single());
    }

    [TestMethod]
    public void AnnotatesOverloads()
    {
        var (library, documents, annotations) = Generate(new CodeGenerationOptions
        {
            Namespace = "Test",
            Marshalling = new MarshallingOptions { Booleans = ValueMarshalling.Wrapper },
            OverloadsForDefaultValues = true,
        });
        var move = Get<InterfaceDefinition>(library, "IShape").Methods.Single(static m => m.Name == "Move");

        var overloads = annotations.GetNodes(Root(documents, "IShape.cs"), move, SyntaxRole.Overload).ToList();

        Assert.HasCount(1, overloads);
        Assert.AreEqual("Move", ((MethodDeclarationSyntax)overloads[0]).Identifier.Text);
        Assert.IsTrue(overloads[0].Ancestors().OfType<ClassDeclarationSyntax>().Single().Identifier.Text == "IShapeExtensions");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EveryTypeHasOneDeclaration(bool singleFile)
    {
        var (library, documents, annotations) = Generate(new CodeGenerationOptions { Namespace = "Test", SingleFile = singleFile, SingleFileName = "All.cs" });

        foreach (var type in library.Types)
        {
            Assert.AreEqual(1, documents.Sum(d => annotations.GetNodes(d.Root, type, SyntaxRole.Declaration).Count()), type.ToString());
        }
    }

    private static (TypeLibrary Library, IReadOnlyList<GeneratedDocument> Documents, ModelAnnotations Annotations) Generate(CodeGenerationOptions? options = null)
    {
        var pipeline = new InteropPipeline();
        var capture = new CapturingTransformer();
        pipeline.SyntaxTransformers.Add(capture);
        var library = pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary);
        var documents = pipeline.GenerateCode(library, options ?? new CodeGenerationOptions { Namespace = "Test" });
        return (library, documents, capture.Annotations!);
    }

    private static SyntaxNode Root(IReadOnlyList<GeneratedDocument> documents, string path) => documents.Single(d => d.RelativePath == path).Root;

    private static T Get<T>(TypeLibrary library, string name)
        where T : TypeDefinition => library.Types.OfType<T>().Single(t => t.Name == name);

    private sealed class CapturingTransformer : ISyntaxTransformer
    {
        public ModelAnnotations? Annotations { get; private set; }

        public IReadOnlyList<GeneratedDocument> Transform(IReadOnlyList<GeneratedDocument> documents, TransformationContext context)
        {
            Annotations = context.Annotations;
            return documents;
        }
    }

    private sealed class RenamingRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) =>
            base.VisitEnumMemberDeclaration(node.WithIdentifier(SyntaxFactory.Identifier("B")));
    }
}
