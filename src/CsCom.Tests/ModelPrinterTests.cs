using CsCom.CodeGeneration;
using CsCom.Output;

namespace CsCom.Tests;

/// <summary>
/// Snapshot tests of the model written by <c>cscom list</c>.
/// </summary>
[TestClass]
public sealed class ModelPrinterTests : VerifyBase
{
    [TestMethod]
    public Task PrintsTheTestTypeLibrary()
    {
        var pipeline = new InteropPipeline();
        var library = pipeline.ReadTypeLibrary(TestEnvironment.TestTypeLibrary);
        pipeline.TransformModel(library, new CodeGenerationOptions { Namespace = "Test" }, sanitizeNames: false);
        using var writer = new StringWriter();

        ModelPrinter.Write(library, writer);

        return Verify(writer.ToString());
    }
}
