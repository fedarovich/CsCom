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

        string project = _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory });

        Assert.AreEqual(Path.Combine(directory, "Interop.CsComTest.csproj"), project);
        string text = File.ReadAllText(project);
        StringAssert.Contains(text, "<TargetFramework>net10.0-windows</TargetFramework>");
        StringAssert.Contains(text, "<LangVersion>14</LangVersion>");
        StringAssert.Contains(text, "<RootNamespace>Interop.CsComTest</RootNamespace>");
        StringAssert.Contains(text, $"<PackageReference Include=\"CsCom.Runtime\" Version=\"{ToolInfo.Version}\" />");
        StringAssert.Contains(text, "DisableRuntimeMarshallingAttribute");
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "Interop.CsComTest", "IShape.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "Interop.stdole", "Font.cs")), "The types used from stdole are in its namespace.");
        Assert.IsFalse(File.Exists(Path.Combine(directory, "nuget.config")));

        var manifest = CsComManifest.Read(directory);
        CollectionAssert.AreEqual(new[] { TestEnvironment.TestTypeLibrary }, manifest.Sources.ToArray());
        Assert.IsNull(manifest.Namespaces, "The default namespaces are not recorded.");
        Assert.AreEqual(14, manifest.LanguageVersion);
        Assert.AreEqual(SupportCodeMode.Package, manifest.SupportCode);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"supportCode\": \"package\"");
    }

    [TestMethod]
    public void PackageSourcesCanBeFeedsOrFolders()
    {
        Assert.AreEqual("https://pkgs.example/v3/index.json", ProjectGenerator.ResolvePackageSource("https://pkgs.example/v3/index.json"));
        Assert.AreEqual(Path.GetFullPath("packages"), ProjectGenerator.ResolvePackageSource("packages"));
        Assert.AreEqual(@"C:\packages", ProjectGenerator.ResolvePackageSource(@"C:\packages"));
    }

    [TestMethod]
    public void RecordsTheSourcesOfTheProjectDirectoryByTheirRelativePaths()
    {
        // The project can then be updated from another copy of the directory, e.g. another clone of its repository.
        string parent = TestEnvironment.CreateDirectory("relative-sources");
        string directory = Path.Combine(parent, "Project");
        Directory.CreateDirectory(Path.Combine(directory, "libs"));
        string library = Path.Combine(directory, "libs", "CsComTest.tlb");
        File.Copy(TestEnvironment.TestTypeLibrary, library);

        _generator.CreateProject(new CreateProjectRequest { Sources = [library, "Scripting.Dictionary"], OutputDirectory = directory, Name = "Interop.Mixed" });

        CollectionAssert.AreEqual(new[] { @"libs\CsComTest.tlb", "Scripting.Dictionary" }, CsComManifest.Read(directory).Sources.ToArray());

        string moved = Path.Combine(parent, "Moved");
        Directory.Move(directory, moved);
        File.Delete(Path.Combine(moved, "Generated", "Interop.CsComTest", "IShape.cs"));

        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = moved });

        Assert.IsTrue(File.Exists(Path.Combine(moved, "Generated", "Interop.CsComTest", "IShape.cs")));
        CollectionAssert.AreEqual(new[] { @"libs\CsComTest.tlb", "Scripting.Dictionary" }, CsComManifest.Read(moved).Sources.ToArray());
    }

    [TestMethod]
    public void DoesNotReplaceAnExistingNuGetConfig()
    {
        // The package source is written into a nuget.config, e.g. into that of a repository in whose root the project is created.
        string directory = TestEnvironment.CreateDirectory("existing-nuget-config");
        string nugetConfig = Path.Combine(directory, "nuget.config");
        File.WriteAllText(nugetConfig, "<configuration />");

        var exception = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            PackageSource = TestEnvironment.LocalPackages,
        }));

        StringAssert.StartsWith(exception.Message, $"'{nugetConfig}' already exists");
        Assert.AreEqual("<configuration />", File.ReadAllText(nugetConfig));
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "Generated")));

        // Without a package source, the project is created and the file is left alone.
        _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory });
        Assert.AreEqual("<configuration />", File.ReadAllText(nugetConfig));
    }

    [TestMethod]
    public void EmptyValuesAreIgnoredOrRejected()
    {
        // An empty configuration file removes the one of a library on update; a new project has none.
        string directory = TestEnvironment.CreateDirectory("empty-values");
        _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            ConfigurationPaths = new Dictionary<string, string> { ["CsComTest"] = string.Empty },
        });
        Assert.IsNull(CsComManifest.Read(directory).Configurations);

        var exception = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = string.Empty }));
        Assert.AreEqual("The output directory is empty.", exception.Message);

        exception = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = TestEnvironment.CreateDirectory("empty-reference-path"),
            ReferencePaths = [string.Empty],
        }));
        Assert.AreEqual("A reference path is empty.", exception.Message);
    }

    [TestMethod]
    public void UpdateKeepsTheLanguageVersionAndTheFormattingOfTheProjectFile()
    {
        // The owner of the project changed its LangVersion and formats the tags over several lines, with single quotes.
        string directory = TestEnvironment.CreateDirectory("update-project-file");
        string project = _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory });
        string text = File.ReadAllText(project)
            .Replace("<LangVersion>14</LangVersion>", "<LangVersion>preview</LangVersion>", StringComparison.Ordinal)
            .Replace(
                $"<PackageReference Include=\"CsCom.Runtime\" Version=\"{ToolInfo.Version}\" />",
                "<PackageReference Include='CsCom.Runtime'\r\n                      Version=\"0.0.1\"/>\r\n    <Content Include='a.txt'\r\n             CopyToOutputDirectory=\"Always\"/>",
                StringComparison.Ordinal);
        File.WriteAllText(project, text);

        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory });

        // Only the tag with the changed version is written again.
        string expected = text.Replace(
            "<PackageReference Include='CsCom.Runtime'\r\n                      Version=\"0.0.1\"/>",
            $"<PackageReference Include=\"CsCom.Runtime\" Version=\"{ToolInfo.Version}\" />",
            StringComparison.Ordinal);
        Assert.AreEqual(expected, File.ReadAllText(project));

        // The version is set when the update asks for one.
        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory, LanguageVersion = 15 });
        Assert.AreEqual(expected.Replace("<LangVersion>preview</LangVersion>", "<LangVersion>15</LangVersion>", StringComparison.Ordinal), File.ReadAllText(project));
    }

    [TestMethod]
    public void UpdateKeepsTheEncodingOfTheProjectFile()
    {
        string directory = TestEnvironment.CreateDirectory("update-encoding");
        string project = _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory });
        var latin1 = System.Text.Encoding.Latin1;
        string text = "<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\r\n" + File.ReadAllText(project)
            .Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable>\r\n    <Authors>Müller</Authors>", StringComparison.Ordinal)
            .Replace($"Version=\"{ToolInfo.Version}\"", "Version=\"0.0.1\"", StringComparison.Ordinal);
        File.WriteAllText(project, text, latin1);

        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory });

        Assert.AreEqual(text.Replace("Version=\"0.0.1\"", $"Version=\"{ToolInfo.Version}\"", StringComparison.Ordinal), File.ReadAllText(project, latin1));
    }

    [TestMethod]
    public void CreatesProjectWithOptions()
    {
        string directory = TestEnvironment.CreateDirectory("options");

        string project = _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
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

        string project = _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = Path.Combine(root, "src", "Interop") });

        StringAssert.Contains(File.ReadAllText(project), "<PackageReference Include=\"CsCom.Runtime\" />");
    }

    [TestMethod]
    public void RefusesToOverwriteProjects()
    {
        string directory = TestEnvironment.CreateDirectory("overwrite");
        _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory });

        var exception = Assert.ThrowsExactly<CsComException>(() =>
            _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory }));
        StringAssert.Contains(exception.Message, "cscom project update");
    }

    [TestMethod]
    [DataRow("net8.0-windows", null, "not supported")]
    [DataRow("net10.0-windows", 13, "C# 14 or later is required")]
    [DataRow("net11.0-windows", 14, "C# 15 or later is required")]
    public void ValidatesTargetFrameworksAndLanguageVersions(string targetFramework, int? languageVersion, string message)
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
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
        Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = TestEnvironment.CreateDirectory("namespace"),
            Namespace = @namespace,
        }));
    }

    [TestMethod]
    public void UpdatesGeneratedFilesAndKeepsUserFiles()
    {
        string directory = TestEnvironment.CreateDirectory("update");
        string project = _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory });
        string userFile = Path.Combine(directory, "Extensions.cs");
        File.WriteAllText(userFile, "// user code");
        File.WriteAllText(Path.Combine(directory, "Generated", "Stale.cs"), "// stale");

        // The files that do not change are not written again, so that they keep their timestamps.
        string shape = Path.Combine(directory, "Generated", "Interop.CsComTest", "IShape.cs");
        var written = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(shape, written);
        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory });
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(shape));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "Generated", "Stale.cs")));
        File.WriteAllText(Path.Combine(directory, "Generated", "Stale.cs"), "// stale");

        _generator.UpdateProject(new UpdateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
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
        Assert.AreEqual("Renamed", manifest.Namespaces!["CsComTest"]);
        Assert.AreEqual(15, manifest.LanguageVersion);
        Assert.IsTrue(manifest.SingleFile);
    }

    [TestMethod]
    public void UpdateKeepsTheRecordedOptions()
    {
        string directory = TestEnvironment.CreateDirectory("update-defaults");
        _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            Namespace = "Custom.Namespace",
            SingleFile = true,
        });

        _generator.UpdateProject(new UpdateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], ProjectDirectory = directory });

        var manifest = CsComManifest.Read(directory);
        Assert.AreEqual("Custom.Namespace", manifest.Namespaces!["CsComTest"]);
        Assert.IsTrue(manifest.SingleFile);
        Assert.AreEqual(1, Directory.GetFiles(Path.Combine(directory, "Generated")).Length);
    }

    [TestMethod]
    public void RecordsAndUpdatesMarshallingOptions()
    {
        string directory = TestEnvironment.CreateDirectory("marshalling");
        _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            Marshalling = new MarshallingOptions { Dates = ValueMarshalling.Wrapper, Booleans = ValueMarshalling.Wrapper },
        });

        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"dates\": \"wrapper\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Interop.CsComTest", "Record.cs")), "global::CsCom.Runtime.ComBool Flag;");

        _generator.UpdateProject(new UpdateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            ProjectDirectory = directory,
            Marshalling = new MarshallingOverrides { Dates = ValueMarshalling.None, Currencies = ValueMarshalling.Wrapper },
        });

        var marshalling = CsComManifest.Read(directory).Marshalling;
        Assert.AreEqual(
            new MarshallingOptions { Dates = ValueMarshalling.None, Currencies = ValueMarshalling.Wrapper, Booleans = ValueMarshalling.Wrapper },
            marshalling);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Interop.CsComTest", "IMarshalling.cs")), "double Dates(double Date, out double Next);");
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
        CollectionAssert.AreEqual(new[] { @"C:\Windows\System32\scrrun.dll" }, manifest.GetSources().ToArray());
        Assert.AreEqual("Interop.Scripting", manifest.Namespace);
        Assert.IsNull(manifest.Namespaces);
        Assert.AreEqual(new MarshallingOptions(), manifest.Marshalling);
        Assert.AreEqual(BstrMarshalling.String, manifest.Marshalling.Bstrs);
        Assert.AreEqual(SafeArrayMarshalling.Array, manifest.Marshalling.SafeArrays);
        Assert.AreEqual(EventStyle.Events, manifest.EventStyle);
        Assert.AreEqual(StructProperties.Auto, manifest.StructProperties);
        Assert.IsNull(manifest.Configuration);
        Assert.IsNull(manifest.Configurations);
        Assert.IsFalse(manifest.OverloadsForDefaultValues);
    }

    [TestMethod]
    public void AppliesAndRecordsTheConfiguration()
    {
        var pipeline = new InteropPipeline();
        TestTransformers.Register(pipeline.Registry);
        var generator = new ProjectGenerator(pipeline);
        string directory = TestEnvironment.CreateDirectory("configuration");
        File.WriteAllText(Path.Combine(directory, "cscom.xml"), """
            <CsCom>
              <ModelTransformers>
                <RenameType Type="^IShape$" NewName="IFigure" />
              </ModelTransformers>
              <SyntaxTransformers>
                <RemoveFile Name="Point.cs" />
              </SyntaxTransformers>
            </CsCom>
            """);
        string generated = Path.Combine(directory, "Generated", "Interop.CsComTest");

        generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            ConfigurationPath = Path.Combine(directory, "cscom.xml"),
        });
        Assert.AreEqual("cscom.xml", CsComManifest.Read(directory).Configurations!["CsComTest"]);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"CsComTest\": \"cscom.xml\"");
        Assert.IsTrue(File.Exists(Path.Combine(generated, "IFigure.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(generated, "IShape.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(generated, "Point.cs")));

        generator.UpdateProject(new UpdateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], ProjectDirectory = directory });
        Assert.AreEqual("cscom.xml", CsComManifest.Read(directory).Configurations!["CsComTest"]);
        Assert.IsTrue(File.Exists(Path.Combine(generated, "IFigure.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(generated, "Point.cs")));

        generator.UpdateProject(new UpdateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], ProjectDirectory = directory, ConfigurationPath = "" });
        Assert.IsNull(CsComManifest.Read(directory).Configurations);
        Assert.IsTrue(File.Exists(Path.Combine(generated, "IShape.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(generated, "Point.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(generated, "IFigure.cs")));
    }

    [TestMethod]
    public void RejectsInvalidConfigurationsBeforeWriting()
    {
        string directory = TestEnvironment.CreateDirectory("invalid-configuration");
        string configuration = Path.Combine(directory, "cscom.xml");
        File.WriteAllText(configuration, "<CsCom><ModelTransformers><Rename /></ModelTransformers></CsCom>");

        var exception = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            ConfigurationPath = configuration,
        }));

        StringAssert.StartsWith(exception.Message, $"{configuration}(1,28): Unknown model transformer <Rename>.");
        Assert.IsFalse(File.Exists(Path.Combine(directory, CsComManifest.FileName)));
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "Generated")));
    }

    [TestMethod]
    public void RecordsAndUpdatesTheEventStyle()
    {
        string directory = TestEnvironment.CreateDirectory("event-style");
        _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = directory,
            EventStyle = EventStyle.Observable,
        });

        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, CsComManifest.FileName)), "\"eventStyle\": \"observable\"");
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Interop.CsComTest", "_ShapeEvents.cs")), "IObservable<_ShapeEvents_MovedEventArgs> Moved");

        _generator.UpdateProject(new UpdateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], ProjectDirectory = directory });
        Assert.AreEqual(EventStyle.Observable, CsComManifest.Read(directory).EventStyle);

        _generator.UpdateProject(new UpdateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], ProjectDirectory = directory, EventStyle = EventStyle.None });
        Assert.AreEqual(EventStyle.None, CsComManifest.Read(directory).EventStyle);
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "Generated", "Interop.CsComTest", "_ShapeEvents.cs")).Contains("Sink", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UpdatesFromTheRecordedTypeLibrary()
    {
        string directory = TestEnvironment.CreateDirectory("recorded-tlb");
        _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory });
        File.Delete(Path.Combine(directory, "Generated", "Interop.CsComTest", "IShape.cs"));

        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory });

        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "Interop.CsComTest", "IShape.cs")));
        CollectionAssert.AreEqual(new[] { TestEnvironment.TestTypeLibrary }, CsComManifest.Read(directory).Sources.ToArray());
    }

    [TestMethod]
    public void RecordsRegisteredTypeLibraries()
    {
        string directory = TestEnvironment.CreateDirectory("progid");
        _generator.CreateProject(new CreateProjectRequest { Sources = ["Scripting.Dictionary"], OutputDirectory = directory });

        CollectionAssert.AreEqual(new[] { "Scripting.Dictionary" }, CsComManifest.Read(directory).Sources.ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(directory, "Interop.Scripting.csproj")));
        File.Delete(Path.Combine(directory, "Generated", "Interop.Scripting", "IDictionary.cs"));

        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory });

        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "Interop.Scripting", "IDictionary.cs")));
        CollectionAssert.AreEqual(new[] { "Scripting.Dictionary" }, CsComManifest.Read(directory).Sources.ToArray());
    }

    [TestMethod]
    public void CreatesAndUpdatesProjectsOfSeveralSources()
    {
        string directory = TestEnvironment.CreateDirectory("several");
        string configuration = Path.Combine(directory, "cscom.xml");
        File.WriteAllText(configuration, """<CsCom><ModelTransformers><RenameType TypeName="IShape" NewName="IFigure" /></ModelTransformers></CsCom>""");
        string generated = Path.Combine(directory, "Generated");

        string project = _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary],
            OutputDirectory = directory,
            Name = "Set.Interop",
            Namespaces = new Dictionary<string, string> { ["CsComDrawing"] = "Drawing.Interop", ["STDOLE"] = "Ole.Interop" },
            ConfigurationPaths = new Dictionary<string, string> { ["CsComTest"] = configuration },
        });

        Assert.AreEqual(Path.Combine(directory, "Set.Interop.csproj"), project);
        StringAssert.Contains(File.ReadAllText(project), "<RootNamespace>Interop.CsComTest</RootNamespace>");
        CollectionAssert.AreEquivalent(new[] { "Interop.CsComTest", "Drawing.Interop", "Ole.Interop" }, Directory.GetDirectories(generated).Select(Path.GetFileName).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(generated, "Interop.CsComTest", "IFigure.cs")));
        StringAssert.Contains(File.ReadAllText(Path.Combine(generated, "Drawing.Interop", "ICanvas.cs")), "global::Ole.Interop.Font? get_Font()");
        var manifest = CsComManifest.Read(directory);
        CollectionAssert.AreEqual(new[] { TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary }, manifest.Sources.ToArray());
        Assert.AreEqual("Ole.Interop", manifest.Namespaces!["stdole"]);
        Assert.AreEqual("cscom.xml", manifest.Configurations!["CsComTest"]);
        Assert.IsNull(manifest.TypeLibrary);
        Assert.IsNull(manifest.Namespace);

        // The changes are merged into the recorded namespaces, and an empty namespace removes one.
        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory, Namespaces = new Dictionary<string, string> { ["stdole"] = string.Empty } });
        CollectionAssert.AreEquivalent(new[] { "Interop.CsComTest", "Drawing.Interop", "Interop.stdole" }, Directory.GetDirectories(generated).Select(Path.GetFileName).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(generated, "Interop.CsComTest", "IFigure.cs")));

        // Other sources replace the recorded ones.
        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory, Sources = [TestEnvironment.DrawingTypeLibrary] });
        CollectionAssert.AreEquivalent(new[] { "Drawing.Interop", "Interop.stdole" }, Directory.GetDirectories(generated).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEqual(new[] { TestEnvironment.DrawingTypeLibrary }, CsComManifest.Read(directory).Sources.ToArray());
    }

    [TestMethod]
    public void RequiresTheNamesOfSeveralSources()
    {
        string directory = TestEnvironment.CreateDirectory("several-invalid");

        var name = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary],
            OutputDirectory = directory,
        }));
        var @namespace = Assert.ThrowsExactly<CsComException>(() => _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary, TestEnvironment.DrawingTypeLibrary],
            OutputDirectory = directory,
            Name = "Set.Interop",
            Namespace = "Set.Interop",
        }));

        Assert.AreEqual("The name of the project or assembly must be specified when there are several sources.", name.Message);
        StringAssert.StartsWith(@namespace.Message, "The namespace 'Set.Interop' cannot be used with several sources");
        Assert.IsFalse(File.Exists(Path.Combine(directory, CsComManifest.FileName)));
    }

    [TestMethod]
    public void UpdatesTheProjectsOfEarlierVersions()
    {
        string directory = TestEnvironment.CreateDirectory("earlier-version");
        File.WriteAllText(Path.Combine(directory, "cscom.xml"), """<CsCom><ModelTransformers><RenameType TypeName="IShape" NewName="IFigure" /></ModelTransformers></CsCom>""");
        File.WriteAllText(Path.Combine(directory, CsComManifest.FileName), $$"""
            {
              "toolVersion": "0.1.0",
              "typeLibrary": "{{TestEnvironment.TestTypeLibrary.Replace(@"\", @"\\", StringComparison.Ordinal)}}",
              "name": "Legacy.Interop",
              "namespace": "Legacy.Interop",
              "targetFrameworks": [ "net10.0-windows" ],
              "languageVersion": 14,
              "supportCode": "package",
              "configuration": "cscom.xml"
            }
            """);

        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory });

        Assert.IsTrue(File.Exists(Path.Combine(directory, "Generated", "Legacy.Interop", "IFigure.cs")));
        var manifest = CsComManifest.Read(directory);
        CollectionAssert.AreEqual(new[] { TestEnvironment.TestTypeLibrary }, manifest.Sources.ToArray());
        Assert.AreEqual("Legacy.Interop", manifest.Namespaces!["CsComTest"]);
        Assert.AreEqual("cscom.xml", manifest.Configurations!["CsComTest"]);
        string json = File.ReadAllText(Path.Combine(directory, CsComManifest.FileName));
        Assert.IsFalse(json.Contains("\"typeLibrary\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"namespace\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"configuration\"", StringComparison.Ordinal));

        // Earlier versions always generated the late binding with dynamic, which the update keeps and records.
        Assert.IsTrue(manifest.Dynamic);
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "Generated", "Legacy.Interop", "Shape.cs")), "CreateDynamic");
    }

    [TestMethod]
    public void AddsAndRemovesTheDynamicPackage()
    {
        string directory = TestEnvironment.CreateDirectory("dynamic-package");
        string project = _generator.CreateProject(new CreateProjectRequest { Sources = [TestEnvironment.TestTypeLibrary], OutputDirectory = directory, Dynamic = true });
        string shape = Path.Combine(directory, "Generated", "Interop.CsComTest", "Shape.cs");

        string text = File.ReadAllText(project);
        StringAssert.Contains(text, $"    <PackageReference Include=\"CsCom.Runtime\" Version=\"{ToolInfo.Version}\" />\r\n    <PackageReference Include=\"CsCom.Runtime.Dynamic\" Version=\"{ToolInfo.Version}\" />".ReplaceLineEndings());
        Assert.IsTrue(CsComManifest.Read(directory).Dynamic);
        StringAssert.Contains(File.ReadAllText(shape), "CreateDynamic");

        // The recorded value is kept unless it is changed; turning it off removes the package reference.
        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory });
        Assert.AreEqual(text, File.ReadAllText(project));
        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory, Dynamic = false });
        Assert.IsFalse(File.ReadAllText(project).Contains("CsCom.Runtime.Dynamic", StringComparison.Ordinal));
        Assert.IsFalse(File.ReadAllText(shape).Contains("CreateDynamic", StringComparison.Ordinal));
        Assert.IsFalse(CsComManifest.Read(directory).Dynamic);

        _generator.UpdateProject(new UpdateProjectRequest { ProjectDirectory = directory, Dynamic = true });
        Assert.AreEqual(text, File.ReadAllText(project));

        // A project with the support code emitted into it gets no package reference, but the code of CsCom.Runtime.Dynamic.
        string inline = TestEnvironment.CreateDirectory("dynamic-inline");
        string inlineProject = _generator.CreateProject(new CreateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = inline,
            SupportCode = SupportCodeMode.Internal,
            Dynamic = true,
        });
        Assert.IsFalse(File.ReadAllText(inlineProject).Contains("PackageReference", StringComparison.Ordinal));
        Assert.IsTrue(File.Exists(Path.Combine(inline, "Generated", "Interop.CsComTest.ComSupport", "DynamicDispatchObject.cs")));
    }

    [TestMethod]
    public void UpdateRequiresAManifest()
    {
        var exception = Assert.ThrowsExactly<CsComException>(() => _generator.UpdateProject(new UpdateProjectRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            ProjectDirectory = TestEnvironment.CreateDirectory("no-manifest"),
        }));

        StringAssert.Contains(exception.Message, CsComManifest.FileName);
    }

    [TestMethod]
    public void AssemblyGeneratorValidatesTheRequestBeforeBuilding()
    {
        var generator = new AssemblyGenerator(new InteropPipeline());
        string output = TestEnvironment.CreateDirectory("pack-invalid");

        var runtimes = Assert.ThrowsExactly<CsComException>(() => generator.PackAssembly(new PackAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            RuntimeIdentifiers = ["win-x64"],
        }));
        var version = Assert.ThrowsExactly<CsComException>(() => generator.PackAssembly(new PackAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            PackageVersion = "1.x",
        }));
        var property = Assert.ThrowsExactly<CsComException>(() => generator.PackAssembly(new PackAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            Properties = new Dictionary<string, string> { ["Package Tags"] = "x" },
        }));
        var buildProperty = Assert.ThrowsExactly<CsComException>(() => generator.BuildAssembly(new BuildAssemblyRequest
        {
            Sources = [TestEnvironment.TestTypeLibrary],
            OutputDirectory = output,
            Properties = new Dictionary<string, string> { ["1x"] = "y" },
        }));

        Assert.AreEqual("The packages are RID-neutral; runtime identifiers are not supported.", runtimes.Message);
        Assert.AreEqual("'1.x' is not a valid package version, e.g. 1.2.3 or 1.2.3-preview.1 is expected.", version.Message);
        Assert.AreEqual("'Package Tags' is not a valid MSBuild property name.", property.Message);
        Assert.AreEqual("'1x' is not a valid MSBuild property name.", buildProperty.Message);
        Assert.IsEmpty(Directory.GetFileSystemEntries(output));
    }
}
