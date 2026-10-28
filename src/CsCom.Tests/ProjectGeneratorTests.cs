using CsCom.CodeGeneration;
using CsCom.Output;

namespace CsCom.Tests;

[TestClass]
public sealed class ProjectGeneratorTests
{
    private readonly ProjectGenerator _generator = new(new InteropPipeline());

    [TestMethod]
    public void CreatesProjectWithDefaults()
    {
        string directory = Path.Combine(TestEnvironment.CreateDirectory("defaults"), "Project");

        string project = _generator.CreateProject(new NewProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, OutputDirectory = directory });

        Assert.AreEqual(Path.Combine(directory, "Interop.CsComTest.csproj"), project);
        string text = File.ReadAllText(project);
        StringAssert.Contains(text, "<TargetFramework>net10.0-windows</TargetFramework>");
        StringAssert.Contains(text, "<LangVersion>14</LangVersion>");
        StringAssert.Contains(text, "<RootNamespace>Interop.CsComTest</RootNamespace>");
        StringAssert.Contains(text, $"<PackageReference Include=\"CsCom.Runtime\" Version=\"{ToolInfo.Version}\" />");
        StringAssert.Contains(text, "DisableRuntimeMarshallingAttribute");
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "IShape.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "nuget.config")));

        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual("Interop.CsComTest", manifest.Namespace);
        Assert.AreEqual(14, manifest.LanguageVersion);
        Assert.AreEqual(SupportCodeMode.Package, manifest.SupportCode);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"supportCode\": \"package\"");
    }

    [TestMethod]
    public void CreatesProjectWithOptions()
    {
        string directory = TestEnvironment.CreateDirectory("options");

        string project = _generator.CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = directory,
            Name = "My.Test",
            Namespace = "My.Test.Interop",
            TargetFrameworks = ["net10.0-windows", "net11.0-windows"],
            RuntimeIdentifiers = ["AnyCPU", "win-x64", "win-arm64"],
            LanguageVersion = 15,
            SingleFile = true,
            SupportCode = SupportCodeMode.Public,
            PackageSource = TestEnvironment.LocalPackages,
        });

        Assert.AreEqual(Path.Combine(directory, "My.Test.csproj"), project);
        string text = File.ReadAllText(project);
        StringAssert.Contains(text, "<TargetFrameworks>net10.0-windows;net11.0-windows</TargetFrameworks>");
        StringAssert.Contains(text, "<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>");
        StringAssert.Contains(text, "<LangVersion>15</LangVersion>");
        Assert.IsFalse(text.Contains("CsCom.Runtime", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "My.Test.cs" }, Directory.GetFiles(Path.Combine(directory, "Generated")).Select(Path.GetFileName).ToArray());
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "nuget.config")), TestEnvironment.LocalPackages.TrimEnd('\\'));
    }

    [TestMethod]
    public void OmitsThePackageVersionWhenCentralPackageManagementIsEnabled()
    {
        string root = TestEnvironment.CreateDirectory("cpm");
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");

        string project = _generator.CreateProject(new NewProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, OutputDirectory = Path.Combine(root, "src", "Interop") });

        StringAssert.Contains(File.ReadAllText(project), "<PackageReference Include=\"CsCom.Runtime\" />");
    }

    [TestMethod]
    public void RefusesToOverwriteProjects()
    {
        string directory = TestEnvironment.CreateDirectory("overwrite");
        _generator.CreateProject(new NewProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, OutputDirectory = directory });

        var exception = Assert.ThrowsExactly<CsComException>(() =>
            _generator.CreateProject(new NewProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, OutputDirectory = directory }));
        StringAssert.Contains(exception.Message, "cscom update project");
    }

    [TestMethod]
    [DataRow("net8.0-windows", null, "not supported")]
    [DataRow("net10.0-windows", 13, "C# 14 or later is required")]
    [DataRow("net11.0-windows", 14, "C# 15 or later is required")]
    public void ValidatesTargetFrameworksAndLanguageVersions(string targetFramework, int? languageVersion, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = TestEnvironment.CreateDirectory("invalid"),
            TargetFrameworks = [targetFramework],
            LanguageVersion = languageVersion,
        }));

        StringAssert.Contains(exception.Message, message);
    }

    [TestMethod]
    [DataRow("1Invalid")]
    [DataRow("My.class")]
    [DataRow("My..Namespace")]
    public void ValidatesNamespaces(string @namespace)
    {
        Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = TestEnvironment.CreateDirectory("namespace"),
            Namespace = @namespace,
        }));
    }

    [TestMethod]
    public void UpdatesGeneratedFilesAndKeepsUserFiles()
    {
        string directory = TestEnvironment.CreateDirectory("update");
        string project = _generator.CreateProject(new NewProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, OutputDirectory = directory });
        string userFile = Path.Combine(directory, "Extensions.cs");
        File.WriteAllText(userFile, "// user code");
        File.WriteAllText(Path.Combine(directory, "Generated", "Stale.cs"), "// stale");

        _generator.UpdateProject(new UpdateProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            ProjectDirectory = directory,
            Namespace = "Renamed",
            LanguageVersion = 15,
            SingleFile = true,
        });

        Assert.IsTrue(File.Exists(userFile));
        CollectionAssert.AreEqual(new[] { "Interop.CsComTest.cs" }, Directory.GetFiles(Path.Combine(directory, "Generated"), "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToArray());
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Interop.CsComTest.cs")), "namespace Renamed");
        StringAssert.Contains(File.ReadAllText(project), "<LangVersion>15</LangVersion>");

        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual("Renamed", manifest.Namespace);
        Assert.AreEqual(15, manifest.LanguageVersion);
        Assert.IsTrue(manifest.SingleFile);
    }

    [TestMethod]
    public void UpdateKeepsTheRecordedOptions()
    {
        string directory = TestEnvironment.CreateDirectory("update-defaults");
        _generator.CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = directory,
            Namespace = "Custom.Namespace",
            SingleFile = true,
        });

        _generator.UpdateProject(new UpdateProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, ProjectDirectory = directory });

        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual("Custom.Namespace", manifest.Namespace);
        Assert.IsTrue(manifest.SingleFile);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(directory, "Generated")).Length);
    }

    [TestMethod]
    public void RecordsAndUpdatesMarshallingOptions()
    {
        string directory = TestEnvironment.CreateDirectory("marshalling");
        _generator.CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = directory,
            Marshalling = new MarshallingOptions { Dates = ValueMarshalling.Wrapper, Booleans = ValueMarshalling.Wrapper },
        });

        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"dates\": \"wrapper\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Record.cs")), "global::CsCom.Runtime.ComBool Flag;");

        _generator.UpdateProject(new UpdateProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            ProjectDirectory = directory,
            Marshalling = new MarshallingOverrides { Dates = ValueMarshalling.None, Currencies = ValueMarshalling.Wrapper },
        });

        var marshalling = CsComManifest.Read(directory).Marshalling;
        Assert.AreEqual(
            new MarshallingOptions { Dates = ValueMarshalling.None, Currencies = ValueMarshalling.Wrapper, Booleans = ValueMarshalling.Wrapper },
            marshalling);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "IMarshalling.cs")), "double Dates(double Date, out double Next);");
    }

    [TestMethod]
    public void ReadsManifestsWithoutMarshallingOptions()
    {
        string directory = TestEnvironment.CreateDirectory("old-manifest");
        File.WriteAllText(Path.Combine(directory, CsComManifest.FileName), """
            {
              "toolVersion": "0.1.0",
              "typeLibrary": "C:\\Windows\\System32\\scrrun.dll",
              "name": "Interop.Scripting",
              "namespace": "Interop.Scripting",
              "targetFrameworks": [ "net10.0-windows" ],
              "languageVersion": 14,
              "supportCode": "package"
            }
            """);

        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual(new MarshallingOptions(), manifest.Marshalling);
        Assert.AreEqual(SafeArrayMarshalling.Array, manifest.Marshalling.SafeArrays);
        Assert.AreEqual(EventStyle.Events, manifest.EventStyle);
        Assert.AreEqual(StructProperties.NonBlittable, manifest.StructProperties);
    }

    [TestMethod]
    public void RecordsAndUpdatesTheEventStyle()
    {
        string directory = TestEnvironment.CreateDirectory("event-style");
        _generator.CreateProject(new NewProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            OutputDirectory = directory,
            EventStyle = EventStyle.Observable,
        });

        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"eventStyle\": \"observable\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "_ShapeEvents.cs")), "IObservable<_ShapeEvents_MovedEventArgs> Moved");

        _generator.UpdateProject(new UpdateProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, ProjectDirectory = directory });
        Assert.AreEqual(EventStyle.Observable, CsComManifest.Read(directory).EventStyle);

        _generator.UpdateProject(new UpdateProjectRequest { TypeLibraryPath = TestEnvironment.TestTypeLibrary, ProjectDirectory = directory, EventStyle = EventStyle.None });
        Assert.AreEqual(EventStyle.None, CsComManifest.Read(directory).EventStyle);
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "Generated", "_ShapeEvents.cs")).Contains("Sink", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UpdateRequiresAManifest()
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => _generator.UpdateProject(new UpdateProjectRequest
        {
            TypeLibraryPath = TestEnvironment.TestTypeLibrary,
            ProjectDirectory = TestEnvironment.CreateDirectory("no-manifest"),
        }));

        StringAssert.Contains(exception.Message, CsComManifest.FileName);
    }
}
