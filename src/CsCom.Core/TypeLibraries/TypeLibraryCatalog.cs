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
    private readonly HashSet<CatalogLibrary> _replacing = [];
    private List<CatalogLibrary>? _files;

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
            if (FindFile(reference) is { } file)
            {
                library = file;
                if (registered is not null && _replacing.Add(file))
                {
                    ReportRegisteredLibrary(reference, file, registered);
                }
            }
            else if (registered is not null)
            {
                try
                {
                    string path = TypeLibrarySource.Resolve(registered);
                    library = new CatalogLibrary(reference, path, Load(path), isRegistered: true);
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
    /// Gets the file of another library that the catalog has loaded with the identifier of a library, in any version: a
    /// file that it was given, or a registered library. OLE Automation resolves the references of a type library by the
    /// identifiers of the libraries alone, to any such library that the process has loaded, so a type library cannot
    /// refer to the types of one of two such libraries.
    /// </summary>
    internal string? GetAmbiguousFile(CatalogLibrary library) =>
        (_files ?? []).Concat(_libraries.Values.OfType<CatalogLibrary>())
            .FirstOrDefault(other => other.LibraryId == library.LibraryId && !string.Equals(other.Path, library.Path, StringComparison.OrdinalIgnoreCase))?.Path;

    // The file of a library: the one with the version of the reference, or else the first one with its identifier, which
    // is the library of that identifier in every version.
    private CatalogLibrary? FindFile(TypeLibraryReference reference)
    {
        var files = GetFiles();
        return files.FirstOrDefault(f => ReferenceComparer.Instance.Equals(f.Reference, reference)) ?? files.FirstOrDefault(f => f.LibraryId == reference.Guid);
    }

    // The programs that load the exported library find the registered library, not the file that was read in its place.
    private void ReportRegisteredLibrary(TypeLibraryReference reference, CatalogLibrary file, string registered)
    {
        try
        {
            string path = TypeLibrarySource.GetFilePath(TypeLibrarySource.Resolve(registered));
            if (!string.Equals(path, file.Path, StringComparison.OrdinalIgnoreCase))
            {
                LogFilePreferred(reference.Name, file.Path, path);
            }
        }
        catch (CsComException)
        {
            // The registration is stale, so there is no other library to find.
        }
    }

    /// <summary>
    /// Finds the type library that has all the given interfaces among the files of the catalog: the type library of an
    /// assembly can have another identifier than the assembly, so it is recognized by the interfaces of the assembly.
    /// </summary>
    internal CatalogLibrary? FindFileWithInterfaces(IReadOnlyCollection<Guid> interfaceIds)
    {
        if (interfaceIds.Count == 0)
        {
            return null;
        }

        foreach (var file in GetFiles())
        {
            if (interfaceIds.All(id => file.FindById(id) is not null))
            {
                return file;
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "The type library '{Name}' is read from '{File}', while '{Registered}' is registered: the programs that load the exported library find its types in the registered one")]
    private partial void LogFilePreferred(string name, string file, string registered);

    // The libraries of the files, in the order they were given.
    private List<CatalogLibrary> GetFiles()
    {
        if (_files is null)
        {
            _files = [];
            foreach (string fullPath in _paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ITypeLib typeLib = Load(fullPath);
                typeLib.GetLibAttr(out TLIBATTR* attr);
                Guid guid = attr->guid;
                ushort major = attr->wMajorVerNum;
                ushort minor = attr->wMinorVerNum;
                typeLib.ReleaseTLibAttr(attr);
                typeLib.GetDocumentation(-1, out string? name, out _, out _, out _);
                var reference = new TypeLibraryReference(name ?? Path.GetFileNameWithoutExtension(fullPath), guid, major, minor);
                _files.Add(new CatalogLibrary(reference, fullPath, typeLib, _findRegistered(reference) is not null));
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
internal sealed unsafe class CatalogLibrary(TypeLibraryReference reference, string path, ITypeLib typeLib, bool isRegistered)
{
    private Dictionary<string, uint>? _names;

    /// <summary>The identity of the library: that of a file, or the one by which a registered library was found.</summary>
    public TypeLibraryReference Reference { get; } = reference;

    public Guid LibraryId => Reference.Guid;

    /// <summary>The file of the library, without the index of a type library resource.</summary>
    public string Path { get; } = TypeLibrarySource.GetFilePath(path);

    public ITypeLib TypeLib { get; } = typeLib;

    /// <summary>Whether the library is registered, so that programs find it wherever its file is.</summary>
    public bool IsRegistered { get; } = isRegistered;

    /// <summary>
    /// Finds a type by its identifier, or by its name: the types without an identifier, and the types whose identifiers
    /// the code generated for them does not keep, are only found by their names.
    /// </summary>
    public ITypeInfo? Find(Guid? id, string name) => (id is { } guid ? FindById(guid) : null) ?? FindByName(name);

    public ITypeInfo? FindById(Guid id) =>
        TypeLib.GetTypeInfoOfGuid(id, out ITypeInfo? typeInfo) >= 0 ? typeInfo : null;

    /// <summary>
    /// Finds an enumeration or a structure by its name that has the given members: a name alone does not tell that a type
    /// of a library is a type of an assembly.
    /// </summary>
    public ITypeInfo? FindByMembers(string name, bool isEnum, IReadOnlyCollection<string> members)
    {
        if (FindByName(name) is not { } typeInfo)
        {
            return null;
        }

        typeInfo.GetTypeAttr(out TYPEATTR* attr);
        var kind = attr->typekind;
        uint count = attr->cVars;
        typeInfo.ReleaseTypeAttr(attr);
        bool isKind = isEnum
            ? kind == System.Runtime.InteropServices.ComTypes.TYPEKIND.TKIND_ENUM
            : kind is System.Runtime.InteropServices.ComTypes.TYPEKIND.TKIND_RECORD or System.Runtime.InteropServices.ComTypes.TYPEKIND.TKIND_UNION;
        if (!isKind || count != members.Count)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (uint i = 0; i < count; i++)
        {
            typeInfo.GetVarDesc(i, out VARDESC* variable);
            int id = variable->memid;
            typeInfo.ReleaseVarDesc(variable);
            typeInfo.GetDocumentation(id, out string? member, out _, out _, out _);
            names.Add(member ?? string.Empty);
        }

        return names.SetEquals(members) ? typeInfo : null;
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
