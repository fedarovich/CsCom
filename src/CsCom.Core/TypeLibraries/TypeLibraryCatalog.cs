using System.Runtime.InteropServices;
using CsCom.Model;
using CsCom.TypeLibraries.Native;

namespace CsCom.TypeLibraries;

/// <summary>
/// Finds the type libraries that an exported type library refers to: among the files that it is given, by their
/// identifiers, and otherwise in the registry. The reader of an assembly asks it which types these libraries have, and
/// the writer refers to them.
/// </summary>
public sealed unsafe class TypeLibraryCatalog
{
    private readonly IReadOnlyList<string> _paths;
    private readonly Dictionary<Guid, CatalogLibrary?> _libraries = [];
    private Dictionary<Guid, string>? _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeLibraryCatalog"/> class.
    /// </summary>
    /// <param name="typeLibraryPaths">The files of type libraries, which are preferred to the registered ones.</param>
    public TypeLibraryCatalog(IReadOnlyList<string>? typeLibraryPaths = null)
    {
        _paths = typeLibraryPaths ?? [];
    }

    /// <summary>
    /// Gets a type library, or <see langword="false"/> if it is neither among the files nor registered.
    /// </summary>
    /// <exception cref="TypeLibraryException">A file of the catalog, or the registered type library, cannot be loaded.</exception>
    internal bool TryGetLibrary(TypeLibraryReference reference, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CatalogLibrary? library)
    {
        if (!_libraries.TryGetValue(reference.Guid, out library))
        {
            string? registered = TypeLibrarySource.FindRegistered(reference.Guid, reference.MajorVersion, reference.MinorVersion);
            string? path = GetFiles().GetValueOrDefault(reference.Guid) ?? (registered is null ? null : TypeLibrarySource.Resolve(registered));
            library = path is null ? null : new CatalogLibrary(reference.Guid, path, Load(path), registered is not null);
            _libraries.Add(reference.Guid, library);
        }

        return library is not null;
    }

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
