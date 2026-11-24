using Microsoft.Build.Framework;

namespace CsCom.Build;

/// <summary>
/// Exports the type library of the assembly that a project compiled, with the <c>build-export</c> command of the bundled
/// cscom tool, unless the type library that a previous build exported is up to date.
/// </summary>
public sealed class CsComExportTypeLibrary : CsComToolTask
{
    private const string RequestFileName = "cscom.typelib.request.json";
    private const string StateFileName = "cscom.typelib.state";

    /// <summary>Gets or sets the assembly that the project compiled.</summary>
    [Required]
    public string Assembly { get; set; } = string.Empty;

    /// <summary>Gets or sets the type library to write.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the directory of the state and request files: the intermediate output.</summary>
    [Required]
    public string StateDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the name of the type library; the name of the assembly when empty.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the identifier of the type library; the <c>[Guid]</c> of the assembly when empty.</summary>
    public string LibraryGuid { get; set; } = string.Empty;

    /// <summary>Gets or sets the version of the type library, <c>major.minor</c>; that of the assembly when empty.</summary>
    public string LibraryVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the platform of the type library: <c>win32</c> or <c>win64</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Gets or sets the assemblies that the project references.</summary>
    public ITaskItem[] ReferencePaths { get; set; } = [];

    /// <summary>Gets or sets the <c>CsComTypeLibraryReference</c> items: the files of the type libraries of generated code.</summary>
    public ITaskItem[] TypeLibraries { get; set; } = [];

    /// <summary>Gets or sets the <c>CsComReference</c> items, whose type library files are those of the code of the assembly.</summary>
    public ITaskItem[] References { get; set; } = [];

    /// <summary>Gets the type library that was exported.</summary>
    [Output]
    public ITaskItem? TypeLibrary { get; private set; }

    /// <inheritdoc/>
    public override bool Execute()
    {
        string output = Path.GetFullPath(Path.Combine(ProjectDirectory, OutputPath));
        string stateDirectory = Path.GetFullPath(Path.Combine(ProjectDirectory, StateDirectory));
        Directory.CreateDirectory(stateDirectory);

        var json = new JsonWriter();
        json.Property("toolVersion", Version);
        json.Property("projectDirectory", Path.GetFullPath(ProjectDirectory));
        json.Property("assembly", Path.GetFullPath(Path.Combine(ProjectDirectory, Assembly)));
        json.Property("output", output);
        json.Property("stateDirectory", stateDirectory);
        json.Property("name", LibraryName);
        json.Property("guid", LibraryGuid);
        json.Property("version", LibraryVersion);
        json.Property("platform", Platform);
        WriteItems(json, "referencePaths", ReferencePaths);
        WriteItems(json, "typeLibraries", TypeLibraries);
        WriteItems(json, "references", References);

        var files = RunTool(
            "build-export",
            json.ToString(),
            Path.Combine(stateDirectory, RequestFileName),
            Path.Combine(stateDirectory, StateFileName),
            $"CsCom: the type library '{output}' is up to date.");
        if (files is null)
        {
            return false;
        }

        TypeLibrary = CreateItem(output);
        return !Log.HasLoggedErrors;
    }

    private static void WriteItems(JsonWriter json, string name, ITaskItem[] items)
    {
        json.StartArray(name);
        foreach (var item in items)
        {
            json.Value(item.ItemSpec);
        }

        json.EndArray();
    }
}
