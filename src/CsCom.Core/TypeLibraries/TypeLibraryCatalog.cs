using System.Runtime.InteropServices;
using CsCom.Model;
using CsCom.TypeLibraries.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.TypeLibraries;

/// <summary>
/// Finds the type libraries that an exported type library refers to: among the files that it is given, by their
/// identifiers, and otherwise in the registry. The reader of an assembly asks it which types these libraries have, and
/// the writer refers to them.
/// </summary>
public sealed unsafe partial class TypeLibraryCatalog
{
    private static readonly Guid StdOleLibraryId = new("00020430-0000-0000-C000-000000000046");

    private readonly IReadOnlyList<string> _paths;
    private readonly ILogger _logger;
    private readonly Func<TypeLibraryReference, string?> _findRegistered;
    // A file is the library of its identifier in any version; the registry has the versions of a library apart.
    private readonly Dictionary<TypeLibraryReference, CatalogLibrary?> _libraries = new(ReferenceComparer.Instance);
    private readonly Dictionary<TypeLibraryReference, string> _loadErrors = new(ReferenceComparer.Instance);
    private List<(string Path, TypeLibraryReference Reference, ITypeLib TypeLib)>? _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeLibraryCatalog"/> class.
    /// </summary>
    /// <param name="typeLibraryPaths">The files of type libraries, which are preferred to the registered ones.</param>
    /// <param name="logger">The logger.</param>
    public TypeLibraryCatalog(IReadOnlyList<string>? typeLibraryPaths = null, ILogger<TypeLibraryCatalog>? logger = null)
        : this(typeLibraryPaths, logger, static reference => TypeLibrarySource.FindRegistered(reference.Guid, reference.MajorVersion, reference.MinorVersion))
    {
    }

    // The registry is replaced in tests.
    internal TypeLibraryCatalog(IReadOnlyList<string>? typeLibraryPaths, ILogger? logger, Func<TypeLibraryReference, string?> findRegistered)
    {
        _paths = typeLibraryPaths ?? [];
        _logger = logger ?? NullLogger<TypeLibraryCatalog>.Instance;
        _findRegistered = findRegistered;
    }

