using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Model;
using CsCom.TypeLibraries.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using CALLCONV = System.Runtime.InteropServices.ComTypes.CALLCONV;
using FUNCFLAGS = System.Runtime.InteropServices.ComTypes.FUNCFLAGS;
using FUNCKIND = System.Runtime.InteropServices.ComTypes.FUNCKIND;
using IMPLTYPEFLAGS = System.Runtime.InteropServices.ComTypes.IMPLTYPEFLAGS;
using TYPEKIND = System.Runtime.InteropServices.ComTypes.TYPEKIND;
using VARFLAGS = System.Runtime.InteropServices.ComTypes.VARFLAGS;
using VARKIND = System.Runtime.InteropServices.ComTypes.VARKIND;

namespace CsCom.TypeLibraries;

/// <summary>
/// Reads a COM type library into the <see cref="TypeLibrary"/> model.
/// </summary>
public sealed partial class TypeLibraryReader
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeLibraryReader"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public TypeLibraryReader(ILogger<TypeLibraryReader>? logger = null)
    {
        _logger = logger ?? NullLogger<TypeLibraryReader>.Instance;
    }

    /// <summary>
    /// Reads the type library from a <c>.tlb</c> file or from a <c>.dll</c>, <c>.ocx</c> or <c>.exe</c> file that contains a type library resource.
    /// </summary>
    /// <param name="path">The path to the file. A resource index can be appended as <c>\N</c>, e.g. <c>foo.dll\2</c>.</param>
    /// <returns>The model of the type library.</returns>
    /// <exception cref="TypeLibraryException">The type library cannot be loaded.</exception>
    public TypeLibrary Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Type libraries can only be read on Windows.");
        }

        string fullPath = System.IO.Path.GetFullPath(path);
        int hr = OleAut32.LoadTypeLibEx(fullPath, REGKIND.REGKIND_NONE, out ITypeLib? typeLib);
        if (hr < 0 || typeLib is null)
        {
            throw new TypeLibraryException($"Failed to load the type library '{path}' (HRESULT 0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
        }

        LogLoading(fullPath);
        return new Session(typeLib, fullPath, _logger).Read();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reading type library '{Path}'")]
    private partial void LogLoading(string path);

    private sealed unsafe partial class Session
    {
        private static readonly Guid StdOleLibraryId = new("00020430-0000-0000-C000-000000000046");
        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
        private static readonly Guid IID_IEnumVARIANT = new("00020404-0000-0000-C000-000000000046");

        private static readonly string[] DispatchMethodNames =
            ["QueryInterface", "AddRef", "Release", "GetTypeInfoCount", "GetTypeInfo", "GetIDsOfNames", "Invoke"];

        private readonly ITypeLib _typeLib;
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly Dictionary<TypeKey, TypeDefinition> _definitions = [];
        private readonly Dictionary<Guid, TypeLibraryReference> _importedLibraries = [];
        private readonly Queue<(ITypeInfo TypeInfo, TypeDefinition Definition)> _pending = [];
        private readonly Dictionary<VarEnum, PrimitiveTypeReference> _primitives = [];
        private Guid _libraryId;
        private TypeLibrary _library = null!;

        public Session(ITypeLib typeLib, string path, ILogger logger)
        {
            _typeLib = typeLib;
            _path = path;
            _logger = logger;
        }

        public TypeLibrary Read()
        {
            TLIBATTR libAttr = GetLibAttr(_typeLib);
            _typeLib.GetDocumentation(-1, out string? name, out string? doc, out _, out _);
            _libraryId = libAttr.guid;
            _library = new TypeLibrary
            {
                Name = name ?? System.IO.Path.GetFileNameWithoutExtension(_path),
                Guid = libAttr.guid,
                MajorVersion = libAttr.wMajorVerNum,
                MinorVersion = libAttr.wMinorVerNum,
                Lcid = libAttr.lcid,
                Documentation = doc,
                Path = _path,
            };

            uint count = _typeLib.GetTypeInfoCount();
            for (uint i = 0; i < count; i++)
            {
                _typeLib.GetTypeInfo(i, out ITypeInfo typeInfo);
                if (ResolveWellKnown(typeInfo, _libraryId) is { } wellKnown)
                {
                    LogSkippingWellKnown(GetName(typeInfo), wellKnown);
                    continue;
                }

                GetOrCreateDefinition(typeInfo, _typeLib, _libraryId, i);
            }

            while (_pending.TryDequeue(out var item))
            {
                Populate(item.TypeInfo, item.Definition);
            }

            // Keep the order of the library: its own types first, then the imported ones in the order they were encountered.
            var ordered = _library.Types.Where(static t => t.ImportedFrom is null).Concat(_library.Types.Where(static t => t.ImportedFrom is not null)).ToList();
            _library.Types.Clear();
            _library.Types.AddRange(ordered);
            _library.ImportedLibraries.AddRange(_importedLibraries.Values);
            return _library;
        }

        private TypeDefinition GetOrCreateDefinition(ITypeInfo typeInfo, ITypeLib containingLib, Guid libraryId, uint index)
        {
            var key = new TypeKey(libraryId, index);
            if (_definitions.TryGetValue(key, out var existing))
            {
                return existing;
            }

            TypeLibraryReference? importedFrom = libraryId == _libraryId ? null : GetImportedLibrary(containingLib, libraryId);
            string name = GetName(typeInfo, out string? doc);
            TypeDefinition definition;

            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            try
            {
                Guid? guid = attr->guid == Guid.Empty ? null : attr->guid;
                var flags = (Model.TypeFlags)(ushort)attr->wTypeFlags;
                definition = attr->typekind switch
                {
                    TYPEKIND.TKIND_ENUM => new EnumDefinition { Name = name, OriginalName = name, Guid = guid, Documentation = doc, Flags = flags, ImportedFrom = importedFrom },
                    TYPEKIND.TKIND_RECORD or TYPEKIND.TKIND_UNION => new RecordDefinition
                    {
                        Name = name,
                        OriginalName = name,
                        Guid = guid,
                        Documentation = doc,
                        Flags = flags,
                        ImportedFrom = importedFrom,
                        IsUnion = attr->typekind == TYPEKIND.TKIND_UNION,
                        Size = (int)attr->cbSizeInstance,
                        Alignment = attr->cbAlignment,
                    },
                    TYPEKIND.TKIND_ALIAS => new AliasDefinition { Name = name, OriginalName = name, Guid = guid, Documentation = doc, Flags = flags, ImportedFrom = importedFrom, Target = GetPrimitive(VarEnum.VT_EMPTY) },
                    TYPEKIND.TKIND_INTERFACE or TYPEKIND.TKIND_DISPATCH => new InterfaceDefinition { Name = name, OriginalName = name, Guid = guid, Documentation = doc, Flags = flags, ImportedFrom = importedFrom },
                    TYPEKIND.TKIND_COCLASS => new CoClassDefinition { Name = name, OriginalName = name, Guid = guid, Documentation = doc, Flags = flags, ImportedFrom = importedFrom },
                    TYPEKIND.TKIND_MODULE => new ModuleDefinition { Name = name, OriginalName = name, Guid = guid, Documentation = doc, Flags = flags, ImportedFrom = importedFrom },
                    _ => throw new TypeLibraryException($"The type '{name}' has an unsupported kind {attr->typekind}."),
                };
            }
            finally
            {
                typeInfo.ReleaseTypeAttr(attr);
            }

            _definitions.Add(key, definition);
            _library.Types.Add(definition);
            _pending.Enqueue((typeInfo, definition));
            if (importedFrom is not null)
            {
                LogImportingType(definition.Kind, name, importedFrom.Name);
            }

            return definition;
        }

        private TypeLibraryReference GetImportedLibrary(ITypeLib typeLib, Guid libraryId)
        {
            if (!_importedLibraries.TryGetValue(libraryId, out var reference))
            {
                TLIBATTR attr = GetLibAttr(typeLib);
                typeLib.GetDocumentation(-1, out string? name, out _, out _, out _);
                reference = new TypeLibraryReference(name ?? libraryId.ToString("B"), libraryId, attr.wMajorVerNum, attr.wMinorVerNum);
                _importedLibraries.Add(libraryId, reference);
            }

            return reference;
        }

        private void Populate(ITypeInfo typeInfo, TypeDefinition definition)
        {
            LogReadingType(definition.Kind, definition.Name);
            switch (definition)
            {
                case EnumDefinition enumDefinition:
                    PopulateEnum(typeInfo, enumDefinition);
                    break;
                case RecordDefinition recordDefinition:
                    PopulateRecord(typeInfo, recordDefinition);
                    break;
                case AliasDefinition aliasDefinition:
                    PopulateAlias(typeInfo, aliasDefinition);
                    break;
                case InterfaceDefinition interfaceDefinition:
                    PopulateInterface(typeInfo, interfaceDefinition);
                    break;
                case CoClassDefinition coClassDefinition:
                    PopulateCoClass(typeInfo, coClassDefinition);
                    break;
                case ModuleDefinition moduleDefinition:
                    PopulateModule(typeInfo, moduleDefinition);
                    break;
            }
        }

        private void PopulateEnum(ITypeInfo typeInfo, EnumDefinition definition)
        {
            ForEachVar(typeInfo, var =>
            {
                string name = GetMemberName(typeInfo, var->memid, out string? doc);
                object? value = var->varkind == VARKIND.VAR_CONST ? ConvertVariant(var->lpvarValue) : null;
                definition.Members.Add(new EnumMember { Name = name, Value = Convert.ToInt64(value ?? 0), Documentation = doc });
            });
        }

        private void PopulateRecord(ITypeInfo typeInfo, RecordDefinition definition)
        {
            ForEachVar(typeInfo, var =>
            {
                string name = GetMemberName(typeInfo, var->memid, out string? doc);
                definition.Fields.Add(new FieldDefinition
                {
                    Name = name,
                    Type = ConvertType(typeInfo, &var->elemdescVar.tdesc),
                    Offset = definition.IsUnion ? 0 : (int)var->oInst,
                    Documentation = doc,
                });
            });
        }

        private void PopulateAlias(ITypeInfo typeInfo, AliasDefinition definition)
        {
            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            try
            {
                definition.Target = ConvertType(typeInfo, &attr->tdescAlias);
            }
            finally
            {
                typeInfo.ReleaseTypeAttr(attr);
            }
        }

        private void PopulateInterface(ITypeInfo typeInfo, InterfaceDefinition definition)
        {
            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            TYPEKIND typeKind = attr->typekind;
            bool isDual = (attr->wTypeFlags & System.Runtime.InteropServices.ComTypes.TYPEFLAGS.TYPEFLAG_FDUAL) != 0;
            typeInfo.ReleaseTypeAttr(attr);

            if (typeKind == TYPEKIND.TKIND_DISPATCH)
            {
                if (isDual && typeInfo.GetRefTypeOfImplType(unchecked((uint)-1), out uint vtableRef) >= 0)
                {
                    typeInfo.GetRefTypeInfo(vtableRef, out ITypeInfo vtableTypeInfo);
                    definition.InterfaceKind = InterfaceKind.Dual;
                    PopulateVtableInterface(vtableTypeInfo, definition);
                }
                else
                {
                    definition.InterfaceKind = InterfaceKind.Dispatch;
                    PopulateDispInterface(typeInfo, definition);
                }
            }
            else
            {
                definition.InterfaceKind = isDual ? InterfaceKind.Dual : InterfaceKind.Vtable;
                PopulateVtableInterface(typeInfo, definition);
            }
        }

        private void PopulateVtableInterface(ITypeInfo typeInfo, InterfaceDefinition definition)
        {
            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            int funcCount;
            int implTypeCount;
            try
            {
                funcCount = attr->cFuncs;
                implTypeCount = attr->cImplTypes;
            }
            finally
            {
                typeInfo.ReleaseTypeAttr(attr);
            }

            if (implTypeCount > 0)
            {
                typeInfo.GetRefTypeOfImplType(0, out uint baseRef);
                typeInfo.GetRefTypeInfo(baseRef, out ITypeInfo baseTypeInfo);
                TypeReference baseInterface = ResolveReference(baseTypeInfo);
                definition.BaseInterface = baseInterface is PrimitiveTypeReference { VarType: VarEnum.VT_UNKNOWN } ? null : baseInterface;

                GetTypeAttr(baseTypeInfo, out TYPEATTR* baseAttr);
                definition.BaseVtableSlots = baseAttr->cbSizeVft / IntPtr.Size;
                baseTypeInfo.ReleaseTypeAttr(baseAttr);
            }

            var methods = new List<MethodDefinition>(funcCount);
            for (uint i = 0; i < funcCount; i++)
            {
                typeInfo.GetFuncDesc(i, out FUNCDESC* func);
                try
                {
                    methods.Add(ReadMethod(typeInfo, func));
                }
                finally
                {
                    typeInfo.ReleaseFuncDesc(func);
                }
            }

            int expectedSlot = definition.BaseVtableSlots;
            foreach (var method in methods.OrderBy(static m => m.VtableSlot))
            {
                if (method.VtableSlot != expectedSlot)
                {
                    LogVtableGap(definition.Name, method.Name, expectedSlot, method.VtableSlot);
                }

                expectedSlot = method.VtableSlot + 1;
                definition.Methods.Add(method);
            }
        }

        private void PopulateDispInterface(ITypeInfo typeInfo, InterfaceDefinition definition)
        {
            definition.BaseInterface = GetPrimitive(VarEnum.VT_DISPATCH);
            definition.BaseVtableSlots = 7;

            ForEachFunc(typeInfo, func =>
            {
                var method = ReadMethod(typeInfo, func);
                if (method.Flags.HasFlag(MemberFlags.Restricted) && DispatchMethodNames.Contains(method.OriginalName))
                {
                    return;
                }

                definition.Methods.Add(method);
            });

            ForEachVar(typeInfo, var =>
            {
                string name = GetMemberName(typeInfo, var->memid, out string? doc);
                definition.Properties.Add(new DispatchPropertyDefinition
                {
                    Name = name,
                    DispId = var->memid,
                    Type = ConvertType(typeInfo, &var->elemdescVar.tdesc),
                    IsReadOnly = (var->wVarFlags & VARFLAGS.VARFLAG_FREADONLY) != 0,
                    Flags = MapVarFlags(var->wVarFlags),
                    Documentation = doc,
                });
            });
        }

        private void PopulateCoClass(ITypeInfo typeInfo, CoClassDefinition definition)
        {
            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            int implTypeCount = attr->cImplTypes;
            typeInfo.ReleaseTypeAttr(attr);

            for (uint i = 0; i < implTypeCount; i++)
            {
                typeInfo.GetRefTypeOfImplType(i, out uint implRef);
                typeInfo.GetImplTypeFlags(i, out IMPLTYPEFLAGS flags);
                typeInfo.GetRefTypeInfo(implRef, out ITypeInfo implTypeInfo);
                definition.Interfaces.Add(new CoClassInterface(
                    ResolveReference(implTypeInfo),
                    IsDefault: (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT) != 0,
                    IsSource: (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE) != 0,
                    IsRestricted: (flags & IMPLTYPEFLAGS.IMPLTYPEFLAG_FRESTRICTED) != 0));
            }
        }

        private void PopulateModule(ITypeInfo typeInfo, ModuleDefinition definition)
        {
            ForEachFunc(typeInfo, func =>
            {
                var method = ReadMethod(typeInfo, func);
                if (typeInfo.GetDllEntry(func->memid, func->invkind, out string? dllName, out string? entryPoint, out ushort ordinal) >= 0)
                {
                    method.DllName = dllName;
                    method.EntryPoint = entryPoint;
                    method.EntryPointOrdinal = entryPoint is null ? ordinal : null;
                }

                definition.Functions.Add(method);
            });

            ForEachVar(typeInfo, var =>
            {
                if (var->varkind != VARKIND.VAR_CONST)
                {
                    return;
                }

                string name = GetMemberName(typeInfo, var->memid, out string? doc);
                definition.Constants.Add(new ConstantDefinition
                {
                    Name = name,
                    Type = ConvertType(typeInfo, &var->elemdescVar.tdesc),
                    Value = ConvertVariant(var->lpvarValue),
                    Documentation = doc,
                });
            });
        }

        private MethodDefinition ReadMethod(ITypeInfo typeInfo, FUNCDESC* func)
        {
            string[] names = GetNames(typeInfo, func->memid, func->cParams + 1);
            string name = names.Length > 0 ? names[0] : $"Method{func->memid:X}";
            typeInfo.GetDocumentation(func->memid, out _, out string? doc, out _, out _);

            bool hasVtableSlot = func->funckind is FUNCKIND.FUNC_VIRTUAL or FUNCKIND.FUNC_PUREVIRTUAL;
            var method = new MethodDefinition
            {
                Name = name,
                OriginalName = name,
                MemberId = func->memid,
                InvokeKind = (Model.InvokeKind)func->invkind,
                CallingConvention = func->callconv == CALLCONV.CC_CDECL ? CallingConvention.Cdecl : CallingConvention.StdCall,
                VtableSlot = hasVtableSlot ? func->oVft / IntPtr.Size : -1,
                ReturnType = ConvertType(typeInfo, &func->elemdescFunc.tdesc),
                Flags = MapFuncFlags(func->wFuncFlags),
                Documentation = doc,
            };

            int paramCount = func->cParams;
            int firstOptional = func->cParamsOpt > 0 ? paramCount - func->cParamsOpt : paramCount;
            for (int i = 0; i < paramCount; i++)
            {
                ELEMDESC* elem = &func->lprgelemdescParam[i];
                var flags = (ParameterFlags)((ushort)elem->paramdesc.wParamFlags & 0x3F);
                if (i >= firstOptional)
                {
                    flags |= ParameterFlags.Optional;
                }

                object? defaultValue = null;
                if (flags.HasFlag(ParameterFlags.HasDefault) && elem->paramdesc.pparamdescex is not null)
                {
                    defaultValue = ConvertVariant(&elem->paramdesc.pparamdescex->varDefaultValue);
                }

                string paramName = i + 1 < names.Length && !string.IsNullOrEmpty(names[i + 1])
                    ? names[i + 1]
                    : i == paramCount - 1 && method.InvokeKind is Model.InvokeKind.PropertyPut or Model.InvokeKind.PropertyPutRef ? "value" : $"param{i}";

                method.Parameters.Add(new ParameterDefinition
                {
                    Name = paramName,
                    Type = ConvertType(typeInfo, &elem->tdesc),
                    Flags = flags,
                    DefaultValue = defaultValue,
                });
            }

            return method;
        }

        private TypeReference ConvertType(ITypeInfo typeInfo, TYPEDESC* desc)
        {
            var vt = (VarEnum)desc->vt;
            switch (vt)
            {
                case VarEnum.VT_PTR:
                    return new PointerTypeReference(ConvertType(typeInfo, desc->lptdesc));
                case VarEnum.VT_SAFEARRAY:
                    return new SafeArrayTypeReference(ConvertType(typeInfo, desc->lptdesc));
                case VarEnum.VT_CARRAY:
                {
                    ARRAYDESC* array = desc->lpadesc;
                    SAFEARRAYBOUND* bounds = ARRAYDESC.Bounds(array);
                    var dimensions = new int[array->cDims];
                    for (int i = 0; i < dimensions.Length; i++)
                    {
                        dimensions[i] = (int)bounds[i].cElements;
                    }

                    return new FixedArrayTypeReference(ConvertType(typeInfo, &array->tdescElem), dimensions);
                }

                case VarEnum.VT_USERDEFINED:
                    typeInfo.GetRefTypeInfo(desc->hreftype, out ITypeInfo refTypeInfo);
                    return ResolveReference(refTypeInfo);
                default:
                    return GetPrimitive(vt);
            }
        }

        private TypeReference ResolveReference(ITypeInfo typeInfo)
        {
            typeInfo.GetContainingTypeLib(out ITypeLib containingLib, out uint index);
            Guid libraryId = GetLibAttr(containingLib).guid;
            return ResolveWellKnown(typeInfo, libraryId)
                ?? new UserDefinedTypeReference(GetOrCreateDefinition(typeInfo, containingLib, libraryId, index));
        }

        private TypeReference? ResolveWellKnown(ITypeInfo typeInfo, Guid libraryId)
        {
            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            Guid guid = attr->guid;
            typeInfo.ReleaseTypeAttr(attr);

            if (guid == IID_IUnknown)
            {
                return GetPrimitive(VarEnum.VT_UNKNOWN);
            }

            if (guid == IID_IDispatch)
            {
                return GetPrimitive(VarEnum.VT_DISPATCH);
            }

            if (guid == IID_IEnumVARIANT)
            {
                return new WellKnownTypeReference(WellKnownType.IEnumVariant);
            }

            if (libraryId == StdOleLibraryId && GetName(typeInfo) == "GUID")
            {
                return new WellKnownTypeReference(WellKnownType.Guid);
            }

            return null;
        }

        private PrimitiveTypeReference GetPrimitive(VarEnum vt)
        {
            if (!_primitives.TryGetValue(vt, out var primitive))
            {
                primitive = new PrimitiveTypeReference(vt);
                _primitives.Add(vt, primitive);
            }

            return primitive;
        }

        private object? ConvertVariant(ComVariant* variant)
        {
            if (variant is null)
            {
                return null;
            }

            VarEnum vt = variant->VarType;
            switch (vt)
            {
                case VarEnum.VT_EMPTY:
                case VarEnum.VT_NULL:
                    return null;
                case VarEnum.VT_I1:
                    return variant->GetRawDataRef<sbyte>();
                case VarEnum.VT_UI1:
                    return variant->GetRawDataRef<byte>();
                case VarEnum.VT_I2:
                    return variant->GetRawDataRef<short>();
                case VarEnum.VT_UI2:
                    return variant->GetRawDataRef<ushort>();
                case VarEnum.VT_I4:
                case VarEnum.VT_INT:
                case VarEnum.VT_ERROR:
                case VarEnum.VT_HRESULT:
                    return variant->GetRawDataRef<int>();
                case VarEnum.VT_UI4:
                case VarEnum.VT_UINT:
                    return variant->GetRawDataRef<uint>();
                case VarEnum.VT_I8:
                    return variant->GetRawDataRef<long>();
                case VarEnum.VT_UI8:
                    return variant->GetRawDataRef<ulong>();
                case VarEnum.VT_R4:
                    return variant->GetRawDataRef<float>();
                case VarEnum.VT_R8:
                    return variant->GetRawDataRef<double>();
                case VarEnum.VT_BOOL:
                    return variant->GetRawDataRef<short>() != 0;
                case VarEnum.VT_BSTR:
                    return variant->As<string>();
                case VarEnum.VT_DECIMAL:
                    return variant->As<decimal>();
                case VarEnum.VT_CY:
                    return decimal.FromOACurrency(variant->GetRawDataRef<long>());
                case VarEnum.VT_DATE:
                    return DateTime.FromOADate(variant->GetRawDataRef<double>());
                case VarEnum.VT_UNKNOWN:
                case VarEnum.VT_DISPATCH:
                    if (variant->GetRawDataRef<nint>() == 0)
                    {
                        return null;
                    }

                    goto default;
                default:
                    LogUnsupportedConstant(vt);
                    return null;
            }
        }

        private static MemberFlags MapFuncFlags(FUNCFLAGS flags)
        {
            const FUNCFLAGS mask = FUNCFLAGS.FUNCFLAG_FRESTRICTED | FUNCFLAGS.FUNCFLAG_FSOURCE | FUNCFLAGS.FUNCFLAG_FBINDABLE | FUNCFLAGS.FUNCFLAG_FHIDDEN
                | FUNCFLAGS.FUNCFLAG_FDEFAULTCOLLELEM | FUNCFLAGS.FUNCFLAG_FUIDEFAULT | FUNCFLAGS.FUNCFLAG_FNONBROWSABLE;
            return (MemberFlags)(ushort)(flags & mask);
        }

        private static MemberFlags MapVarFlags(VARFLAGS flags)
        {
            var result = MemberFlags.None;
            if ((flags & VARFLAGS.VARFLAG_FRESTRICTED) != 0)
            {
                result |= MemberFlags.Restricted;
            }

            if ((flags & VARFLAGS.VARFLAG_FSOURCE) != 0)
            {
                result |= MemberFlags.Source;
            }

            if ((flags & VARFLAGS.VARFLAG_FBINDABLE) != 0)
            {
                result |= MemberFlags.Bindable;
            }

            if ((flags & VARFLAGS.VARFLAG_FHIDDEN) != 0)
            {
                result |= MemberFlags.Hidden;
            }

            if ((flags & VARFLAGS.VARFLAG_FDEFAULTCOLLELEM) != 0)
            {
                result |= MemberFlags.DefaultCollectionElement;
            }

            if ((flags & VARFLAGS.VARFLAG_FUIDEFAULT) != 0)
            {
                result |= MemberFlags.UiDefault;
            }

            if ((flags & VARFLAGS.VARFLAG_FNONBROWSABLE) != 0)
            {
                result |= MemberFlags.NonBrowsable;
            }

            if ((flags & VARFLAGS.VARFLAG_FREADONLY) != 0)
            {
                result |= MemberFlags.ReadOnly;
            }

            return result;
        }

        private static void ForEachFunc(ITypeInfo typeInfo, FuncAction action)
        {
            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            int count = attr->cFuncs;
            typeInfo.ReleaseTypeAttr(attr);
            for (uint i = 0; i < count; i++)
            {
                typeInfo.GetFuncDesc(i, out FUNCDESC* func);
                try
                {
                    action(func);
                }
                finally
                {
                    typeInfo.ReleaseFuncDesc(func);
                }
            }
        }

        private static void ForEachVar(ITypeInfo typeInfo, VarAction action)
        {
            GetTypeAttr(typeInfo, out TYPEATTR* attr);
            int count = attr->cVars;
            typeInfo.ReleaseTypeAttr(attr);
            for (uint i = 0; i < count; i++)
            {
                typeInfo.GetVarDesc(i, out VARDESC* var);
                try
                {
                    action(var);
                }
                finally
                {
                    typeInfo.ReleaseVarDesc(var);
                }
            }
        }

        private static void GetTypeAttr(ITypeInfo typeInfo, out TYPEATTR* attr) => typeInfo.GetTypeAttr(out attr);

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

        private static string GetName(ITypeInfo typeInfo) => GetName(typeInfo, out _);

        private static string GetName(ITypeInfo typeInfo, out string? documentation)
        {
            Marshal.ThrowExceptionForHR(typeInfo.GetDocumentation(-1, out string? name, out documentation, out _, out _));
            return name ?? throw new TypeLibraryException("A type in the type library has no name.");
        }

        private static string GetMemberName(ITypeInfo typeInfo, int memberId, out string? documentation)
        {
            Marshal.ThrowExceptionForHR(typeInfo.GetDocumentation(memberId, out string? name, out documentation, out _, out _));
            return name ?? $"Member{memberId:X}";
        }

        private static string[] GetNames(ITypeInfo typeInfo, int memberId, int maxNames)
        {
            nint* names = stackalloc nint[maxNames];
            typeInfo.GetNames(memberId, names, (uint)maxNames, out uint count);
            var result = new string[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = names[i] == 0 ? string.Empty : Marshal.PtrToStringBSTR(names[i]);
                Marshal.FreeBSTR(names[i]);
            }

            return result;
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Reading {Kind} '{Name}'")]
        private partial void LogReadingType(Model.TypeKind kind, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "Importing {Kind} '{Name}' from type library '{Library}'")]
        private partial void LogImportingType(Model.TypeKind kind, string name, string library);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping '{Name}', which is mapped to {Type}")]
        private partial void LogSkippingWellKnown(string name, TypeReference type);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Interface '{Interface}': method '{Method}' is expected in vtable slot {Expected}, but it is in slot {Actual}")]
        private partial void LogVtableGap(string @interface, string method, int expected, int actual);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Constants of type {VarType} are not supported")]
        private partial void LogUnsupportedConstant(VarEnum varType);

        private delegate void FuncAction(FUNCDESC* func);

        private delegate void VarAction(VARDESC* var);

        private readonly record struct TypeKey(Guid LibraryId, uint Index);
    }
}

/// <summary>
/// The exception thrown when a type library cannot be read.
/// </summary>
public sealed class TypeLibraryException : CsComException
{
    /// <summary>Initializes a new instance of the <see cref="TypeLibraryException"/> class.</summary>
    public TypeLibraryException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TypeLibraryException"/> class.</summary>
    public TypeLibraryException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
