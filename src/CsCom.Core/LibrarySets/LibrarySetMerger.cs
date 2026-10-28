using CsCom.Configuration;
using CsCom.Model;
using CsCom.Output;
using CsCom.Transformations;
using Microsoft.Extensions.Logging;

namespace CsCom.LibrarySets;

/// <summary>
/// A library of a set: a reference with all its types, or a library whose types are only used by the references.
/// </summary>
internal sealed class LibrarySetEntry
{
    /// <summary>The model, with only the types of this library.</summary>
    public required TypeLibrary Library { get; init; }

    /// <summary>The namespace of the types.</summary>
    public required string Namespace { get; init; }

    /// <summary>The reference, or <see langword="null"/> for a library that is only used by the references.</summary>
    public LibrarySetReference? Reference { get; init; }

    /// <summary>The transformers of the library.</summary>
    public PipelineConfiguration Configuration { get; set; } = PipelineConfiguration.Empty;
}

/// <summary>
/// Merges the models of the references, which contain copies of the types they use from other libraries, into one model per
/// library, so that every type is generated once, in the namespace of its library.
/// </summary>
/// <remarks>
/// The types imported from a library that is a reference are replaced by the types of that reference. The types imported from
/// other libraries are collected into a model per library, which contains the types used by any of the references.
/// </remarks>
internal sealed partial class LibrarySetMerger
{
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, string> _namespaces;
    private readonly List<LibrarySetEntry> _entries = [];
    private readonly Dictionary<string, LibrarySetEntry> _entriesByLibrary = new(StringComparer.Ordinal);
    private readonly Dictionary<LibrarySetEntry, Dictionary<string, TypeDefinition>> _types = [];
    private readonly Dictionary<TypeDefinition, TypeDefinition> _replacements = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _versionWarnings = new(StringComparer.Ordinal);

    private LibrarySetMerger(IReadOnlyDictionary<string, string> namespaces, ILogger logger)
    {
        _namespaces = namespaces;
        _logger = logger;
    }

    /// <summary>
    /// Merges the models of the references.
    /// </summary>
    /// <param name="references">The references with their models, which are changed, and their configurations.</param>
    /// <param name="namespaces">The namespaces by library name, case-insensitive, unless the reference has one.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The libraries: the references in order, then the libraries they use.</returns>
    public static List<LibrarySetEntry> Merge(
        IReadOnlyList<(LibrarySetReference Reference, TypeLibrary Library, PipelineConfiguration Configuration)> references,
        IReadOnlyDictionary<string, string> namespaces,
        ILogger logger)
    {
        var merger = new LibrarySetMerger(namespaces, logger);
        var imported = new List<TypeDefinition>();
        foreach (var (reference, library, configuration) in references)
        {
            imported.AddRange(merger.AddReference(reference, library, configuration));
        }

        foreach (var type in imported)
        {
            merger.AddImportedType(type);
        }

        foreach (var entry in merger._entries)
        {
            TypeReferenceRewriter.Rewrite(entry.Library, merger.Replace);
        }

        merger.RemoveUnusedTypes();
        ValidateNamespaces(merger._entries);
        return merger._entries;
    }

