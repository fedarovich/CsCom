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
using INVOKEKIND = System.Runtime.InteropServices.ComTypes.INVOKEKIND;
using PARAMFLAG = System.Runtime.InteropServices.ComTypes.PARAMFLAG;
using SYSKIND = System.Runtime.InteropServices.ComTypes.SYSKIND;
using TYPEFLAGS = System.Runtime.InteropServices.ComTypes.TYPEFLAGS;
using TYPEKIND = System.Runtime.InteropServices.ComTypes.TYPEKIND;
using VARFLAGS = System.Runtime.InteropServices.ComTypes.VARFLAGS;
using VARKIND = System.Runtime.InteropServices.ComTypes.VARKIND;

namespace CsCom.TypeLibraries;

/// <summary>
/// The platform that a type library is written for, which determines the sizes of the pointers in the layout of its types.
/// </summary>
public enum TypeLibraryPlatform
{
    /// <summary>32-bit Windows (<c>SYS_WIN32</c>).</summary>
    Win32,

    /// <summary>64-bit Windows (<c>SYS_WIN64</c>).</summary>
    Win64,
}

/// <summary>
/// The options of a <see cref="TypeLibraryWriter"/>.
/// </summary>
public sealed class TypeLibraryWriterOptions
{
    /// <summary>The platform of the type library.</summary>
    public TypeLibraryPlatform Platform { get; init; } = TypeLibraryPlatform.Win64;

    /// <summary>
    /// The files of type libraries that the types of the library are imported from. A library that is not among them is
    /// looked up in the registry.
    /// </summary>
    public IReadOnlyList<string> TypeLibraryPaths { get; init; } = [];
}

/// <summary>
/// Writes a <see cref="TypeLibrary"/> model to a type library file (<c>.tlb</c>).
/// </summary>
/// <remarks>
/// The types that the model imports from other libraries (<see cref="TypeDefinition.ImportedFrom"/>) are not written: the
/// type library refers to them in their libraries, which have to be available. The layouts of the structures and of the
/// vtables are computed for the platform, so <see cref="RecordDefinition.Size"/>, <see cref="FieldDefinition.Offset"/> and
/// <see cref="MethodDefinition.VtableSlot"/> are ignored.
/// </remarks>
public sealed partial class TypeLibraryWriter
{
    private static readonly Guid StdOleLibraryId = new("00020430-0000-0000-C000-000000000046");

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeLibraryWriter"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public TypeLibraryWriter(ILogger<TypeLibraryWriter>? logger = null)
    {
        _logger = logger ?? NullLogger<TypeLibraryWriter>.Instance;
    }

    /// <summary>
    /// Writes the type library.
    /// </summary>
    /// <param name="library">The model.</param>
    /// <param name="path">The path of the file, which is replaced if it exists.</param>
    /// <param name="options">The options.</param>
    /// <returns>The files of the type libraries that the written library imports types from.</returns>
    /// <exception cref="TypeLibraryException">The type library cannot be written.</exception>
    public IReadOnlyList<string> Write(TypeLibrary library, string path, TypeLibraryWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Type libraries can only be written on Windows.");
        }

        if (library.Guid == StdOleLibraryId)
        {
            throw new TypeLibraryException(
                $"The type library '{library.Name}' cannot be written: the model does not hold its types IUnknown, IDispatch, IEnumVARIANT and GUID, which every type library refers to in it.");
        }

