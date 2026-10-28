using System.Xml;
using System.Xml.Linq;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.Output;

/// <summary>
/// A <c>CsComReference</c> item of a project file.
/// </summary>
/// <param name="Source">The <c>Include</c> of the item: a file relative to the project, a LIBID or a ProgID.</param>
/// <param name="Metadata">The metadata of the item, from its attributes and then its child elements.</param>
public sealed record CsComReferenceItem(string Source, IReadOnlyList<KeyValuePair<string, string>> Metadata);

/// <summary>
/// Adds, lists and removes the <c>CsComReference</c> items of a project file, whose interop code the CsCom.Build package
/// generates during the build. Only the edited elements change, so the formatting of the file is kept.
/// </summary>
/// <remarks>
/// The items are those written in the file, outside targets: the project is not evaluated, so the items of imported files
/// are not included and the conditions are ignored.
/// </remarks>
public sealed partial class CsComReferenceEditor
{
    /// <summary>The name of the items.</summary>
    public const string ItemName = "CsComReference";

    /// <summary>The ID of the package that generates the code of the items.</summary>
    public const string BuildPackageId = "CsCom.Build";

    // The attributes of items that are not metadata.
    private static readonly HashSet<string> ItemAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Include", "Exclude", "Remove", "Update", "Condition", "Label", "KeepMetadata", "RemoveMetadata", "KeepDuplicates",
        "MatchOnMetadata", "MatchOnMetadataOptions",
    };

    private static readonly string[] FileExtensions = [".tlb", ".olb", ".dll", ".exe", ".ocx"];

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CsComReferenceEditor"/> class.
    /// </summary>
    public CsComReferenceEditor(ILoggerFactory? loggerFactory = null) =>
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<CsComReferenceEditor>();

    /// <summary>
    /// Finds a project file.
    /// </summary>
    /// <param name="path">A project file, or a directory that contains a single project file; the current directory by default.</param>
    /// <returns>The full path of the project file.</returns>
    /// <exception cref="CsComException">The project is not found, or the directory contains several projects.</exception>
    public static string FindProjectFile(string? path)
    {
        string fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? Environment.CurrentDirectory : path);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            throw new CsComException($"The project '{fullPath}' does not exist.");
        }

        string[] projects = Directory.GetFiles(fullPath, "*.csproj");
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new CsComException($"There is no project file in '{fullPath}': specify it with --project."),
            _ => throw new CsComException($"There are {projects.Length} project files in '{fullPath}': specify one with --project."),
        };
    }

    /// <summary>
    /// Gets the <c>CsComReference</c> items of a project file.
    /// </summary>
    /// <param name="project">A project file, or a directory that contains a single project file; the current directory by default.</param>
    public IReadOnlyList<CsComReferenceItem> List(string? project)
    {
        string path = FindProjectFile(project);
        var items = GetItems(ProjectFile.Load(path)).Select(static item => new CsComReferenceItem(GetInclude(item), GetMetadata(item))).ToList();
        if (items.Count == 0)
        {
            LogNoItems(path);
        }

        return items;
    }

    /// <summary>
    /// Adds <c>CsComReference</c> items to a project file, or updates the metadata of the existing items of the sources, and
    /// adds a reference to the CsCom.Build package if the project file has none.
    /// </summary>
    /// <returns>The full path of the project file.</returns>
    /// <exception cref="CsComException">The request or the project file is not valid.</exception>
    public string Add(AddReferencesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Sources.Count == 0)
        {
            throw new CsComException("No source is specified.");
        }

        if (request.Namespace is not null)
        {
            if (request.Sources.Count > 1)
            {
                throw new CsComException($"The namespace '{request.Namespace}' cannot be used with several sources: add them one by one.");
            }

            ProjectGenerator.ValidateNamespace(request.Namespace.Trim());
        }

        string path = FindProjectFile(request.Project);
        string directory = Path.GetDirectoryName(path)!;
        var metadata = GetMetadata(request, directory);
        var includes = request.Sources.Select(source => ToInclude(source, directory)).ToList();
        var document = ProjectFile.Load(path);
        var project = document.Root!;
        bool changed = AddPackageReference(project, path);
        foreach (string include in includes)
        {
            string key = GetKey(include, directory);
            if (GetItems(document).LastOrDefault(item => string.Equals(GetKey(GetInclude(item), directory), key, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                bool updated = false;
                foreach (var (name, value) in metadata)
                {
                    updated |= SetMetadata(existing, name, value);
                }

                if (updated)
                {
                    LogUpdated(GetInclude(existing), path);
                }
                else
                {
                    LogUnchanged(GetInclude(existing), path);
                }

                changed |= updated;
            }
            else
            {
                AddItem(project, new XElement(project.Name.Namespace + ItemName, new XAttribute("Include", include), metadata.Select(static m => new XAttribute(m.Key, m.Value))));
                LogAdded(include, path);
                changed = true;
            }
        }

        if (changed)
        {
            ProjectFile.Save(document, path);
        }

        return path;
    }

    /// <summary>
    /// Removes the <c>CsComReference</c> items of sources from a project file. The reference to the CsCom.Build package is kept.
    /// </summary>
    /// <param name="project">A project file, or a directory that contains a single project file; the current directory by default.</param>
    /// <param name="sources">The sources, as written in the project file, or files relative to the current directory.</param>
    /// <returns>The full path of the project file.</returns>
    /// <exception cref="CsComException">The project file has no item of a source; then it is not changed.</exception>
    public string Remove(string? project, IReadOnlyList<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        string path = FindProjectFile(project);
        string directory = Path.GetDirectoryName(path)!;
        var document = ProjectFile.Load(path);
        var items = GetItems(document).ToList();
        var removed = new List<XElement>();
        foreach (string source in sources)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new CsComException("A source is empty.");
            }

            string[] keys = [GetKey(source, directory), GetKey(source, Environment.CurrentDirectory)];
            var matching = items.Where(item => keys.Contains(GetKey(GetInclude(item), directory), StringComparer.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 0)
            {
                throw new CsComException($"The project '{path}' has no CsComReference '{source}'.");
            }

            removed.AddRange(matching.Except(removed));
        }

        foreach (var item in removed)
        {
            var group = item.Parent!;
            RemoveWithWhitespace(item);
            if (group.Nodes().All(IsWhitespace))
            {
                RemoveWithWhitespace(group);
            }

            LogRemoved(GetInclude(item), path);
        }

        if (removed.Count > 0)
        {
            ProjectFile.Save(document, path);
        }

        return path;
    }

    // The items of the project file outside targets that include a source.
    private static IEnumerable<XElement> GetItems(XDocument document) =>
        document.Root!.Descendants().Where(static e => e.Name.LocalName == ItemName
            && e.Parent?.Name.LocalName == "ItemGroup"
            && e.Attribute("Include") is not null
            && !IsInTarget(e));

    private static string GetInclude(XElement item) => item.Attribute("Include")!.Value.Trim();

    private static List<KeyValuePair<string, string>> GetMetadata(XElement item) =>
    [
        .. item.Attributes()
            .Where(static a => !a.IsNamespaceDeclaration && !ItemAttributes.Contains(a.Name.LocalName))
            .Select(static a => KeyValuePair.Create(a.Name.LocalName, a.Value)),
        .. item.Elements().Select(static e => KeyValuePair.Create(e.Name.LocalName, e.Value.Trim())),
    ];

    // The metadata of the specified options, in the order of the CsCom.Build documentation. The paths are relative to the project.
    private List<KeyValuePair<string, string>> GetMetadata(AddReferencesRequest request, string directory)
    {
        string? configuration = null;
        if (request.ConfigurationPath is not null)
        {
            if (string.IsNullOrWhiteSpace(request.ConfigurationPath))
            {
                throw new CsComException("The path of the configuration file is empty.");
            }

            string fullPath = Path.GetFullPath(request.ConfigurationPath.Trim());
            if (!File.Exists(fullPath))
            {
                LogConfigurationNotFound(fullPath);
            }

            configuration = ToProjectPath(fullPath, directory);
        }

        var referencePaths = request.ReferencePaths.Where(static p => !string.IsNullOrWhiteSpace(p)).Select(p => ToProjectPath(Path.GetFullPath(p.Trim()), directory)).ToList();
        (string Name, string? Value)[] values =
        [
            ("Namespace", request.Namespace?.Trim()),
            ("Config", configuration),
            ("ReferencePath", referencePaths.Count > 0 ? string.Join(';', referencePaths) : null),
            ("EventStyle", Format(request.EventStyle)),
            ("StructProperties", Format(request.StructProperties)),
            ("OverloadsForDefaultValues", request.OverloadsForDefaultValues is { } overloads ? (overloads ? "true" : "false") : null),
            ("DateMarshalling", Format(request.Marshalling.Dates)),
            ("CurrencyMarshalling", Format(request.Marshalling.Currencies)),
            ("BoolMarshalling", Format(request.Marshalling.Booleans)),
            ("BstrMarshalling", Format(request.Marshalling.Bstrs)),
            ("SafeArrayMarshalling", Format(request.Marshalling.SafeArrays)),
        ];
        return [.. values.Where(static v => v.Value is not null).Select(static v => KeyValuePair.Create(v.Name, v.Value!))];
    }

    // The values are the lower-case names of the enum members, as in the CsCom* properties.
    private static string? Format<T>(T? value)
        where T : struct, Enum => value?.ToString().ToLowerInvariant();

    // The Include of the item of a source: a file relative to the project, a LIBID in its normalized form, or the ProgID. A
    // source that is not found on this machine is added anyway, e.g. a library that is only registered on the build machines.
    private string ToInclude(string source, string directory)
    {
        source = source.Trim();
        if (source.Length == 0)
        {
            throw new CsComException("A source is empty.");
        }

        if (source.Contains("$(", StringComparison.Ordinal))
        {
            return source;
        }

        bool isFile = TypeLibrarySource.IsFile(source) || IsPath(source);
        string include = isFile ? ToProjectPath(Path.GetFullPath(source), directory) : TypeLibrarySource.Normalize(source);
        try
        {
            TypeLibrarySource.Resolve(source);
        }
        catch (CsComException e)
        {
            LogSourceNotFound(e.Message);
        }

        return include;
    }

    // A path relative to the project, e.g. of a file in the same repository, unless the file shares only the root with the
    // project, e.g. a system file, which keeps its full path.
    private static string ToProjectPath(string fullPath, string directory)
    {
        string relative = Path.GetRelativePath(directory, fullPath);
        int depth = directory[Path.GetPathRoot(directory)!.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;
        int parents = relative.Split(['\\', '/']).TakeWhile(static part => part == "..").Count();
        return Path.IsPathRooted(relative) || parents >= depth ? fullPath : relative;
    }

    // The form in which sources are compared: the full path of a file, a normalized LIBID, or the text of anything else, such
    // as a ProgID or an Include with properties or wildcards.
    private static string GetKey(string include, string directory)
    {
        include = include.Trim();
        if (include.Length == 0 || include.Contains("$(", StringComparison.Ordinal) || include.Contains("@(", StringComparison.Ordinal)
            || include.Contains("%(", StringComparison.Ordinal) || include.IndexOfAny(['*', '?', ';']) >= 0)
        {
            return include;
        }

        string path = Path.Combine(directory, include);
        if (TypeLibrarySource.IsFile(path) || IsPath(include))
        {
            return Path.GetFullPath(path);
        }

        try
        {
            return TypeLibrarySource.Normalize(include);
        }
        catch (CsComException)
        {
            return include;
        }
    }

    // A file that does not exist, e.g. one that the build creates, is recognized by a directory separator or its extension.
    private static bool IsPath(string source) =>
        source.IndexOfAny(['\\', '/']) >= 0
        || FileExtensions.Contains(Path.GetExtension(TypeLibrarySource.GetFilePath(source)), StringComparer.OrdinalIgnoreCase);

    // Adds a reference to CsCom.Build unless the project file has one. With central package management, the version belongs
    // to Directory.Packages.props.
    private bool AddPackageReference(XElement project, string path)
    {
        if (project.Descendants().Any(static e => e.Name.LocalName == "PackageReference"
            && string.Equals(e.Attribute("Include")?.Value.Trim(), BuildPackageId, StringComparison.OrdinalIgnoreCase)
            && !IsInTarget(e)))
        {
            return false;
        }

        var reference = new XElement(project.Name.Namespace + "PackageReference", new XAttribute("Include", BuildPackageId));
        if (ProjectFile.FindCentralPackageManagement(Path.GetDirectoryName(path)!) is { } packagesProps)
        {
            if (!DeclaresPackageVersion(packagesProps))
            {
                LogCentralPackageManagement(packagesProps, BuildPackageId, ToolInfo.Version);
            }
        }
        else
        {
            reference.Add(new XAttribute("Version", ToolInfo.Version));
        }

        AddItem(project, reference);
        LogPackageAdded(BuildPackageId, path);
        return true;
    }

    private static bool DeclaresPackageVersion(string packagesProps)
    {
        try
        {
            return XDocument.Load(packagesProps).Descendants().Any(static e => e.Name.LocalName == "PackageVersion"
                && string.Equals(e.Attribute("Include")?.Value.Trim(), BuildPackageId, StringComparison.OrdinalIgnoreCase));
        }
        catch (XmlException)
        {
            return false;
        }
    }

    // Sets metadata where the item has it, as a child element or an attribute, or else as a new attribute.
    private static bool SetMetadata(XElement item, string name, string value)
    {
        if (item.Elements().LastOrDefault(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase)) is { } element)
        {
            if (element.Value == value)
            {
                return false;
            }

            element.Value = value;
            return true;
        }

        var attribute = item.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
        {
            item.Add(new XAttribute(name, value));
            return true;
        }

        if (attribute.Value == value)
        {
            return false;
        }

        attribute.Value = value;
        return true;
    }

    // Adds an item after the last item of its type in an unconditional item group of the project, or else into a new item
    // group after the last item group (or property group).
    private static void AddItem(XElement project, XElement item)
    {
        var sibling = project.Elements()
            .Where(static e => e.Name.LocalName == "ItemGroup" && e.Attribute("Condition") is null)
            .SelectMany(static group => group.Elements())
            .LastOrDefault(e => e.Name.LocalName == item.Name.LocalName);
        if (sibling is not null)
        {
            InsertAfter(sibling, item);
            return;
        }

        var group = new XElement(project.Name.Namespace + "ItemGroup");
        var previous = project.Elements().LastOrDefault(static e => e.Name.LocalName == "ItemGroup")
            ?? project.Elements().LastOrDefault(static e => e.Name.LocalName == "PropertyGroup");
        if (previous is not null)
        {
            InsertAfter(previous, group);
        }
        else
        {
            AppendChild(project, group);
        }

        AppendChild(group, item);
    }

    // Inserts an element after a sibling, separated from it like the sibling is from the node before it.
    private static void InsertAfter(XElement sibling, XElement element)
    {
        string separator = sibling.PreviousNode is XText text && IsWhitespace(text) ? text.Value : "\n" + GetIndentation(sibling);
        sibling.AddAfterSelf(new XText(separator), element);
    }

    // Adds an element after the last child element, or into an element without child elements, one level deeper.
    private static void AppendChild(XElement parent, XElement child)
    {
        if (parent.Elements().LastOrDefault() is { } last)
        {
            InsertAfter(last, child);
            return;
        }

        string indentation = GetIndentation(parent);
        string childIndentation = indentation + GetIndentationUnit(parent.Document);
        if (parent.Nodes().LastOrDefault(static n => !IsWhitespace(n)) is { } comment)
        {
            comment.AddAfterSelf(new XText("\n" + childIndentation), child);
        }
        else
        {
            parent.RemoveNodes();
            parent.Add(new XText("\n" + childIndentation), child, new XText("\n" + indentation));
        }
    }

    // The whitespace that indents an element on its line, or else the indentation unit for each ancestor.
    private static string GetIndentation(XElement element)
    {
        if (element.PreviousNode is XText text && IsWhitespace(text) && text.Value.LastIndexOf('\n') is var newline and >= 0)
        {
            return text.Value[(newline + 1)..];
        }

        return string.Concat(Enumerable.Repeat(GetIndentationUnit(element.Document), element.Ancestors().Count()));
    }

    // The indentation of the first child element of the project; two spaces by default.
    private static string GetIndentationUnit(XDocument? document)
    {
        if (document?.Root?.Elements().FirstOrDefault()?.PreviousNode is XText text && IsWhitespace(text)
            && text.Value.LastIndexOf('\n') is var newline and >= 0 && newline < text.Value.Length - 1)
        {
            return text.Value[(newline + 1)..];
        }

        return "  ";
    }

    // Removes an element with the whitespace that indents it.
    private static void RemoveWithWhitespace(XElement element)
    {
        if (element.PreviousNode is XText text && IsWhitespace(text))
        {
            text.Remove();
        }

        element.Remove();
    }

    private static bool IsWhitespace(XNode node) => node is XText text and not XCData && string.IsNullOrWhiteSpace(text.Value);

    private static bool IsInTarget(XElement element) => element.Ancestors().Any(static a => a.Name.LocalName == "Target");

    [LoggerMessage(Level = LogLevel.Information, Message = "Added the CsComReference '{Source}' to '{Project}'")]
    private partial void LogAdded(string source, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated the CsComReference '{Source}' in '{Project}'")]
    private partial void LogUpdated(string source, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "The CsComReference '{Source}' is already in '{Project}'")]
    private partial void LogUnchanged(string source, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed the CsComReference '{Source}' from '{Project}'")]
    private partial void LogRemoved(string source, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "Added a reference to the {Package} package to '{Project}'")]
    private partial void LogPackageAdded(string package, string project);

    [LoggerMessage(Level = LogLevel.Information, Message = "'{Project}' has no CsComReference items")]
    private partial void LogNoItems(string project);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Reason} The reference is added anyway")]
    private partial void LogSourceNotFound(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The configuration file '{Path}' does not exist")]
    private partial void LogConfigurationNotFound(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The project uses central package management ({Path}): add <PackageVersion Include=\"{Package}\" Version=\"{Version}\" /> to it")]
    private partial void LogCentralPackageManagement(string path, string package, string version);
}
