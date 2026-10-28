using System.Text;
using CsCom.CodeGeneration;
using CsCom.Output;

namespace CsCom.Tests;

[TestClass]
public sealed class CsComReferenceEditorTests
{
    private const string SdkProject = """
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0-windows</TargetFramework>
          </PropertyGroup>

        </Project>

        """;

    [TestMethod]
    public void AddsItemsAndThePackageToTheProject()
    {
        string project = CreateProject("reference-add", SdkProject);
        string typeLibrary = CopyFile(project, TestEnvironment.TestTypeLibrary, @"lib\CsComTest.tlb");
        var editor = new CsComReferenceEditor();

        // Files are written relative to the project, except those that only share the root with it; LIBIDs are normalized.
        editor.Add(new AddReferencesRequest { Project = project, Sources = [typeLibrary] });
        editor.Add(new AddReferencesRequest
        {
            Project = Path.GetDirectoryName(project),
            Sources = ["Scripting.Dictionary", "00020430-0000-0000-c000-000000000046", TestEnvironment.ScriptingRuntime],
        });

        Assert.AreEqual(
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-windows</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="CsCom.Build" Version="{{ToolInfo.Version}}" />
              </ItemGroup>

              <ItemGroup>
                <CsComReference Include="lib\CsComTest.tlb" />
                <CsComReference Include="Scripting.Dictionary" />
                <CsComReference Include="{00020430-0000-0000-C000-000000000046}" />
                <CsComReference Include="{{TestEnvironment.ScriptingRuntime}}" />
              </ItemGroup>

            </Project>

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(project));
    }

    [TestMethod]
    public void WritesTheSpecifiedOptionsAsMetadata()
    {
        string project = CreateProject("reference-metadata", SdkProject);
        string directory = Path.GetDirectoryName(project)!;
        string typeLibrary = CopyFile(project, TestEnvironment.DrawingTypeLibrary, @"lib\CsComDrawing.tlb");
        File.WriteAllText(Path.Combine(directory, "drawing.cscom.xml"), "<CsCom />");

        new CsComReferenceEditor().Add(new AddReferencesRequest
        {
            Project = project,
            Sources = [typeLibrary],
            Namespace = "Drawing.Interop",
            ConfigurationPath = Path.Combine(directory, "drawing.cscom.xml"),
            ReferencePaths = [Path.Combine(directory, "lib"), Path.Combine(directory, "refs", "Office.dll")],
            EventStyle = EventStyle.Observable,
            StructProperties = StructProperties.Never,
            OverloadsForDefaultValues = true,
            Marshalling = new MarshallingOverrides
            {
                Dates = ValueMarshalling.Wrapper,
                Currencies = ValueMarshalling.None,
                Booleans = ValueMarshalling.Bcl,
                Bstrs = BstrMarshalling.Wrapper,
                SafeArrays = SafeArrayMarshalling.SysArray,
            },
        });

        StringAssert.Contains(
            File.ReadAllText(project),
            """<CsComReference Include="lib\CsComDrawing.tlb" Namespace="Drawing.Interop" Config="drawing.cscom.xml" ReferencePath="lib;refs\Office.dll" """ +
            """EventStyle="observable" StructProperties="never" OverloadsForDefaultValues="true" DateMarshalling="wrapper" CurrencyMarshalling="none" """ +
            """BoolMarshalling="bcl" BstrMarshalling="wrapper" SafeArrayMarshalling="sysarray" />""");
    }