    /// <summary>
    /// Gets the files that the catalog has read so far, on which what was read or written with it depends: the files that
    /// it was given, once it looked a library up among them, and the files of the registered libraries that it loaded,
    /// except that of OLE Automation.
    /// </summary>
    public IReadOnlyCollection<string> Files
    {
        get
        {
            var files = new List<string>();
            if (_files is not null)
            {
                files.AddRange(_paths.Select(static p => TypeLibrarySource.GetFilePath(Path.GetFullPath(p))));
            }

            files.AddRange(_libraries.Values.Where(static l => l is not null && l.LibraryId != StdOleLibraryId).Select(static l => l!.Path));
            return [.. files.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>
    /// Gets a type library, or <see langword="false"/> if it is neither among the files nor registered, or if it is
    /// registered and cannot be loaded, as a library that was uninstalled without removing its registration.
    /// </summary>
    /// <exception cref="TypeLibraryException">A file of the catalog cannot be loaded.</exception>
    internal bool TryGetLibrary(TypeLibraryReference reference, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogLibrary? library)
    {
        if (!_libraries.TryGetValue(reference, out library))
        {
            string? registered = _findRegistered(reference);
            if (GetFiles().FirstOrDefault(f => f.Reference.Guid == reference.Guid) is { Path: not null } file)
            {
                library = new CatalogLibrary(reference.Guid, file.Path, file.TypeLib, registered is not null);
            }
            else if (registered is not null)
            {
                try
                {
                    string path = TypeLibrarySource.Resolve(registered);
                    library = new CatalogLibrary(reference.Guid, path, Load(path), isRegistered: true);
                }
                catch (CsComException e)
                {
                    _loadErrors[reference] = e.Message;
                    LogUnloadableLibrary(reference.Name, reference.MajorVersion, reference.MinorVersion, e.Message);
                }
            }

            _libraries.Add(reference, library);
        }

        return library is not null;
    }

    /// <summary>
    /// Gets why a registered type library that <see cref="TryGetLibrary"/> did not return could not be loaded.
    /// </summary>
    internal string? GetLoadError(TypeLibraryReference reference) => _loadErrors.GetValueOrDefault(reference);

    /// <summary>
    /// Finds the type library that has an interface among the files of the catalog: the type library of an assembly
    /// can have another identifier than the assembly, so it is recognized by the interfaces of the assembly.
    /// </summary>
    internal TypeLibraryReference? FindFileWithInterface(Guid interfaceId)
    {
        foreach (var file in GetFiles())
        {
            if (file.TypeLib.GetTypeInfoOfGuid(interfaceId, out ITypeInfo? typeInfo) >= 0 && typeInfo is not null)
            {
                return file.Reference;
            }
        }

        return null;
    }

    /// <summary>Gets a value indicating whether the catalog was given files.</summary>
    internal bool HasFiles => _paths.Count > 0;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The registered type library '{Name}' {Major}.{Minor} cannot be loaded, so it cannot be told which types it has: {Error}")]
    private partial void LogUnloadableLibrary(string name, int major, int minor, string error);

    // The files with the identities of their libraries, in the order they were given: the first one of an identifier
    // is the library of that identifier.
    private List<(string Path, TypeLibraryReference Reference, ITypeLib TypeLib)> GetFiles()
    {
        if (_files is null)
        {
            _files = [];
            foreach (string path in _paths)
            {
                string fullPath = Path.GetFullPath(path);
                ITypeLib typeLib = Load(fullPath);
                typeLib.GetLibAttr(out TLIBATTR* attr);
                Guid guid = attr->guid;
                ushort major = attr->wMajorVerNum;
                ushort minor = attr->wMinorVerNum;
                typeLib.ReleaseTLibAttr(attr);
                typeLib.GetDocumentation(-1, out string? name, out _, out _, out _);
                _files.Add((fullPath, new TypeLibraryReference(name ?? Path.GetFileNameWithoutExtension(fullPath), guid, major, minor), typeLib));
            }
        }

        return _files;
    }

    private sealed class ReferenceComparer : IEqualityComparer<TypeLibraryReference>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(TypeLibraryReference? x, TypeLibraryReference? y) =>
            ReferenceEquals(x, y) || (x is not null && y is not null && x.Guid == y.Guid && x.MajorVersion == y.MajorVersion && x.MinorVersion == y.MinorVersion);

        public int GetHashCode(TypeLibraryReference obj) => HashCode.Combine(obj.Guid, obj.MajorVersion, obj.MinorVersion);
    }

    private static ITypeLib Load(string path)
    {
        int hr = OleAut32.LoadTypeLibEx(path, REGKIND.REGKIND_NONE, out ITypeLib? typeLib);
        if (hr < 0 || typeLib is null)
        {
            throw new TypeLibraryException($"Failed to load the type library '{path}' (HRESULT 0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
        }

        return typeLib;
    }
}

/// <summary>
/// A type library of a <see cref="TypeLibraryCatalog"/>.
/// </summary>
internal sealed class CatalogLibrary(Guid libraryId, string path, ITypeLib typeLib, bool isRegistered)
{
    private Dictionary<string, uint>? _names;

    public Guid LibraryId { get; } = libraryId;

    /// <summary>The file of the library, without the index of a type library resource.</summary>
    public string Path { get; } = TypeLibrarySource.GetFilePath(path);

    public ITypeLib TypeLib { get; } = typeLib;

    /// <summary>Whether the library is registered, so that programs find it wherever its file is.</summary>
    public bool IsRegistered { get; } = isRegistered;

    /// <summary>
    /// Finds a type by its identifier, or by its name: the types without an identifier, and the types whose identifiers
    /// the code generated for them does not keep, are only found by their names.
    /// </summary>
    public ITypeInfo? Find(Guid? id, string name)
    {
        if (id is { } guid && TypeLib.GetTypeInfoOfGuid(guid, out ITypeInfo? typeInfo) >= 0 && typeInfo is not null)
        {
            return typeInfo;
        }

        return FindByName(name);
    }

    public ITypeInfo? FindByName(string name)
    {
        if (_names is null)
        {
            _names = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            uint count = TypeLib.GetTypeInfoCount();
            for (uint i = 0; i < count; i++)
            {
                TypeLib.GetDocumentation((int)i, out string? typeName, out _, out _, out _);
                if (typeName is not null)
                {
                    _names.TryAdd(typeName, i);
                }
            }
        }

        if (!_names.TryGetValue(name, out uint index))
        {
            return null;
        }

        TypeLib.GetTypeInfo(index, out ITypeInfo found);
        return found;
    }
}