    /// <summary>
    /// Checks that the types used by every library are generated, e.g. that a configuration file did not exclude a type
    /// that another library uses.
    /// </summary>
    public static void ValidateReferences(IReadOnlyList<LibrarySetEntry> entries)
    {
        var generated = entries.SelectMany(static e => e.Library.Types).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            foreach (var type in entry.Library.Types)
            {
                if (GetUsedTypes(type).FirstOrDefault(d => !generated.Contains(d)) is { } missing)
                {
                    throw new CsComException(
                        $"The type '{type.Name}' of '{entry.Library.Name}' uses '{missing.Name}', which a configuration file removed from its library.");
                }
            }
        }
    }

    /// <summary>
    /// Gets the types that a type uses: the types of its members, its base interface, the interfaces of a coclass and the
    /// target of an alias.
    /// </summary>
    public static List<TypeDefinition> GetUsedTypes(TypeDefinition type)
    {
        var library = new TypeLibrary { Name = string.Empty };
        library.Types.Add(type);
        var used = new List<TypeDefinition>();
        TypeReferenceRewriter.Rewrite(library, reference =>
        {
            if (reference is UserDefinedTypeReference { Definition: var definition })
            {
                used.Add(definition);
            }

            return reference;
        });

        return used;
    }

    // The libraries are identified by their LIBID, or by their name if they have none, e.g. hand-written ComImport assemblies.
    private static string GetLibraryKey(Guid guid, string name) =>
        guid != Guid.Empty ? guid.ToString("B") : "name:" + name.ToUpperInvariant();

    // The types are identified by their GUID, or by their name in the library if they have none, e.g. aliases and modules.
    private static string GetTypeKey(TypeDefinition type) =>
        type.Guid is { } guid && guid != Guid.Empty ? guid.ToString("B") : "name:" + type.OriginalName;

    private static void ValidateNamespaces(List<LibrarySetEntry> entries)
    {
        foreach (var entry in entries)
        {
            ProjectGenerator.ValidateNamespace(entry.Namespace);
        }

        foreach (var group in entries.GroupBy(static e => e.Namespace, StringComparer.OrdinalIgnoreCase).Where(static g => g.Count() > 1))
        {
            string libraries = string.Join(" and ", group.Select(static e => $"'{e.Library.Name}'"));
            throw new CsComException(
                $"The libraries {libraries} would be generated into the same namespace '{group.Key}'. Set another namespace for one of them; " +
                "a library that is only used by the references can be added as a reference with its own namespace.");
        }
    }

    private List<TypeDefinition> AddReference(LibrarySetReference reference, TypeLibrary library, PipelineConfiguration configuration)
    {
        string key = GetLibraryKey(library.Guid, library.Name);
        if (_entriesByLibrary.TryGetValue(key, out var existing))
        {
            throw new CsComException($"The library '{library.Name}' is referenced twice: as '{existing.Reference!.Source}' and as '{reference.Source}'.");
        }

        var imported = library.Types.Where(static t => t.ImportedFrom is not null).ToList();
        library.Types.RemoveAll(static t => t.ImportedFrom is not null);
        library.ImportedLibraries.Clear();

        var entry = new LibrarySetEntry
        {
            Library = library,
            Namespace = reference.Namespace ?? GetNamespace(library.Name),
            Reference = reference,
            Configuration = configuration,
        };
        Add(key, entry);
        foreach (var type in library.Types)
        {
            _types[entry].TryAdd(GetTypeKey(type), type);
        }

        return imported;
    }

    private void AddImportedType(TypeDefinition type)
    {
        var origin = type.ImportedFrom!;
        string key = GetLibraryKey(origin.Guid, origin.Name);
        if (!_entriesByLibrary.TryGetValue(key, out var entry))
        {
            entry = new LibrarySetEntry
            {
                Library = new TypeLibrary { Name = origin.Name, Guid = origin.Guid, MajorVersion = origin.MajorVersion, MinorVersion = origin.MinorVersion },
                Namespace = GetNamespace(origin.Name),
            };
            Add(key, entry);
        }
        else if ((entry.Library.MajorVersion, entry.Library.MinorVersion) != (origin.MajorVersion, origin.MinorVersion) && _versionWarnings.Add(key))
        {
            LogVersionMismatch(origin.Name, $"{entry.Library.MajorVersion}.{entry.Library.MinorVersion}", $"{origin.MajorVersion}.{origin.MinorVersion}");
        }

        var types = _types[entry];
        if (types.TryGetValue(GetTypeKey(type), out var canonical))
        {
            _replacements[type] = canonical;
            return;
        }

        if (entry.Reference is not null)
        {
            LogImportedTypeNotFound(type.Name, entry.Library.Name);
        }

        types.Add(GetTypeKey(type), type);
        entry.Library.Types.Add(type);
    }

    private string GetNamespace(string library) =>
        _namespaces.TryGetValue(library, out string? @namespace) ? @namespace : "Interop." + NameSanitizer.ToIdentifier(library);

    private void Add(string key, LibrarySetEntry entry)
    {
        _entries.Add(entry);
        _entriesByLibrary.Add(key, entry);
        _types.Add(entry, new Dictionary<string, TypeDefinition>(StringComparer.Ordinal));
    }

    private TypeReference Replace(TypeReference reference) =>
        reference is UserDefinedTypeReference { Definition: var definition } && _replacements.TryGetValue(definition, out var canonical)
            ? new UserDefinedTypeReference(canonical)
            : reference;

    // The copies of the types replaced by those of a reference may have used types that no reference uses.
    private void RemoveUnusedTypes()
    {
        var used = new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<TypeDefinition>(_entries.Where(static e => e.Reference is not null).SelectMany(static e => e.Library.Types));
        while (pending.TryDequeue(out var type))
        {
            if (used.Add(type))
            {
                foreach (var usedType in GetUsedTypes(type))
                {
                    pending.Enqueue(usedType);
                }
            }
        }

        foreach (var entry in _entries.Where(static e => e.Reference is null))
        {
            entry.Library.Types.RemoveAll(t => !used.Contains(t));
        }

        _entries.RemoveAll(static e => e.Reference is null && e.Library.Types.Count == 0);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The references use the versions {FirstVersion} and {Version} of '{Library}'; the types of the first one are generated")]
    private partial void LogVersionMismatch(string library, string firstVersion, string version);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The type '{Type}' was not found in the reference '{Library}'; the imported copy is generated")]
    private partial void LogImportedTypeNotFound(string type, string library);
}