    [TestMethod]
    public void UpdatesTheMetadataOfExistingItems()
    {
        string project = CreateProject("reference-update", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="cscom.build" Version="0.0.1" />
                <CsComReference Include="Scripting.Dictionary" Namespace="Scripting.Interop">
                  <EventStyle>none</EventStyle>
                </CsComReference>
                <CsComReference Include="lib\CsComTest.tlb" />
              </ItemGroup>
            </Project>
            """);
        string typeLibrary = CopyFile(project, TestEnvironment.TestTypeLibrary, @"lib\CsComTest.tlb");
        var editor = new CsComReferenceEditor();

        // The metadata are set where the items have them, and the reference to the package is kept whatever its version.
        editor.Add(new AddReferencesRequest { Project = project, Sources = ["scripting.dictionary"], EventStyle = EventStyle.Interface, StructProperties = StructProperties.Never });
        editor.Add(new AddReferencesRequest { Project = project, Sources = [typeLibrary], Namespace = "Test.Interop" });

        Assert.AreEqual(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="cscom.build" Version="0.0.1" />
                <CsComReference Include="Scripting.Dictionary" Namespace="Scripting.Interop" StructProperties="never">
                  <EventStyle>interface</EventStyle>
                </CsComReference>
                <CsComReference Include="lib\CsComTest.tlb" Namespace="Test.Interop" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"),
            File.ReadAllText(project));

        // Adding the sources again without options does not write the file.
        var written = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(project, written);
        editor.Add(new AddReferencesRequest { Project = project, Sources = ["Scripting.Dictionary", typeLibrary] });
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(project));
    }

    [TestMethod]
    public void LeavesThePackageVersionToCentralPackageManagement()
    {
        string project = CreateProject("reference-cpm", """<Project Sdk="Microsoft.NET.Sdk" />""");
        string packagesProps = Path.Combine(Path.GetDirectoryName(project)!, "Directory.Packages.props");
        File.WriteAllText(packagesProps, "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>");
        var logger = new WarningCollector<CsComReferenceEditor>();
        var editor = new CsComReferenceEditor(new CollectorFactory(logger));

        editor.Add(new AddReferencesRequest { Project = project, Sources = ["Scripting.Dictionary"] });

        Assert.AreEqual(
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="CsCom.Build" />
              </ItemGroup>
              <ItemGroup>
                <CsComReference Include="Scripting.Dictionary" />
              </ItemGroup>
            </Project>
            """.ReplaceLineEndings("\n"),
            File.ReadAllText(project));
        CollectionAssert.AreEqual(
            new[] { $"The project uses central package management ({packagesProps}): add <PackageVersion Include=\"CsCom.Build\" Version=\"{ToolInfo.Version}\" /> to it" },
            logger.Messages);

        // There is no warning when Directory.Packages.props has the version of the package.
        File.WriteAllText(packagesProps, """
            <Project>
              <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>
              <ItemGroup><PackageVersion Include="CsCom.Build" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(project, """<Project Sdk="Microsoft.NET.Sdk" />""");
        logger.Messages.Clear();
        editor.Add(new AddReferencesRequest { Project = project, Sources = ["Scripting.Dictionary"] });
        Assert.AreEqual(0, logger.Messages.Count, string.Join(Environment.NewLine, logger.Messages));
        StringAssert.Contains(File.ReadAllText(project), """<PackageReference Include="CsCom.Build" />""");
    }

    [TestMethod]
    public void KeepsTheFormattingOfTheProjectFile()
    {
        // An old-style project in the MSBuild namespace, with an XML declaration, a byte order mark, CRLF and four spaces.
        const string Original = """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                </PropertyGroup>
                <ItemGroup>
                    <Reference Include="System" />
                </ItemGroup>
                <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>

            """;
        string project = CreateProject("reference-format", string.Empty);
        File.WriteAllText(project, Original.ReplaceLineEndings("\r\n"), new UTF8Encoding(true));
        var editor = new CsComReferenceEditor();

        editor.Add(new AddReferencesRequest { Project = project, Sources = ["Scripting.Dictionary"] });

        string withPackage = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                <PropertyGroup>
                    <OutputType>Exe</OutputType>
                </PropertyGroup>
                <ItemGroup>
                    <Reference Include="System" />
                </ItemGroup>
                <ItemGroup>
                    <PackageReference Include="CsCom.Build" Version="{{ToolInfo.Version}}" />
                </ItemGroup>
                <ItemGroup>
                    <CsComReference Include="Scripting.Dictionary" />
                </ItemGroup>
                <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>

            """.ReplaceLineEndings("\r\n");
        Assert.AreEqual(withPackage, ReadWithBom(project));
        Assert.AreEqual("Scripting.Dictionary", editor.List(project).Single().Source);

        // Removing the item removes its item group, but keeps the reference to the package.
        editor.Remove(project, ["Scripting.Dictionary"]);
        Assert.AreEqual(
            withPackage.Replace("    <ItemGroup>\r\n        <CsComReference Include=\"Scripting.Dictionary\" />\r\n    </ItemGroup>\r\n", string.Empty, StringComparison.Ordinal),
            ReadWithBom(project));
    }

    [TestMethod]
    public void RemovesItemsAndEmptyItemGroups()
    {
        string project = CreateProject("reference-remove", SdkProject);
        string typeLibrary = CopyFile(project, TestEnvironment.TestTypeLibrary, "CsComTest.tlb");
        var editor = new CsComReferenceEditor();
        editor.Add(new AddReferencesRequest { Project = project, Sources = [typeLibrary, "Scripting.Dictionary"] });
        string added = File.ReadAllText(project);

        // A source that the project does not have fails, and nothing is removed.
        var exception = Assert.ThrowsExactly<CsComException>(() => editor.Remove(project, ["Scripting.Dictionary", "Excel.Application"]));
        Assert.AreEqual($"The project '{project}' has no CsComReference 'Excel.Application'.", exception.Message);
        Assert.AreEqual(added, File.ReadAllText(project));

        // A file is given as the project has it, or by its path.
        editor.Remove(project, ["CsComTest.tlb"]);
        StringAssert.Contains(
            File.ReadAllText(project),
            """
              <ItemGroup>
                <CsComReference Include="Scripting.Dictionary" />
              </ItemGroup>
            """.ReplaceLineEndings("\n"));
        editor.Add(new AddReferencesRequest { Project = project, Sources = [typeLibrary] });
        editor.Remove(project, [typeLibrary, "SCRIPTING.DICTIONARY"]);

        Assert.AreEqual(
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-windows</TargetFramework>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="CsCom.Build" Version="{{ToolInfo.Version}}" />
              </ItemGroup>

            </Project>

            """.ReplaceLineEndings("\n"),
            File.ReadAllText(project));
    }

    [TestMethod]
    public void ListsTheItemsWithTheirMetadata()
    {
        // Items that update other items and those of targets are not listed.
        string project = CreateProject("reference-list", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <CsComReference Include="lib\CsComTest.tlb" Namespace="Test.Interop" />
                <CsComReference Include="Excel.Application" Condition="'$(Office)' == 'true'">
                  <Config>excel.cscom.xml</Config>
                </CsComReference>
                <CsComReference Update="Excel.Application" EventStyle="none" />
              </ItemGroup>
              <Target Name="AddMore">
                <ItemGroup>
                  <CsComReference Include="Dynamic.tlb" />
                </ItemGroup>
              </Target>
            </Project>
            """);
        var editor = new CsComReferenceEditor();

        var items = editor.List(project);

        CollectionAssert.AreEqual(new[] { @"lib\CsComTest.tlb", "Excel.Application" }, items.Select(static i => i.Source).ToArray());
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("Namespace", "Test.Interop") }, items[0].Metadata.ToArray());
        CollectionAssert.AreEqual(new[] { KeyValuePair.Create("Config", "excel.cscom.xml") }, items[1].Metadata.ToArray());
        Assert.AreEqual(0, editor.List(CreateProject("reference-list-empty", SdkProject)).Count);
    }

    [TestMethod]
    public void AddsSourcesThatAreNotFoundWithWarnings()
    {
        // E.g. a library that is only registered on the build machines, or a file that the build creates.
        string project = CreateProject("reference-missing", SdkProject);
        string directory = Path.GetDirectoryName(project)!;
        var logger = new WarningCollector<CsComReferenceEditor>();

        new CsComReferenceEditor(new CollectorFactory(logger)).Add(new AddReferencesRequest
        {
            Project = project,
            Sources = ["Contoso.Widget", Path.Combine(directory, "lib", "Widget.tlb")],
            ConfigurationPath = Path.Combine(directory, "widget.cscom.xml"),
        });

        CollectionAssert.AreEqual(
            new[]
            {
                $"The configuration file '{Path.Combine(directory, "widget.cscom.xml")}' does not exist",
                "'Contoso.Widget' is not a type library file, a registered LIBID or a ProgID. The reference is added anyway",
                $"'{Path.Combine(directory, "lib", "Widget.tlb")}' is not a type library file, a registered LIBID or a ProgID. The reference is added anyway",
            },
            logger.Messages);
        string text = File.ReadAllText(project);
        StringAssert.Contains(text, """<CsComReference Include="Contoso.Widget" Config="widget.cscom.xml" />""");
        StringAssert.Contains(text, """<CsComReference Include="lib\Widget.tlb" Config="widget.cscom.xml" />""");
    }

    [TestMethod]
    public void RejectsInvalidRequestsAndProjects()
    {
        string project = CreateProject("reference-invalid", SdkProject);
        var editor = new CsComReferenceEditor();

        var exception = Assert.ThrowsExactly<CsComException>(() => editor.Add(new AddReferencesRequest
        {
            Project = project,
            Sources = ["Scripting.Dictionary", TestEnvironment.StdOle],
            Namespace = "Scripting.Interop",
        }));
        Assert.AreEqual("The namespace 'Scripting.Interop' cannot be used with several sources: add them one by one.", exception.Message);
        exception = Assert.ThrowsExactly<CsComException>(() => editor.Add(new AddReferencesRequest { Project = project, Sources = ["Scripting.Dictionary"], Namespace = "Scripting.class" }));
        Assert.AreEqual("'Scripting.class' is not a valid namespace.", exception.Message);
        Assert.AreEqual(SdkProject.ReplaceLineEndings("\n"), File.ReadAllText(project));

        File.WriteAllText(project, "<Project");
        exception = Assert.ThrowsExactly<CsComException>(() => editor.List(project));
        StringAssert.StartsWith(exception.Message, $"'{project}' is not a valid project file: ");
        File.WriteAllText(project, "<Package />");
        exception = Assert.ThrowsExactly<CsComException>(() => editor.Remove(project, ["Scripting.Dictionary"]));
        Assert.AreEqual($"'{project}' is not an MSBuild project: its root element is not <Project>.", exception.Message);
    }

    [TestMethod]
    public void FindsTheProjectFile()
    {
        string project = CreateProject("reference-find", SdkProject);
        string directory = Path.GetDirectoryName(project)!;

        Assert.AreEqual(project, CsComReferenceEditor.FindProjectFile(project));
        Assert.AreEqual(project, CsComReferenceEditor.FindProjectFile(directory));

        string missing = Path.Combine(directory, "Missing");
        var exception = Assert.ThrowsExactly<CsComException>(() => CsComReferenceEditor.FindProjectFile(missing));
        Assert.AreEqual($"The project '{missing}' does not exist.", exception.Message);
        string empty = Directory.CreateDirectory(Path.Combine(directory, "Empty")).FullName;
        exception = Assert.ThrowsExactly<CsComException>(() => CsComReferenceEditor.FindProjectFile(empty));
        Assert.AreEqual($"There is no project file in '{empty}': specify it with --project.", exception.Message);
        File.WriteAllText(Path.Combine(directory, "Other.csproj"), "<Project />");
        exception = Assert.ThrowsExactly<CsComException>(() => CsComReferenceEditor.FindProjectFile(directory));
        Assert.AreEqual($"There are 2 project files in '{directory}': specify one with --project.", exception.Message);
    }

    // A project file with LF line endings in its own directory, whose Directory.Packages.props disables central package management.
    private static string CreateProject(string name, string text)
    {
        string directory = TestEnvironment.CreateDirectory(name);
        File.WriteAllText(Path.Combine(directory, "Directory.Packages.props"), "<Project />");
        string project = Path.Combine(directory, "App.csproj");
        File.WriteAllText(project, text.ReplaceLineEndings("\n"));
        return project;
    }

    private static string CopyFile(string project, string source, string relativePath)
    {
        string path = Path.Combine(Path.GetDirectoryName(project)!, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(source, path);
        return path;
    }

    // The text of a file that must start with the byte order mark of UTF-8.
    private static string ReadWithBom(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(Encoding.UTF8.Preamble.ToArray(), bytes[..3]);
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }
}