        options ??= new TypeLibraryWriterOptions();
        string fullPath = System.IO.Path.GetFullPath(path);
        if (System.IO.Path.GetDirectoryName(fullPath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        // OLE Automation keeps the type libraries that a process loaded by their paths, and a loaded file cannot be
        // replaced in place, so the library is written to a file of its own and moved.
        string temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        LogWriting(fullPath);
        try
        {
            IReadOnlyList<string> imported;
            using (var session = new Session(library, options, _logger))
            {
                imported = session.Write(temporaryPath);
            }

            try
            {
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new TypeLibraryException($"The type library '{fullPath}' cannot be replaced: {e.Message}", e);
            }

            return imported;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file is left in place when another process holds it.
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Writing type library '{Path}'")]
    private partial void LogWriting(string path);

    private sealed unsafe partial class Session : IDisposable
    {
        private const int MemberIdNil = -1;

        // The identifiers that MIDL gives the fields of structures and the members of enumerations.
        private const int FirstVariableId = 0x40000000;

        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
        private static readonly Guid IID_IEnumVARIANT = new("00020404-0000-0000-C000-000000000046");
        private static readonly TypeLibraryReference StdOle = new("stdole", StdOleLibraryId, 2, 0);

        private readonly TypeLibrary _library;
        private readonly TypeLibraryWriterOptions _options;
        private readonly ILogger _logger;
        private readonly List<TypeDefinition> _types;
        private readonly Dictionary<TypeDefinition, ICreateTypeInfo> _created = [];
        private readonly Dictionary<(TypeDefinition Owner, object Target, bool Vtable), uint> _references = [];
        private readonly Dictionary<Guid, ImportedLibrary> _imported = [];
        private readonly Dictionary<Guid, string> _libraryPaths = [];
        private readonly HashSet<TypeDefinition> _laidOut = [];
        private readonly List<nint> _allocations = [];
        private readonly List<nint> _variants = [];
        private readonly List<object> _comObjects = [];

        public Session(TypeLibrary library, TypeLibraryWriterOptions options, ILogger logger)
        {
            _library = library;
            _options = options;
            _logger = logger;
            _types = [.. library.Types.Where(static t => t.ImportedFrom is null)];
        }

        public IReadOnlyList<string> Write(string path)
        {
            IndexTypeLibraryPaths();

            SYSKIND kind = _options.Platform == TypeLibraryPlatform.Win32 ? SYSKIND.SYS_WIN32 : SYSKIND.SYS_WIN64;
            int hr = OleAut32.CreateTypeLib2(kind, path, out ICreateTypeLib? typeLib);
            if (hr < 0 || typeLib is null)
            {
                throw new TypeLibraryException($"Failed to create the type library '{path}' (HRESULT 0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
            }

            _comObjects.Add(typeLib);
            try
            {
                typeLib.SetName(_library.Name);
                typeLib.SetGuid(_library.Guid);
                typeLib.SetVersion(_library.MajorVersion, _library.MinorVersion);
                typeLib.SetLcid(_library.Lcid);
                if (!string.IsNullOrEmpty(_library.Documentation))
                {
                    typeLib.SetDocString(_library.Documentation);
                }
            }
            catch (COMException e)
            {
                throw new TypeLibraryException($"Failed to write the attributes of the type library '{_library.Name}' (HRESULT 0x{e.HResult:X8}).", e);
            }

            // All the types are created before their members are added, which refer to types that follow them.
            foreach (var type in _types)
            {
                Guard(type, () => Create(typeLib, type));
            }

            foreach (var type in _types)
            {
                LogWritingType(type.Kind, type.OriginalName);
                Guard(type, () => Fill(type));
                ReleaseTemporaries();
            }

            foreach (var type in _types)
            {
                LayOut(type);
            }

            try
            {
                typeLib.SaveAllChanges();
            }
            catch (COMException e)
            {
                throw new TypeLibraryException($"Failed to save the type library '{path}' (HRESULT 0x{e.HResult:X8}).", e);
            }

            return [.. _imported.Values.Where(static l => l.LibraryId != StdOleLibraryId).Select(static l => l.Path).Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        public void Dispose()
        {
            ReleaseTemporaries();

            // The objects are released now rather than by the garbage collector, because they keep the files open.
            foreach (object comObject in _comObjects)
            {
                if (comObject is ComObject wrapper)
                {
                    wrapper.FinalRelease();
                }
            }

            _comObjects.Clear();
        }

        private void Create(ICreateTypeLib typeLib, TypeDefinition type)
        {
            TYPEKIND kind = type switch
            {
                EnumDefinition => TYPEKIND.TKIND_ENUM,
                RecordDefinition { IsUnion: true } => TYPEKIND.TKIND_UNION,
                RecordDefinition => TYPEKIND.TKIND_RECORD,
                AliasDefinition => TYPEKIND.TKIND_ALIAS,
                InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } => TYPEKIND.TKIND_DISPATCH,
                InterfaceDefinition => TYPEKIND.TKIND_INTERFACE,
                CoClassDefinition => TYPEKIND.TKIND_COCLASS,
                ModuleDefinition => TYPEKIND.TKIND_MODULE,
                _ => throw new TypeLibraryException($"The type '{type.OriginalName}' has an unsupported kind {type.Kind}."),
            };

            typeLib.CreateTypeInfo(type.OriginalName, kind, out ICreateTypeInfo typeInfo);
            _comObjects.Add(typeInfo);
            _created.Add(type, typeInfo);

            if (type.Guid is { } guid)
            {
                typeInfo.SetGuid(guid);
            }

            var flags = (TYPEFLAGS)(ushort)type.Flags;
            if (type is InterfaceDefinition @interface)
            {
                // The flags of a dual interface make OLE Automation add the dispinterface to it.
                flags = @interface.InterfaceKind == InterfaceKind.Dual
                    ? flags | TYPEFLAGS.TYPEFLAG_FDUAL | TYPEFLAGS.TYPEFLAG_FOLEAUTOMATION
                    : flags & ~TYPEFLAGS.TYPEFLAG_FDUAL;
            }

            typeInfo.SetTypeFlags((uint)flags);
            if (!string.IsNullOrEmpty(type.Documentation))
            {
                typeInfo.SetDocString(type.Documentation);
            }
        }

        private void Fill(TypeDefinition type)
        {
            ICreateTypeInfo typeInfo = _created[type];
            switch (type)
            {
                case EnumDefinition @enum:
                    FillEnum(typeInfo, @enum);
                    break;
                case RecordDefinition record:
                    FillRecord(typeInfo, record);
                    break;
                case AliasDefinition alias:
                    FillAlias(typeInfo, alias);
                    break;
                case InterfaceDefinition @interface:
                    FillInterface(typeInfo, @interface);
                    break;
                case CoClassDefinition coClass:
                    FillCoClass(typeInfo, coClass);
                    break;
                case ModuleDefinition module:
                    FillModule(typeInfo, module);
                    break;
            }
        }

        // The layout of a type needs those of the types that it contains and of the interface that it derives from.
        private void LayOut(TypeDefinition type)
        {
            if (!_created.TryGetValue(type, out var typeInfo) || !_laidOut.Add(type))
            {
                return;
            }

            foreach (var dependency in GetLayoutDependencies(type))
            {
                LayOut(dependency);
            }

            Guard(type, typeInfo.LayOut);
        }

        private static IEnumerable<TypeDefinition> GetLayoutDependencies(TypeDefinition type)
        {
            switch (type)
            {
                case RecordDefinition record:
                    foreach (var field in record.Fields)
                    {
                        if (GetValueDefinition(field.Type) is { } definition)
                        {
                            yield return definition;
                        }
                    }

                    break;
                case AliasDefinition alias:
                    if (GetValueDefinition(alias.Target) is { } target)
                    {
                        yield return target;
                    }

                    break;
                case InterfaceDefinition { BaseInterface: { } baseInterface }:
                    if (GetValueDefinition(baseInterface) is { } baseDefinition)
                    {
                        yield return baseDefinition;
                    }

                    break;
            }
        }

        // The type that a value of a type contains, which a pointer to it does not.
        private static TypeDefinition? GetValueDefinition(TypeReference type) => type switch
        {
            UserDefinedTypeReference user => user.Definition,
            FixedArrayTypeReference array => GetValueDefinition(array.ElementType),
            _ => null,
        };

        private void FillEnum(ICreateTypeInfo typeInfo, EnumDefinition definition)
        {
            for (int i = 0; i < definition.Members.Count; i++)
            {
                var member = definition.Members[i];
                ComVariant* value = AllocateVariant(ComVariant.Create(unchecked((int)member.Value)));
                VARDESC variable = default;
                variable.memid = FirstVariableId + i;
                variable.varkind = VARKIND.VAR_CONST;
                variable.lpValue = (nint)value;
                variable.elemdescVar.tdesc.vt = (ushort)VarEnum.VT_INT;
                AddVariable(typeInfo, (uint)i, &variable, member.Name, member.Documentation);
            }
        }

        private void FillRecord(ICreateTypeInfo typeInfo, RecordDefinition definition)
        {
            int pointerSize = _options.Platform == TypeLibraryPlatform.Win32 ? 4 : 8;
            typeInfo.SetAlignment((ushort)(definition.Alignment > 0 ? definition.Alignment : pointerSize));
            for (int i = 0; i < definition.Fields.Count; i++)
            {
                var field = definition.Fields[i];
                VARDESC variable = default;
                variable.memid = FirstVariableId + i;
                variable.varkind = VARKIND.VAR_PERINSTANCE;
                FillTypeDesc(definition, field.Type, &variable.elemdescVar.tdesc);
                AddVariable(typeInfo, (uint)i, &variable, field.Name, field.Documentation);
            }
        }

        private void FillAlias(ICreateTypeInfo typeInfo, AliasDefinition definition)
        {
            TYPEDESC target;
            FillTypeDesc(definition, definition.Target, &target);
            typeInfo.SetTypeDescAlias(&target);
        }

        private void FillInterface(ICreateTypeInfo typeInfo, InterfaceDefinition definition)
        {
            bool isDispatch = definition.InterfaceKind == InterfaceKind.Dispatch;
            uint baseReference = definition.BaseInterface switch
            {
                _ when isDispatch => GetStdOleReference(definition, IID_IDispatch),
                null or PrimitiveTypeReference { VarType: VarEnum.VT_UNKNOWN } => GetStdOleReference(definition, IID_IUnknown),
                PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => GetStdOleReference(definition, IID_IDispatch),
                WellKnownTypeReference { Type: WellKnownType.IEnumVariant } => GetStdOleReference(definition, IID_IEnumVARIANT),
                UserDefinedTypeReference { Definition: InterfaceDefinition baseDefinition } => GetReference(definition, baseDefinition, vtable: true),
                _ => throw new TypeLibraryException($"The interface '{definition.OriginalName}' derives from '{definition.BaseInterface}', which is not an interface."),
            };
            typeInfo.AddImplType(0, baseReference);

            for (int i = 0; i < definition.Methods.Count; i++)
            {
                AddFunction(typeInfo, definition, (uint)i, definition.Methods[i], isDispatch ? FUNCKIND.FUNC_DISPATCH : FUNCKIND.FUNC_PUREVIRTUAL);
            }

            for (int i = 0; i < definition.Properties.Count; i++)
            {
                var property = definition.Properties[i];
                VARDESC variable = default;
                variable.memid = property.DispId;
                variable.varkind = VARKIND.VAR_DISPATCH;
                variable.wVarFlags = MapVarFlags(property.Flags, property.IsReadOnly);
                FillTypeDesc(definition, property.Type, &variable.elemdescVar.tdesc);
                AddVariable(typeInfo, (uint)i, &variable, property.Name, property.Documentation);
            }
        }

        private void FillCoClass(ICreateTypeInfo typeInfo, CoClassDefinition definition)
        {
            for (int i = 0; i < definition.Interfaces.Count; i++)
            {
                var implemented = definition.Interfaces[i];
                uint reference = implemented.Interface switch
                {
                    PrimitiveTypeReference { VarType: VarEnum.VT_UNKNOWN } => GetStdOleReference(definition, IID_IUnknown),
                    PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => GetStdOleReference(definition, IID_IDispatch),
                    WellKnownTypeReference { Type: WellKnownType.IEnumVariant } => GetStdOleReference(definition, IID_IEnumVARIANT),
                    UserDefinedTypeReference { Definition: InterfaceDefinition @interface } => GetReference(definition, @interface, vtable: false),
                    _ => throw new TypeLibraryException($"The class '{definition.OriginalName}' implements '{implemented.Interface}', which is not an interface."),
                };

                var flags = (implemented.IsDefault ? IMPLTYPEFLAGS.IMPLTYPEFLAG_FDEFAULT : 0)
                    | (implemented.IsSource ? IMPLTYPEFLAGS.IMPLTYPEFLAG_FSOURCE : 0)
                    | (implemented.IsRestricted ? IMPLTYPEFLAGS.IMPLTYPEFLAG_FRESTRICTED : 0);
                typeInfo.AddImplType((uint)i, reference);
                typeInfo.SetImplTypeFlags((uint)i, (int)flags);
            }
        }

        private void FillModule(ICreateTypeInfo typeInfo, ModuleDefinition definition)
        {
            for (int i = 0; i < definition.Functions.Count; i++)
            {
                var function = definition.Functions[i];
                AddFunction(typeInfo, definition, (uint)i, function, FUNCKIND.FUNC_STATIC);
                if (function.DllName is null)
                {
                    continue;
                }

                char* dllName = AllocateString(function.DllName);
                char* entryPoint = function.EntryPoint is { } name ? AllocateString(name) : (char*)(nuint)(function.EntryPointOrdinal ?? 0);
                typeInfo.DefineFuncAsDllEntry((uint)i, dllName, entryPoint);
            }

            for (int i = 0; i < definition.Constants.Count; i++)
            {
                var constant = definition.Constants[i];
                VARDESC variable = default;
                variable.memid = FirstVariableId + i;
                variable.varkind = VARKIND.VAR_CONST;
                variable.lpValue = (nint)AllocateVariant(CreateVariant(constant.Value, constant.Type));
                FillTypeDesc(definition, constant.Type, &variable.elemdescVar.tdesc);
                AddVariable(typeInfo, (uint)i, &variable, constant.Name, constant.Documentation);
            }
        }

        private static void AddVariable(ICreateTypeInfo typeInfo, uint index, VARDESC* variable, string name, string? documentation)
        {
            typeInfo.AddVarDesc(index, variable);
            typeInfo.SetVarName(index, name);
            if (!string.IsNullOrEmpty(documentation))
            {
                typeInfo.SetVarDocString(index, documentation);
            }
        }

        private void AddFunction(ICreateTypeInfo typeInfo, TypeDefinition owner, uint index, MethodDefinition method, FUNCKIND kind)
        {
            int count = method.Parameters.Count;
            ELEMDESC* parameters = (ELEMDESC*)Allocate(sizeof(ELEMDESC) * Math.Max(count, 1));
            int optional = 0;
            for (int i = 0; i < count; i++)
            {
                var parameter = method.Parameters[i];
                FillTypeDesc(owner, parameter.Type, &parameters[i].tdesc);
                var flags = (PARAMFLAG)((int)parameter.Flags & 0x3F);
                if (parameter.Flags.HasFlag(ParameterFlags.HasDefault))
                {
                    // A default value makes the parameter optional, and is not counted among the optional VARIANTs.
                    flags |= PARAMFLAG.PARAMFLAG_FOPT;
                    PARAMDESCEX* extension = (PARAMDESCEX*)Allocate(sizeof(PARAMDESCEX));
                    extension->cBytes = (uint)sizeof(PARAMDESCEX);
                    extension->varDefaultValue = CreateVariant(parameter.DefaultValue, parameter.Type);
                    _variants.Add((nint)(&extension->varDefaultValue));
                    parameters[i].paramdesc.pparamdescex = extension;
                }
                else if (parameter.Flags.HasFlag(ParameterFlags.Optional))
                {
                    optional++;
                }

                parameters[i].paramdesc.wParamFlags = flags;
            }

            FUNCDESC function = default;
            function.memid = method.MemberId;
            function.funckind = kind;
            function.invkind = (INVOKEKIND)method.InvokeKind;
            function.callconv = method.CallingConvention == CallingConvention.Cdecl ? CALLCONV.CC_CDECL : CALLCONV.CC_STDCALL;
            function.cParams = (short)count;
            function.cParamsOpt = (short)optional;
            function.lprgelemdescParam = parameters;
            function.wFuncFlags = (FUNCFLAGS)((int)method.Flags & 0xFFFF);
            FillTypeDesc(owner, method.ReturnType, &function.elemdescFunc.tdesc);
            try
            {
                typeInfo.AddFuncDesc(index, &function);
            }
            catch (COMException e)
            {
                throw new TypeLibraryException(
                    $"Failed to write the member '{owner.OriginalName}.{method.OriginalName}' to the type library (HRESULT 0x{e.HResult:X8}): {e.Message}", e);
            }

            // The value of a property setter, its last parameter, has no name.
            int named = method.InvokeKind is InvokeKind.PropertyPut or InvokeKind.PropertyPutRef ? Math.Max(count - 1, 0) : count;
            char** names = (char**)Allocate(sizeof(char*) * (named + 1));
            names[0] = AllocateString(method.OriginalName);
            for (int i = 0; i < named; i++)
            {
                names[i + 1] = AllocateString(method.Parameters[i].Name);
            }

            typeInfo.SetFuncAndParamNames(index, names, (uint)(named + 1));
            if (!string.IsNullOrEmpty(method.Documentation))
            {
                typeInfo.SetFuncDocString(index, method.Documentation);
            }
        }

        private void FillTypeDesc(TypeDefinition owner, TypeReference type, TYPEDESC* desc)
        {
            *desc = default;
            switch (type)
            {
                case PrimitiveTypeReference primitive:
                    desc->vt = (ushort)primitive.VarType;
                    break;
                case PointerTypeReference pointer:
                    desc->vt = (ushort)VarEnum.VT_PTR;
                    desc->lpValue = Allocate(sizeof(TYPEDESC));
                    FillTypeDesc(owner, pointer.ElementType, desc->lptdesc);
                    break;
                case SafeArrayTypeReference array:
                    desc->vt = (ushort)VarEnum.VT_SAFEARRAY;
                    desc->lpValue = Allocate(sizeof(TYPEDESC));
                    FillTypeDesc(owner, array.ElementType, desc->lptdesc);
                    break;
                case FixedArrayTypeReference array:
                {
                    int dimensions = array.Dimensions.Count;
                    ARRAYDESC* arrayDesc = (ARRAYDESC*)Allocate(sizeof(ARRAYDESC) + (sizeof(SAFEARRAYBOUND) * Math.Max(dimensions - 1, 0)));
                    arrayDesc->cDims = (ushort)dimensions;
                    SAFEARRAYBOUND* bounds = ARRAYDESC.Bounds(arrayDesc);
                    for (int i = 0; i < dimensions; i++)
                    {
                        bounds[i].cElements = (uint)array.Dimensions[i];
                    }

                    FillTypeDesc(owner, array.ElementType, &arrayDesc->tdescElem);
                    desc->vt = (ushort)VarEnum.VT_CARRAY;
                    desc->lpValue = (nint)arrayDesc;
                    break;
                }

                case UserDefinedTypeReference user:
                    desc->vt = (ushort)VarEnum.VT_USERDEFINED;
                    desc->lpValue = (nint)GetReference(owner, user.Definition, vtable: false);
                    break;
                case WellKnownTypeReference { Type: WellKnownType.Guid }:
                    desc->vt = (ushort)VarEnum.VT_USERDEFINED;
                    desc->lpValue = (nint)GetStdOleReference(owner, "GUID");
                    break;
                case WellKnownTypeReference { Type: WellKnownType.IEnumVariant }:
                    desc->vt = (ushort)VarEnum.VT_USERDEFINED;
                    desc->lpValue = (nint)GetStdOleReference(owner, IID_IEnumVARIANT);
                    break;
                default:
                    throw new TypeLibraryException($"The type '{type}' that '{owner.OriginalName}' uses cannot be written to a type library.");
            }
        }

        private static VARFLAGS MapVarFlags(MemberFlags flags, bool isReadOnly)
        {
            VARFLAGS result = 0;
            Map(MemberFlags.Restricted, VARFLAGS.VARFLAG_FRESTRICTED);
            Map(MemberFlags.Source, VARFLAGS.VARFLAG_FSOURCE);
            Map(MemberFlags.Bindable, VARFLAGS.VARFLAG_FBINDABLE);
            Map(MemberFlags.Hidden, VARFLAGS.VARFLAG_FHIDDEN);
            Map(MemberFlags.DefaultCollectionElement, VARFLAGS.VARFLAG_FDEFAULTCOLLELEM);
            Map(MemberFlags.UiDefault, VARFLAGS.VARFLAG_FUIDEFAULT);
            Map(MemberFlags.NonBrowsable, VARFLAGS.VARFLAG_FNONBROWSABLE);
            Map(MemberFlags.ReadOnly, VARFLAGS.VARFLAG_FREADONLY);
            return isReadOnly ? result | VARFLAGS.VARFLAG_FREADONLY : result;

            void Map(MemberFlags flag, VARFLAGS mapped)
            {
                if (flags.HasFlag(flag))
                {
                    result |= mapped;
                }
            }
        }

        // The VARIANT of a constant or of a default value has the type of the constant or of the parameter, which the value
        // of the model does not tell: a CURRENCY and a DECIMAL are both decimals, and an enumeration member is a number.
        private ComVariant CreateVariant(object? value, TypeReference type)
        {
            if (value is null)
            {
                return default;
            }

            VarEnum vt = GetVariantType(type);
            switch (vt)
            {
                case VarEnum.VT_BOOL:
                    return ComVariant.Create(Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture));
                case VarEnum.VT_CY:
                    return ComVariant.CreateRaw(VarEnum.VT_CY, decimal.ToOACurrency(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)));
                case VarEnum.VT_DATE:
                    return ComVariant.CreateRaw(VarEnum.VT_DATE, value is DateTime date ? date.ToOADate() : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                case VarEnum.VT_ERROR or VarEnum.VT_HRESULT when value is not string:
                    return ComVariant.CreateRaw(VarEnum.VT_ERROR, unchecked((int)ToInt64(value)));
                case VarEnum.VT_I1 when value is not string:
                    return ComVariant.CreateRaw(vt, unchecked((sbyte)ToInt64(value)));
                case VarEnum.VT_UI1 when value is not string:
                    return ComVariant.CreateRaw(vt, unchecked((byte)ToInt64(value)));
                case VarEnum.VT_I2 when value is not string:
                    return ComVariant.CreateRaw(vt, unchecked((short)ToInt64(value)));
                case VarEnum.VT_UI2 when value is not string:
                    return ComVariant.CreateRaw(vt, unchecked((ushort)ToInt64(value)));
                case VarEnum.VT_I4 or VarEnum.VT_INT when value is not string:
                    return ComVariant.CreateRaw(VarEnum.VT_I4, unchecked((int)ToInt64(value)));
                case VarEnum.VT_UI4 or VarEnum.VT_UINT when value is not string:
                    return ComVariant.CreateRaw(VarEnum.VT_UI4, unchecked((uint)ToInt64(value)));
                case VarEnum.VT_I8 when value is not string:
                    return ComVariant.CreateRaw(vt, ToInt64(value));
                case VarEnum.VT_UI8 when value is not string:
                    return ComVariant.CreateRaw(vt, unchecked((ulong)ToInt64(value)));
                // A floating-point value keeps its own precision, which a conversion would show as more digits.
                case VarEnum.VT_R4 when value is not (string or float or double):
                    return ComVariant.CreateRaw(vt, Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture));
                case VarEnum.VT_R8 when value is not (string or float or double):
                    return ComVariant.CreateRaw(vt, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                case VarEnum.VT_DECIMAL when value is not string:
                    return ComVariant.Create(Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture));
            }

            switch (value)
            {
                case string text:
                    return ComVariant.Create(text);
                case bool flag:
                    return ComVariant.Create(flag);
                case sbyte number:
                    return ComVariant.Create(number);
                case byte number:
                    return ComVariant.Create(number);
                case short number:
                    return ComVariant.Create(number);
                case ushort number:
                    return ComVariant.Create(number);
                case int number:
                    return ComVariant.Create(number);
                case uint number:
                    return ComVariant.Create(number);
                case long number:
                    return ComVariant.Create(number);
                case ulong number:
                    return ComVariant.Create(number);
                case float number:
                    return ComVariant.Create(number);
                case double number:
                    return ComVariant.Create(number);
                case decimal number:
                    return ComVariant.Create(number);
                case DateTime date:
                    return ComVariant.CreateRaw(VarEnum.VT_DATE, date.ToOADate());
                default:
                    LogUnsupportedValue(value.GetType().Name);
                    return default;
            }
        }

        private static VarEnum GetVariantType(TypeReference type) => type switch
        {
            PrimitiveTypeReference primitive => primitive.VarType,
            UserDefinedTypeReference { Definition: EnumDefinition } => VarEnum.VT_I4,
            UserDefinedTypeReference { Definition: AliasDefinition alias } => GetVariantType(alias.Target),
            _ => VarEnum.VT_EMPTY,
        };

        private static long ToInt64(object value) => value switch
        {
            ulong number => unchecked((long)number),
            bool flag => flag ? 1 : 0,
            _ => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        };

        private void Guard(TypeDefinition type, Action action)
        {
            try
            {
                action();
            }
            catch (COMException e)
            {
                throw new TypeLibraryException(
                    $"Failed to write the {type.Kind.ToString().ToLowerInvariant()} '{type.OriginalName}' to the type library (HRESULT 0x{e.HResult:X8}): {e.Message}", e);
            }
        }

        private nint Allocate(int size)
        {
            nint memory = (nint)NativeMemory.AllocZeroed((nuint)size);
            _allocations.Add(memory);
            return memory;
        }

        private char* AllocateString(string text)
        {
            char* memory = (char*)Allocate(sizeof(char) * (text.Length + 1));
            text.CopyTo(new Span<char>(memory, text.Length));
            return memory;
        }

        private ComVariant* AllocateVariant(ComVariant value)
        {
            ComVariant* memory = (ComVariant*)Allocate(sizeof(ComVariant));
            *memory = value;
            _variants.Add((nint)memory);
            return memory;
        }

        // Frees the descriptions of the members of a type, which OLE Automation copied.
        private void ReleaseTemporaries()
        {
            foreach (nint variant in _variants)
            {
                ((ComVariant*)variant)->Dispose();
            }

            _variants.Clear();
            foreach (nint memory in _allocations)
            {
                NativeMemory.Free((void*)memory);
            }

            _allocations.Clear();
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Writing {Kind} '{Name}'")]
        private partial void LogWritingType(Model.TypeKind kind, string name);

        [LoggerMessage(Level = LogLevel.Debug, Message = "The type library refers to the types of '{Library}' in '{Path}'")]
        private partial void LogImportedLibrary(string library, string path);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "The type library refers to the types of '{Library}', which is not registered: a program only loads these types where '{Library}' is registered or its file '{File}' is in the current directory or on the path")]
        private partial void LogUnregisteredImport(string library, string file);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Values of type {Type} cannot be written to a type library")]
        private partial void LogUnsupportedValue(string type);
    }
}
