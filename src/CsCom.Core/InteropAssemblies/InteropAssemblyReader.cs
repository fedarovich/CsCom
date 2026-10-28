using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using FieldDefinition = CsCom.Model.FieldDefinition;
using ModuleDefinition = CsCom.Model.ModuleDefinition;
using TypeDefinition = CsCom.Model.TypeDefinition;

namespace CsCom.InteropAssemblies;

/// <summary>
/// Reads a .NET interop assembly into the <see cref="TypeLibrary"/> model: an assembly imported from a type library, such as
/// a primary interop assembly or the output of TlbImp, or an assembly with <c>[ComImport]</c> declarations. The metadata is
/// read with <see cref="MetadataLoadContext"/>, so the assembly and its references are not loaded for execution.
/// </summary>
/// <remarks>
/// The model is rebuilt from the conventions of TlbImp: <c>[retval]</c> parameters from the return values of the methods that
/// return <c>HRESULT</c>s, <c>[lcid]</c> parameters from <see cref="LCIDConversionAttribute"/>, coclasses from the <c>XClass</c>
/// classes and their <see cref="ComSourceInterfacesAttribute"/>, and the native types from the marshalling information. The
/// documentation, the functions of modules and the aliases are not stored in interop assemblies, so they are lost.
/// </remarks>
public sealed partial class InteropAssemblyReader
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InteropAssemblyReader"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="referencePaths">Assemblies or directories that contain the assemblies referenced by the interop assembly.</param>
    public InteropAssemblyReader(ILogger<InteropAssemblyReader>? logger = null, IReadOnlyList<string>? referencePaths = null)
    {
        _logger = logger ?? NullLogger<InteropAssemblyReader>.Instance;
        ReferencePaths = referencePaths ?? [];
    }

    /// <summary>
    /// Gets the assemblies or directories that contain the assemblies referenced by the interop assembly. The directory of
    /// the interop assembly, the .NET runtime and the global assembly caches of .NET Framework are searched as well.
    /// </summary>
    public IReadOnlyList<string> ReferencePaths { get; }

    /// <summary>
    /// Reads the interop assembly.
    /// </summary>
    /// <param name="path">The path to the assembly.</param>
    /// <returns>The model of the types of the assembly.</returns>
    /// <exception cref="CsComException">The assembly or one of the assemblies it references cannot be loaded.</exception>
    public TypeLibrary Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string fullPath = Path.GetFullPath(path);
        var resolver = new InteropAssemblyResolver(fullPath, ReferencePaths);
        using var context = new MetadataLoadContext(resolver);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(fullPath);
        }
        catch (Exception e) when (e is BadImageFormatException or FileNotFoundException)
        {
            throw new CsComException($"'{path}' is not a .NET assembly: {e.Message}", e);
        }

        LogLoading(fullPath);
        try
        {
            using var session = new Session(context, assembly, fullPath, _logger);
            return session.Read();
        }
        catch (FileNotFoundException e) when (resolver.Missing is { } missing)
        {
            throw new CsComException(
                $"The assembly '{missing.FullName}', which '{Path.GetFileName(fullPath)}' references, was not found. Add the directory that contains it with --reference-path.",
                e);
        }
    }

    /// <summary>
    /// Gets the name, identifier and version of the type library an assembly was imported from: <c>[ImportedFromTypeLib]</c>,
    /// the <c>[Guid]</c> of the assembly and <c>[TypeLibVersion]</c> or <c>[PrimaryInteropAssembly]</c>, otherwise the name and
    /// version of the assembly.
    /// </summary>
    internal static TypeLibraryReference GetLibrary(Assembly assembly)
    {
        var attributes = assembly.GetCustomAttributesData();
        string name = GetArgument<string>(attributes, "System.Runtime.InteropServices.ImportedFromTypeLibAttribute") ?? assembly.GetName().Name!;
        Guid guid = GetArgument<string>(attributes, "System.Runtime.InteropServices.GuidAttribute") is { } text && Guid.TryParse(text, out var parsed) ? parsed : Guid.Empty;
        var version = Find(attributes, "System.Runtime.InteropServices.TypeLibVersionAttribute") ?? Find(attributes, "System.Runtime.InteropServices.PrimaryInteropAssemblyAttribute");
        (ushort major, ushort minor) = version is { ConstructorArguments: [var majorArgument, var minorArgument] }
            ? ((ushort)(int)majorArgument.Value!, (ushort)(int)minorArgument.Value!)
            : ((ushort)(assembly.GetName().Version?.Major ?? 0), (ushort)(assembly.GetName().Version?.Minor ?? 0));
        return new TypeLibraryReference(name, guid, major, minor);
    }

    private static CustomAttributeData? Find(IList<CustomAttributeData> attributes, string fullName) =>
        attributes.FirstOrDefault(a => a.AttributeType.FullName == fullName);

    private static T? GetArgument<T>(IList<CustomAttributeData> attributes, string fullName) =>
        Find(attributes, fullName) is { ConstructorArguments: [var argument, ..] } && argument.Value is T value ? value : default;

    [LoggerMessage(Level = LogLevel.Information, Message = "Reading interop assembly '{Path}'")]
    private partial void LogLoading(string path);

    // The positions of types, which determine the default marshalling of some of them.
    private enum TypePosition
    {
        Parameter,
        Return,
        Field,
        ArrayElement,
    }

    private sealed partial class Session : IDisposable
    {
        private const int DispIdNewEnum = -4;
        private const int FirstMemberId = 0x60000000;
        private const short TypeLibTypeDispatchable = 0x1000;
        private const string InteropNamespace = "System.Runtime.InteropServices.";

        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
        private static readonly Guid IID_IEnumVARIANT = new("00020404-0000-0000-C000-000000000046");

        private readonly MetadataLoadContext _context;
        private readonly Assembly _assembly;
        private readonly string _path;
        private readonly ILogger _logger;
        private readonly bool _importedFromTypeLibrary;
        private readonly Dictionary<Type, TypeDefinition> _definitions = [];
        private readonly Dictionary<Assembly, TypeLibraryReference> _importedLibraries = [];
        private readonly Dictionary<Assembly, Dictionary<Type, Type>> _coClassInterfaces = [];
        private readonly Dictionary<Assembly, PEReader> _peReaders = [];
        private readonly Dictionary<Type, RecordLayout> _layouts = [];
        private readonly Dictionary<Type, int> _vtableSizes = [];
        private readonly Queue<(Type Type, TypeDefinition Definition)> _pending = [];
        private readonly Dictionary<VarEnum, PrimitiveTypeReference> _primitives = [];
        private TypeLibrary _library = null!;

        public Session(MetadataLoadContext context, Assembly assembly, string path, ILogger logger)
        {
            _context = context;
            _assembly = assembly;
            _path = path;
            _logger = logger;
            _importedFromTypeLibrary = Find(assembly.GetCustomAttributesData(), "System.Runtime.InteropServices.ImportedFromTypeLibAttribute") is not null;
        }

        public TypeLibrary Read()
        {
            var library = GetLibrary(_assembly);
            _library = new TypeLibrary
            {
                Name = library.Name,
                Guid = library.Guid,
                MajorVersion = library.MajorVersion,
                MinorVersion = library.MinorVersion,
                Path = _path,
            };

            RejectEmbeddedInteropTypes();
            foreach (Type type in GetTypes(_assembly))
            {
                if (IsLibraryType(type))
                {
                    GetOrCreateDefinition(type);
                }
            }

            while (_pending.TryDequeue(out var item))
            {
                Populate(item.Type, item.Definition);
            }

            // Keep the order of the assembly: its own types first, then the imported ones in the order they were encountered.
            var ordered = _library.Types.Where(static t => t.ImportedFrom is null).Concat(_library.Types.Where(static t => t.ImportedFrom is not null)).ToList();
            _library.Types.Clear();
            _library.Types.AddRange(ordered);
            _library.ImportedLibraries.AddRange(_importedLibraries.Values);
            return _library;
        }

        public void Dispose()
        {
            foreach (var reader in _peReaders.Values)
            {
                reader.Dispose();
            }
        }

        private static IEnumerable<Type> GetTypes(Assembly assembly) => assembly.GetTypes().OrderBy(static t => t.MetadataToken);

        private static bool IsComImport(Type type) => (type.Attributes & TypeAttributes.Import) != 0;

        // Assemblies compiled with "Embed Interop Types" contain the COM types they use with only the members they use, and
        // placeholders (_VtblGap...) for the vtable slots of the others.
        private void RejectEmbeddedInteropTypes()
        {
            var embedded = GetTypes(_assembly)
                .Where(static t => IsComImport(t)
                    && (Find(t.GetCustomAttributesData(), InteropNamespace + "TypeIdentifierAttribute") is not null
                        || (t.IsInterface && GetDeclaredMethods(t).Any(static m => m.Name.StartsWith("_VtblGap", StringComparison.Ordinal)))))
                .Select(static t => t.Name)
                .ToList();
            if (embedded.Count > 0)
            {
                throw new CsComException(
                    $"'{Path.GetFileName(_path)}' contains embedded interop types (Embed Interop Types), which have only the members its code uses: "
                    + $"generate from the primary interop assembly or the type library of {string.Join(", ", embedded.Take(3))}{(embedded.Count > 3 ? ", ..." : string.Empty)}.");
            }
        }

        // The types of assemblies imported from type libraries are all generated; otherwise, only the COM types and the types
        // they use are. Hand-written COM types are often nested in other classes, e.g. NativeMethods.
        private bool IsLibraryType(Type type)
        {
            if (type.IsGenericTypeDefinition || (type.IsNested && !IsComImport(type)))
            {
                return false;
            }

            if (type.IsInterface)
            {
                if (!IsComImport(type))
                {
                    if (Find(type.GetCustomAttributesData(), "System.Runtime.InteropServices.Marshalling.GeneratedComInterfaceAttribute") is not null)
                    {
                        LogSkippingGeneratedInterface(type.Name);
                    }

                    return false;
                }

                return Find(type.GetCustomAttributesData(), InteropNamespace + "CoClassAttribute") is null && GetInterfaceKind(type) is not null;
            }

            // TlbImp also imports the anonymous types of MIDL (__MIDL___MIDL_itf_...) under their typedef names, so they are
            // only read when they are used.
            if (type.IsValueType)
            {
                return _importedFromTypeLibrary && type.IsPublic && !type.Name.StartsWith("__MIDL", StringComparison.Ordinal);
            }

            return IsComImport(type) || (_importedFromTypeLibrary && IsModule(type));
        }

        // TlbImp imports the constants of a module into an abstract sealed class of literal fields.
        private static bool IsModule(Type type)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var fields = type.GetFields(all);
            return type is { IsClass: true, IsAbstract: true, IsSealed: true }
                && fields.Length > 0 && fields.All(static f => f.IsLiteral)
                && type.GetMethods(all).Length == 0;
        }

        private TypeDefinition GetOrCreateDefinition(Type type)
        {
            if (_definitions.TryGetValue(type, out var existing))
            {
                return existing;
            }

            TypeLibraryReference? importedFrom = type.Assembly == _assembly ? null : GetImportedLibrary(type.Assembly);
            var attributes = type.GetCustomAttributesData();
            string name = !type.IsValueType && !type.IsInterface && GetCoClassInterface(type) is { } coClassInterface ? coClassInterface.Name : type.Name;
            Guid? guid = GetArgument<string>(attributes, InteropNamespace + "GuidAttribute") is { } text && Guid.TryParse(text, out var parsed) ? parsed : null;
            var flags = Find(attributes, InteropNamespace + "TypeLibTypeAttribute") is { ConstructorArguments: [var flagsArgument] }
                ? (Model.TypeFlags)(ushort)Convert.ToInt16(flagsArgument.Value, CultureInfo.InvariantCulture)
                : Model.TypeFlags.None;

            TypeDefinition definition;
            if (type.IsEnum)
            {
                definition = new EnumDefinition { Name = name, OriginalName = name, Guid = guid, Flags = flags, ImportedFrom = importedFrom };
            }
            else if (type.IsValueType)
            {
                var layout = GetRecordLayout(type);
                definition = new RecordDefinition
                {
                    Name = name,
                    OriginalName = name,
                    Guid = guid,
                    Flags = flags,
                    ImportedFrom = importedFrom,
                    IsUnion = layout.IsUnion,
                    Size = layout.Size,
                    Alignment = layout.Alignment,
                };
            }
            else if (type.IsInterface)
            {
                // Dispinterfaces are dispatchable, also in the assemblies that do not record the flags of their types.
                if (flags == Model.TypeFlags.None && GetInterfaceKind(type) == InterfaceKind.Dispatch)
                {
                    flags = Model.TypeFlags.Dispatchable;
                }

                definition = new InterfaceDefinition { Name = name, OriginalName = name, Guid = guid, Flags = flags, ImportedFrom = importedFrom };
            }
            else if (IsComImport(type))
            {
                // Classes declared without a type library can be created.
                bool fromTypeLibrary = Find(type.Assembly.GetCustomAttributesData(), InteropNamespace + "ImportedFromTypeLibAttribute") is not null;
                definition = new CoClassDefinition
                {
                    Name = name,
                    OriginalName = name,
                    Guid = guid,
                    Flags = fromTypeLibrary ? flags : flags | Model.TypeFlags.CanCreate,
                    ImportedFrom = importedFrom,
                };
            }
            else
            {
                definition = new ModuleDefinition { Name = name, OriginalName = name, Guid = guid, Flags = flags, ImportedFrom = importedFrom };
            }

            _definitions.Add(type, definition);
            _library.Types.Add(definition);
            _pending.Enqueue((type, definition));
            if (importedFrom is not null)
            {
                LogImportingType(definition.Kind, name, importedFrom.Name);
            }

            return definition;
        }

        private TypeLibraryReference GetImportedLibrary(Assembly assembly)
        {
            if (!_importedLibraries.TryGetValue(assembly, out var reference))
            {
                reference = GetLibrary(assembly);
                _importedLibraries.Add(assembly, reference);
            }

            return reference;
        }

        private void Populate(Type type, TypeDefinition definition)
        {
            LogReadingType(definition.Kind, definition.Name);
            switch (definition)
            {
                case EnumDefinition enumDefinition:
                    PopulateEnum(type, enumDefinition);
                    break;
                case RecordDefinition recordDefinition:
                    PopulateRecord(type, recordDefinition);
                    break;
                case InterfaceDefinition interfaceDefinition:
                    PopulateInterface(type, interfaceDefinition);
                    break;
                case CoClassDefinition coClassDefinition:
                    PopulateCoClass(type, coClassDefinition);
                    break;
                case ModuleDefinition moduleDefinition:
                    PopulateModule(type, moduleDefinition);
                    break;
            }
        }

        private static void PopulateEnum(Type type, EnumDefinition definition)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(static f => f.IsLiteral).OrderBy(static f => f.MetadataToken))
            {
                definition.Members.Add(new EnumMember { Name = field.Name, Value = Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture) });
            }
        }

        private void PopulateRecord(Type type, RecordDefinition definition)
        {
            var layout = GetRecordLayout(type);
            var fields = GetInstanceFields(type);
            for (int i = 0; i < fields.Count; i++)
            {
                definition.Fields.Add(new FieldDefinition
                {
                    Name = fields[i].Name,
                    Type = MapType(fields[i].FieldType, GetDescriptor(fields[i]), TypePosition.Field, $"{type.Name}.{fields[i].Name}", type.StructLayoutAttribute?.CharSet ?? CharSet.Ansi),
                    Offset = layout.IsUnion ? 0 : layout.Offsets[i],
                });
            }
        }

        private void PopulateModule(Type type, ModuleDefinition definition)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).OrderBy(static f => f.MetadataToken))
            {
                definition.Constants.Add(new ConstantDefinition
                {
                    Name = field.Name,
                    // Constants have the default types of values, e.g. BSTR strings, rather than those of structure fields.
                    Type = MapType(field.FieldType, GetDescriptor(field), TypePosition.Parameter, $"{type.Name}.{field.Name}"),
                    Value = field.GetRawConstantValue(),
                });
            }
        }

        private void PopulateCoClass(Type type, CoClassDefinition definition)
        {
            // The default interface is the first base of the coclass interface of TlbImp (X : IDefault, XEvents_Event),
            // otherwise the first interface of the class.
            var interfaces = type.GetInterfaces().Where(IsCoClassInterfaceCandidate).ToList();
            var coClassInterface = GetCoClassInterface(type);
            Type? defaultInterface = coClassInterface?.GetInterfaces().FirstOrDefault(IsCoClassInterfaceCandidate) ?? interfaces.FirstOrDefault();
            if (defaultInterface is not null)
            {
                definition.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(GetOrCreateDefinition(defaultInterface)), IsDefault: true, IsSource: false, IsRestricted: false));
            }

            // The bases of the other interfaces are implemented through them.
            foreach (Type implemented in interfaces)
            {
                if (implemented != defaultInterface && !interfaces.Any(other => other != implemented && other.GetInterfaces().Contains(implemented)))
                {
                    definition.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(GetOrCreateDefinition(implemented)), IsDefault: false, IsSource: false, IsRestricted: false));
                }
            }

            string? sources = GetArgument<string>(type.GetCustomAttributesData(), InteropNamespace + "ComSourceInterfacesAttribute");
            bool first = true;
            foreach (string name in (sources ?? string.Empty).Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (ResolveTypeName(name, type.Assembly) is not { } source)
                {
                    LogUnresolvedSource(name, definition.Name);
                    continue;
                }

                definition.Interfaces.Add(new CoClassInterface(new UserDefinedTypeReference(GetOrCreateDefinition(source)), IsDefault: first, IsSource: true, IsRestricted: false));
                first = false;
            }
        }

        private bool IsCoClassInterfaceCandidate(Type type) =>
            IsComImport(type) && Find(type.GetCustomAttributesData(), InteropNamespace + "CoClassAttribute") is null && GetInterfaceKind(type) is not null;

        private Type? ResolveTypeName(string name, Assembly defaultAssembly)
        {
            int comma = name.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0)
            {
                return defaultAssembly.GetType(name);
            }

            try
            {
                return _context.LoadFromAssemblyName(name[(comma + 1)..].Trim()).GetType(name[..comma].Trim());
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        // The interface with [CoClass(typeof(XClass))] that TlbImp generates for the coclass XClass, named like the coclass.
        private Type? GetCoClassInterface(Type coClass)
        {
            if (!_coClassInterfaces.TryGetValue(coClass.Assembly, out var map))
            {
                map = [];
                foreach (Type type in GetTypes(coClass.Assembly).Where(static t => t.IsInterface && IsComImport(t)))
                {
                    if (GetArgument<Type>(type.GetCustomAttributesData(), InteropNamespace + "CoClassAttribute") is { } target)
                    {
                        map.TryAdd(target, type);
                    }
                }

                _coClassInterfaces.Add(coClass.Assembly, map);
            }

            return map.GetValueOrDefault(coClass);
        }

        private static InterfaceKind? GetInterfaceKind(Type type)
        {
            object? value = Find(type.GetCustomAttributesData(), InteropNamespace + "InterfaceTypeAttribute") is { ConstructorArguments: [var argument] } ? argument.Value : null;
            return value is null ? InterfaceKind.Dual : Convert.ToInt32(value, CultureInfo.InvariantCulture) switch
            {
                (int)ComInterfaceType.InterfaceIsIUnknown => InterfaceKind.Vtable,
                (int)ComInterfaceType.InterfaceIsIDispatch => InterfaceKind.Dispatch,
                (int)ComInterfaceType.InterfaceIsDual => InterfaceKind.Dual,
                _ => null,
            };
        }

        private static List<FieldInfo> GetInstanceFields(Type type) =>
            [.. type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(static f => f.MetadataToken)];

        private static List<MethodInfo> GetDeclaredMethods(Type type) =>
            [.. type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(static m => m.MetadataToken)];

        private PrimitiveTypeReference GetPrimitive(VarEnum vt)
        {
            if (!_primitives.TryGetValue(vt, out var primitive))
            {
                primitive = new PrimitiveTypeReference(vt);
                _primitives.Add(vt, primitive);
            }

            return primitive;
        }

        private MarshalDescriptor? GetDescriptor(ParameterInfo parameter)
        {
            if ((parameter.Attributes & ParameterAttributes.HasFieldMarshal) == 0)
            {
                return null;
            }

            var reader = GetMetadataReader(parameter.Member.Module.Assembly);
            return MarshalDescriptor.Read(reader, reader.GetParameter(MetadataTokens.ParameterHandle(parameter.MetadataToken)).GetMarshallingDescriptor());
        }

        private MarshalDescriptor? GetDescriptor(FieldInfo field)
        {
            if ((field.Attributes & FieldAttributes.HasFieldMarshal) == 0)
            {
                return null;
            }

            var reader = GetMetadataReader(field.Module.Assembly);
            return MarshalDescriptor.Read(reader, reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(field.MetadataToken)).GetMarshallingDescriptor());
        }

        private int GetFieldOffset(FieldInfo field)
        {
            var reader = GetMetadataReader(field.Module.Assembly);
            return reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(field.MetadataToken)).GetOffset();
        }

        // The marshalling information is decoded from the metadata, because MetadataLoadContext resolves the types it names.
        private MetadataReader GetMetadataReader(Assembly assembly)
        {
            if (!_peReaders.TryGetValue(assembly, out var reader))
            {
                reader = new PEReader(File.OpenRead(assembly.Location));
                _peReaders.Add(assembly, reader);
            }

            return reader.GetMetadataReader();
        }

        [LoggerMessage(Level = LogLevel.Debug, Message = "Reading {Kind} '{Name}'")]
        private partial void LogReadingType(Model.TypeKind kind, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "Importing {Kind} '{Name}' from '{Library}'")]
        private partial void LogImportingType(Model.TypeKind kind, string name, string library);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The source interface '{Name}' of the coclass '{CoClass}' was not found")]
        private partial void LogUnresolvedSource(string name, string coClass);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping '{Name}', which is a source-generated COM interface rather than a [ComImport] one")]
        private partial void LogSkippingGeneratedInterface(string name);

        // Alignment is the alignment recorded in the model, the packing when it is specified; FieldAlignment is the alignment
        // of the structure as a field of another structure.
        private readonly record struct RecordLayout(int Size, int Alignment, int[] Offsets, bool IsUnion)
        {
            public int FieldAlignment { get; init; }
        }
    }
}
