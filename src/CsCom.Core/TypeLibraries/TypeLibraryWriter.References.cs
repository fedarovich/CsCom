using CsCom.Model;
using CsCom.TypeLibraries.Native;

using TYPEFLAGS = System.Runtime.InteropServices.ComTypes.TYPEFLAGS;
using TYPEKIND = System.Runtime.InteropServices.ComTypes.TYPEKIND;

namespace CsCom.TypeLibraries;

public sealed partial class TypeLibraryWriter
{
    private sealed unsafe partial class Session
    {
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
                typeInfo = imported.Find(target.Guid, target.OriginalName)
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

        private CatalogLibrary GetImportedLibrary(TypeLibraryReference reference)
        {
            var key = (reference.Guid, reference.MajorVersion, reference.MinorVersion);
            if (_imported.TryGetValue(key, out var imported))
            {
                return imported;
            }

            if (!_catalog.TryGetLibrary(reference, out imported))
            {
                string reason = _catalog.GetLoadError(reference) is { } error ? $"is registered, but cannot be loaded ({error})" : "is not registered";
                throw new TypeLibraryException(
                    $"The type library '{reference.Name}' {reference.MajorVersion}.{reference.MinorVersion} ({reference.Guid:B}), which the types of '{_library.Name}' use, {reason}: specify its file.");
            }

            // A file is the library of every version of its identifier, and is reported once.
            _imported.Add(key, imported);
            if (_reported.Add(imported))
            {
                if (!imported.IsRegistered)
                {
                    LogUnregisteredImport(reference.Name, System.IO.Path.GetFileName(imported.Path));
                }

                LogImportedLibrary(reference.Name, imported.Path);
            }

            return imported;
        }
    }
}
