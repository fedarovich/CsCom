using System.Runtime.InteropServices;
using CsCom.Model;
using CsCom.TypeLibraries.Native;

using TYPEFLAGS = System.Runtime.InteropServices.ComTypes.TYPEFLAGS;
using TYPEKIND = System.Runtime.InteropServices.ComTypes.TYPEKIND;

namespace CsCom.TypeLibraries;

public sealed partial class TypeLibraryWriter
{
    private sealed unsafe partial class Session
    {
        // The files of the options by the identifiers of their libraries.
        private void IndexTypeLibraryPaths()
        {
            foreach (string path in _options.TypeLibraryPaths)
            {
                string fullPath = System.IO.Path.GetFullPath(path);
                ITypeLib typeLib = Load(fullPath);
                _libraryPaths.TryAdd(GetLibAttr(typeLib).guid, fullPath);
            }
        }

        // Gets the reference of a type to another type, of the library or of a library that it imports, which is only valid
        // for the referring type.
        private uint GetReference(TypeDefinition owner, TypeDefinition target, bool vtable)
        {
            var key = (owner, (object)target, vtable);
            if (_references.TryGetValue(key, out uint reference))
            {
                return reference;
            }

            ITypeInfo typeInfo;
            if (target.ImportedFrom is { } library)
            {
                var imported = GetImportedLibrary(library);
                typeInfo = imported.Find(target)
                    ?? throw new TypeLibraryException($"The type library '{imported.Path}' does not contain the type '{target.OriginalName}' that '{owner.OriginalName}' uses.");
            }
            else if (_created.TryGetValue(target, out var created))
            {
                typeInfo = (ITypeInfo)created;
            }
            else
            {
                throw new TypeLibraryException($"The type '{target.OriginalName}' that '{owner.OriginalName}' uses is not a type of the library.");
            }

            return AddReference(key, vtable ? GetVtableInterface(typeInfo) : typeInfo);
        }

        private uint GetStdOleReference(TypeDefinition owner, Guid interfaceId)
        {
            var key = (owner, (object)interfaceId, true);
            if (_references.TryGetValue(key, out uint reference))
            {
                return reference;
            }

            var stdOle = GetImportedLibrary(StdOle);
            if (stdOle.TypeLib.GetTypeInfoOfGuid(interfaceId, out ITypeInfo? typeInfo) < 0 || typeInfo is null)
            {
                throw new TypeLibraryException($"The type library '{stdOle.Path}' does not contain the interface {interfaceId:B}.");
            }

            return AddReference(key, GetVtableInterface(typeInfo));
        }

        private uint GetStdOleReference(TypeDefinition owner, string name)
        {
            var key = (owner, (object)name, false);
            if (_references.TryGetValue(key, out uint reference))
            {
                return reference;
            }

            var stdOle = GetImportedLibrary(StdOle);
            ITypeInfo typeInfo = stdOle.FindByName(name)
                ?? throw new TypeLibraryException($"The type library '{stdOle.Path}' does not contain the type '{name}'.");
            return AddReference(key, typeInfo);
        }

        private uint AddReference((TypeDefinition Owner, object Target, bool Vtable) key, ITypeInfo typeInfo)
        {
            _created[key.Owner].AddRefTypeInfo(typeInfo, out uint reference);
            _references.Add(key, reference);
            return reference;
        }

        // The vtable interface of a dual interface, which a type library presents as its dispinterface.
        private static ITypeInfo GetVtableInterface(ITypeInfo typeInfo)
        {
            typeInfo.GetTypeAttr(out TYPEATTR* attr);
            bool isDualDispatch = attr->typekind == TYPEKIND.TKIND_DISPATCH && (attr->wTypeFlags & TYPEFLAGS.TYPEFLAG_FDUAL) != 0;
            typeInfo.ReleaseTypeAttr(attr);
            if (isDualDispatch && typeInfo.GetRefTypeOfImplType(unchecked((uint)-1), out uint reference) >= 0)
            {
                typeInfo.GetRefTypeInfo(reference, out ITypeInfo vtable);
                return vtable;
            }

            return typeInfo;
        }

        private ImportedLibrary GetImportedLibrary(TypeLibraryReference reference)
        {
            if (_imported.TryGetValue(reference.Guid, out var imported))
            {
                return imported;
            }

            if (!_libraryPaths.TryGetValue(reference.Guid, out string? path))
            {
                string source = TypeLibrarySource.FindRegistered(reference.Guid, reference.MajorVersion, reference.MinorVersion)
                    ?? throw new TypeLibraryException(
                        $"The type library '{reference.Name}' {reference.MajorVersion}.{reference.MinorVersion} ({reference.Guid:B}), which the types of '{_library.Name}' use, is not registered: specify its file.");
                path = TypeLibrarySource.Resolve(source);
            }

            imported = new ImportedLibrary(reference.Guid, path, Load(path));
            _imported.Add(reference.Guid, imported);
            LogImportedLibrary(reference.Name, path);
            return imported;
        }

        private ITypeLib Load(string path)
        {
            int hr = OleAut32.LoadTypeLibEx(path, REGKIND.REGKIND_NONE, out ITypeLib? typeLib);
            if (hr < 0 || typeLib is null)
            {
                throw new TypeLibraryException($"Failed to load the type library '{path}' (HRESULT 0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
            }

            return typeLib;
        }

        private static TLIBATTR GetLibAttr(ITypeLib typeLib)
        {
            typeLib.GetLibAttr(out TLIBATTR* attr);
            try
            {
                return *attr;
            }
            finally
            {
                typeLib.ReleaseTLibAttr(attr);
            }
        }

        private sealed class ImportedLibrary(Guid libraryId, string path, ITypeLib typeLib)
        {
            private Dictionary<string, uint>? _names;

            public Guid LibraryId { get; } = libraryId;

            public string Path { get; } = TypeLibrarySource.GetFilePath(path);

            public ITypeLib TypeLib { get; } = typeLib;

            // The types without an identifier, and the types whose identifiers the code generated for them does not keep,
            // are found by their names.
            public ITypeInfo? Find(TypeDefinition definition)
            {
                if (definition.Guid is { } guid && TypeLib.GetTypeInfoOfGuid(guid, out ITypeInfo? typeInfo) >= 0 && typeInfo is not null)
                {
                    return typeInfo;
                }

                return FindByName(definition.OriginalName);
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
    }
}
