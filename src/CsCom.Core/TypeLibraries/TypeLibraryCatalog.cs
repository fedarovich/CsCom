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
    private readonly Dictionary<Guid, CatalogLibrary?> _libraries = [];
    private readonly Dictionary<Guid, string> _loadErrors = [];
    private Dictionary<Guid, string>? _files;

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
        if (!_libraries.TryGetValue(reference.Guid, out library))
        {
            string? registered = _findRegistered(reference);
            if (GetFiles().GetValueOrDefault(reference.Guid) is { } file)
            {
                library = new CatalogLibrary(reference.Guid, file, Load(file), registered is not null);
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
                    _loadErrors[reference.Guid] = e.Message;
                    LogUnloadableLibrary(reference.Name, reference.MajorVersion, reference.MinorVersion, e.Message);
                }
            }

            _libraries.Add(reference.Guid, library);
        }

        return library is not null;
    }

    /// <summary>
    /// Gets why a registered type library that <see cref="TryGetLibrary"/> did not return could not be loaded.
    /// </summary>
    internal string? GetLoadError(Guid libraryId) => _loadErrors.GetValueOrDefault(libraryId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The registered type library '{Name}' {Major}.{Minor} cannot be loaded, so it cannot be told which types it has: {Error}")]
    private partial void LogUnloadableLibrary(string name, int major, int minor, string error);

    // The files by the identifiers of their libraries.
    private Dictionary<Guid, string> GetFiles()
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
                typeLib.ReleaseTLibAttr(attr);
                _files.TryAdd(guid, fullPath);
            }
        }

        return _files;
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
