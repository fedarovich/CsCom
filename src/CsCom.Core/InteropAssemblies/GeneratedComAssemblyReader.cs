using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CsCom.LibrarySets;
using CsCom.Model;
using CsCom.TypeLibraries;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using static CsCom.InteropAssemblies.MetadataAttributes;

using TypeDefinition = CsCom.Model.TypeDefinition;

namespace CsCom.InteropAssemblies;

/// <summary>
/// The identity of the type library that is exported from an assembly, which overrides that of the assembly.
/// </summary>
public sealed class ExportedLibraryIdentity
{
    /// <summary>The name of the library; the name of the assembly when <see langword="null"/>.</summary>
    public string? Name { get; init; }

    /// <summary>The identifier of the library; the <c>[Guid]</c> of the assembly when <see langword="null"/>.</summary>
    public Guid? Guid { get; init; }

    /// <summary>
    /// The version of the library, major and minor; <c>[TypeLibVersion]</c> or else the version of the assembly when
    /// <see langword="null"/>.
    /// </summary>
    public (ushort Major, ushort Minor)? Version { get; init; }
}

/// <summary>
/// Reads the COM types that an assembly declares for source-generated COM interop into the <see cref="TypeLibrary"/> model
/// of the type library to export for them: the public <c>[GeneratedComInterface]</c> interfaces, the public
/// <c>[GeneratedComClass]</c> classes with a <c>[Guid]</c>, and the enumerations and structures that they use. The metadata
/// is read with <see cref="MetadataLoadContext"/>, so the assembly and its references are not loaded for execution.
/// </summary>
/// <remarks>
/// <para>
/// The types of the code that CsCom generated from type libraries, in the assembly or in the assemblies that it
/// references, are not types of the library: they are imported from their type libraries, which the metadata of the
/// generated code names (<see cref="TypeLibraryMarker"/>).
/// </para>
/// <para>
/// What the attributes of source-generated COM interop do not tell comes from the attributes of the built-in COM interop:
/// <c>[DispId]</c>, <c>[ComDefaultInterface]</c>, <c>[ComSourceInterfaces]</c>, <c>[TypeLibType]</c>,
/// <c>[TypeLibFunc]</c>, <c>[ComVisible]</c>, the <c>[Guid]</c> and <c>[TypeLibVersion]</c> of the assembly, and
/// <c>[Description]</c> for the help strings.
/// </para>
/// </remarks>
public sealed partial class GeneratedComAssemblyReader
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeneratedComAssemblyReader"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="referencePaths">Assemblies or directories that contain the assemblies referenced by the assembly.</param>
    public GeneratedComAssemblyReader(ILogger<GeneratedComAssemblyReader>? logger = null, IReadOnlyList<string>? referencePaths = null)
    {
        _logger = logger ?? NullLogger<GeneratedComAssemblyReader>.Instance;
        ReferencePaths = referencePaths ?? [];
    }

    /// <summary>
    /// Gets the assemblies or directories that contain the assemblies referenced by the assembly. The directory of the
    /// assembly and the .NET runtime are searched as well.
    /// </summary>
    public IReadOnlyList<string> ReferencePaths { get; }

    /// <summary>
    /// Gets the number of the public COM interfaces and classes that the last <see cref="Read"/> left out because of
    /// <c>[ComVisible(false)]</c>, on them or on the assembly.
    /// </summary>
    public int HiddenTypeCount { get; private set; }

    /// <summary>
    /// Reads the assembly.
    /// </summary>
    /// <param name="path">The path to the assembly.</param>
    /// <param name="identity">The identity of the type library, which overrides that of the assembly.</param>
    /// <param name="catalog">
    /// The type libraries that the library can refer to. With it, a type in a namespace of generated code that its type
    /// library does not have is a type of the assembly, and a referenced assembly with a <c>[Guid]</c> is only taken to
    /// have a type library of its own when that library is found. Without it, every type of such a namespace is taken
    /// for a type of the type library, and no referenced assembly has one of its own.
    /// </param>
    /// <returns>The model of the type library of the assembly.</returns>
    /// <exception cref="CsComException">
    /// The assembly or one of the assemblies it references cannot be loaded, the type library has no identifier, or a type
    /// cannot be described in a type library.
    /// </exception>
    public TypeLibrary Read(string path, ExportedLibraryIdentity? identity = null, TypeLibraryCatalog? catalog = null)
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
            using var session = new Session(context, assembly, fullPath, identity ?? new ExportedLibraryIdentity(), catalog, _logger);
            HiddenTypeCount = 0;
            var library = session.Read();
            HiddenTypeCount = session.HiddenTypeCount;
            return library;
        }
        catch (FileNotFoundException e) when (resolver.Missing is { } missing)
        {
            throw new CsComException(
                $"The assembly '{missing.FullName}', which '{Path.GetFileName(fullPath)}' references, was not found: specify the assembly, or the directory that contains it, as a reference path.",
                e);
        }
    }

    /// <summary>
    /// Gets the identity of the type library that is exported from an assembly without overrides: the name of the assembly,
    /// its <c>[Guid]</c>, and <c>[TypeLibVersion]</c> or else its version, where 0.0 is 1.0.
    /// </summary>
    internal static TypeLibraryReference GetExportedLibrary(Assembly assembly)
    {
        var attributes = assembly.GetCustomAttributesData();
        string name = InvalidNameCharacter().Replace(assembly.GetName().Name ?? "Library", "_");
        if (char.IsAsciiDigit(name[0]))
        {
            name = "_" + name;
        }

        Guid guid = GetGuid(attributes) ?? Guid.Empty;
        (ushort major, ushort minor) = Find(attributes, InteropNamespace + "TypeLibVersionAttribute") is { ConstructorArguments: [var majorArgument, var minorArgument] }
            ? ((ushort)(int)majorArgument.Value!, (ushort)(int)minorArgument.Value!)
            : ((ushort)(assembly.GetName().Version?.Major ?? 0), (ushort)(assembly.GetName().Version?.Minor ?? 0));
        return new TypeLibraryReference(name, guid, major == 0 && minor == 0 ? (ushort)1 : major, minor);
    }

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex InvalidNameCharacter();

    [LoggerMessage(Level = LogLevel.Information, Message = "Reading assembly '{Path}'")]
    private partial void LogLoading(string path);

    private sealed partial class Session : IDisposable
    {
        private const int DispIdNewEnum = -4;
        private const int FirstMemberId = 0x60000000;
        private const string MarshallingNamespace = InteropNamespace + "Marshalling.";

        private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
        private static readonly Guid IID_IEnumVARIANT = new("00020404-0000-0000-C000-000000000046");

        private readonly MetadataLoadContext _context;
        private readonly Assembly _assembly;
        private readonly string _path;
        private readonly ExportedLibraryIdentity _identity;
        private readonly TypeLibraryCatalog? _catalog;
        private readonly ILogger _logger;
        private readonly MarshallingMetadata _marshalling = new();
        private readonly Dictionary<Type, TypeDefinition> _definitions = [];
        private readonly Dictionary<Assembly, AssemblyMarkers> _markers = [];
        private readonly Dictionary<Type, TypeLibraryReference?> _imported = [];
        private readonly List<InterfaceDefinition> _unmarkedInterfaces = [];
        private readonly Dictionary<Guid, TypeLibraryReference> _importedLibraries = [];
        private readonly HashSet<string> _importedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<(Type Type, TypeDefinition Definition)> _pending = [];
        private readonly Dictionary<VarEnum, PrimitiveTypeReference> _primitives = [];
        private TypeLibrary _library = null!;

        public Session(MetadataLoadContext context, Assembly assembly, string path, ExportedLibraryIdentity identity, TypeLibraryCatalog? catalog, ILogger logger)
        {
            _catalog = catalog;
            _context = context;
            _assembly = assembly;
            _path = path;
            _identity = identity;
            _logger = logger;
        }

        public TypeLibrary Read()
        {
            var library = GetExportedLibrary(_assembly);
            Guid guid = _identity.Guid ?? library.Guid;
            if (guid == Guid.Empty)
            {
                throw new CsComException(
                    $"'{Path.GetFileName(_path)}' has no identifier for its type library: add [assembly: Guid(\"...\")] to it or specify the identifier.");
            }

            var version = _identity.Version ?? (library.MajorVersion, library.MinorVersion);
            _library = new TypeLibrary
            {
                Name = _identity.Name ?? library.Name,
                Guid = guid,
                MajorVersion = version.Major,
                MinorVersion = version.Minor,
                Documentation = GetArgument<string>(_assembly.GetCustomAttributesData(), "System.Reflection.AssemblyDescriptionAttribute") is { Length: > 0 } description ? description : null,
                Path = _path,
            };

            foreach (Type type in _assembly.GetTypes().OrderBy(static t => t.MetadataToken))
            {
                if (IsRoot(type))
                {
                    GetOrCreateDefinition(type);
                }
            }

            while (_pending.TryDequeue(out var item))
            {
                Populate(item.Type, item.Definition);
            }

            RejectDuplicates();
            MarkAutomationInterfaces();

            // The types of the library first, then the imported ones in the order they were encountered.
            var ordered = _library.Types.Where(static t => t.ImportedFrom is null).Concat(_library.Types.Where(static t => t.ImportedFrom is not null)).ToList();
            _library.Types.Clear();
            _library.Types.AddRange(ordered);
            _library.ImportedLibraries.AddRange(_importedLibraries.Values);
            _library.ImportedFiles.AddRange(_importedFiles);
            return _library;
        }

        public int HiddenTypeCount { get; private set; }

        public void Dispose() => _marshalling.Dispose();

        // The types that are exported whether they are used or not: the COM interfaces and classes that are visible.
        private bool IsRoot(Type type)
        {
            if (!type.IsVisible || type.IsGenericTypeDefinition || GetImportedLibrary(type) is not null || IsSupportType(type))
            {
                return false;
            }

            bool isComType = type.IsInterface
                ? IsGeneratedComInterface(type) && GetWellKnownInterface(type) is null
                : type.IsClass && Find(type.GetCustomAttributesData(), MarshallingNamespace + "GeneratedComClassAttribute") is not null;
            if (!isComType)
            {
                return false;
            }

            if (!IsComVisible(type))
            {
                HiddenTypeCount++;
                return false;
            }

            if (type.IsClass && GetGuid(type.GetCustomAttributesData()) is null)
            {
                LogSkippingClass(type.Name);
                return false;
            }

            return true;
        }

        private static bool IsGeneratedComInterface(Type type) =>
            type.IsInterface && Find(type.GetCustomAttributesData(), MarshallingNamespace + "GeneratedComInterfaceAttribute") is not null;

        // [ComVisible] of the type, otherwise that of the assembly; visible without it.
        private static bool IsComVisible(Type type)
        {
            for (Type? current = type; current is not null; current = current.DeclaringType)
            {
                if (Find(current.GetCustomAttributesData(), InteropNamespace + "ComVisibleAttribute") is { ConstructorArguments: [var argument] })
                {
                    return (bool)argument.Value!;
                }
            }

            return Find(type.Assembly.GetCustomAttributesData(), InteropNamespace + "ComVisibleAttribute") is not { ConstructorArguments: [{ Value: false }] };
        }

        // The interfaces that every type library refers to in stdole, whatever declares them for source-generated COM interop.
        private static Guid? GetWellKnownInterface(Type type) =>
            type.IsInterface && GetGuid(type.GetCustomAttributesData()) is { } guid && (guid == IID_IUnknown || guid == IID_IDispatch || guid == IID_IEnumVARIANT) ? guid : null;

        private AssemblyMarkers GetMarkers(Assembly assembly)
        {
            if (!_markers.TryGetValue(assembly, out var markers))
            {
                markers = AssemblyMarkers.Read(assembly);
                _markers.Add(assembly, markers);
            }

            return markers;
        }

        // The type library that the type was generated from by CsCom, or the type library of another assembly that exports
        // its types.
        private TypeLibraryReference? GetImportedLibrary(Type type)
        {
            if (!_imported.TryGetValue(type, out var library))
            {
                library = FindImportedLibrary(type);
                _imported.Add(type, library);
            }

            return library;
        }

        private TypeLibraryReference? FindImportedLibrary(Type type)
        {
            var markers = GetMarkers(type.Assembly);
            if (type.Namespace is { } @namespace && markers.Libraries.TryGetValue(@namespace, out var library))
            {
                // A type that was written into a namespace of generated code is not a type of its type library. When the
                // library is not found, the writer reports it for the types that are used.
                if (_catalog is null || !_catalog.TryGetLibrary(library, out var catalogLibrary))
                {
                    return library;
                }

                if (catalogLibrary.Find(GetInterfaceId(type), GetOriginalName(type)) is not null)
                {
                    return library;
                }
            }

            // The other types of a referenced assembly with a [Guid] are those of its own type library, the one that is
            // exported from it, if that library exists and has the type: many assemblies have a [Guid] that identifies
            // nothing.
            return type.Assembly != _assembly && !IsSupportType(type)
                && markers.Exported is { } exported && _catalog?.TryGetLibrary(exported, out var exportedLibrary) == true
                && exportedLibrary.Find(GetInterfaceId(type), type.Name) is not null
                ? exported
                : null;
        }

        // The identifier by which an interface is found in a type library; the other types are found by their names.
        private static Guid? GetInterfaceId(Type type) => type.IsInterface ? GetGuid(type.GetCustomAttributesData()) : null;

        // The name of a type of generated code in its type library.
        private string GetOriginalName(Type type) =>
            GetMarkers(type.Assembly).OriginalNames.TryGetValue(type.FullName ?? type.Name, out string? original) ? original : type.Name;

        // The support code of CsCom: its package, or the namespace that it was generated into.
        private bool IsSupportType(Type type) =>
            type.Assembly.GetName().Name is "CsCom.Runtime" or "CsCom.Runtime.Dynamic"
            || (type.Namespace is { } @namespace && GetMarkers(type.Assembly).SupportNamespaces.Contains(@namespace));

        // A type of .NET, which is in no type library. The exported assembly can have such a name itself.
        private bool IsFrameworkType(Type type) =>
            type.Assembly != _assembly && type.Assembly.GetName().Name is { } name
            && (name is "mscorlib" or "netstandard" or "System" || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal));

        private TypeDefinition GetOrCreateDefinition(Type type)
        {
            if (_definitions.TryGetValue(type, out var existing))
            {
                return existing;
            }

            var attributes = type.GetCustomAttributesData();
            TypeLibraryReference? importedFrom = GetImportedLibrary(type);
            string name = importedFrom is not null ? GetOriginalName(type) : type.Name;
            Guid? guid = GetGuid(attributes);
            string? documentation = GetArgument<string>(attributes, "System.ComponentModel.DescriptionAttribute");
            Model.TypeFlags? flags = Find(attributes, InteropNamespace + "TypeLibTypeAttribute") is { ConstructorArguments: [var flagsArgument] }
                ? (Model.TypeFlags)(ushort)Convert.ToInt16(flagsArgument.Value, CultureInfo.InvariantCulture)
                : null;

            TypeDefinition definition;
            if (type.IsEnum)
            {
                definition = new EnumDefinition { Name = name, OriginalName = name, Guid = guid, Flags = flags ?? Model.TypeFlags.None };
            }
            else if (type.IsValueType)
            {
                bool isExplicit = type.StructLayoutAttribute?.Value == LayoutKind.Explicit;
                bool isUnion = isExplicit && GetInstanceFields(type) is { Count: > 0 } fields && fields.All(f => _marshalling.GetFieldOffset(f) == 0);
                if (importedFrom is null && ((isExplicit && !isUnion) || type.StructLayoutAttribute is { Size: > 0 }))
                {
                    // A type library has the fields of a structure, whose offsets and size it computes itself.
                    LogExplicitLayout(name);
                }

                definition = new RecordDefinition
                {
                    Name = name,
                    OriginalName = name,
                    Guid = guid,
                    Flags = flags ?? Model.TypeFlags.None,
                    IsUnion = isUnion,
                    Alignment = type.StructLayoutAttribute is { Pack: > 0 } layout ? layout.Pack : 0,
                };
            }
            else if (type.IsInterface)
            {
                if (guid is null && importedFrom is null)
                {
                    throw new CsComException($"The interface '{type.FullName}' has no [Guid], which the interfaces of a type library need.");
                }

                // Whether an interface without [TypeLibType] is compatible with Automation is told by its members, see
                // MarkAutomationInterfaces.
                bool isDual = IsDispatchInterface(type);
                definition = new InterfaceDefinition
                {
                    Name = name,
                    OriginalName = name,
                    Guid = guid,
                    InterfaceKind = isDual ? InterfaceKind.Dual : InterfaceKind.Vtable,
                    Flags = flags ?? (isDual ? Model.TypeFlags.Dual | Model.TypeFlags.OleAutomation | Model.TypeFlags.Dispatchable : Model.TypeFlags.None),
                };
                if (flags is null && importedFrom is null)
                {
                    _unmarkedInterfaces.Add((InterfaceDefinition)definition);
                }
            }
            else if (importedFrom is not null)
            {
                // A class of the code generated from a type library is the late-bound wrapper of a dispinterface, which is
                // found in the type library by its name: its identifier is not in the metadata.
                definition = new InterfaceDefinition
                {
                    Name = name,
                    OriginalName = name,
                    InterfaceKind = InterfaceKind.Dispatch,
                    Flags = Model.TypeFlags.Dispatchable,
                    BaseInterface = GetPrimitive(VarEnum.VT_DISPATCH),
                };
            }
            else
            {
                bool canCreate = !type.IsAbstract && type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is not null;
                definition = new CoClassDefinition
                {
                    Name = name,
                    OriginalName = name,
                    Guid = guid,
                    Flags = flags ?? (canCreate ? Model.TypeFlags.CanCreate : Model.TypeFlags.None),
                };
            }

            definition.Documentation = string.IsNullOrEmpty(documentation) ? null : documentation;
            definition.ImportedFrom = importedFrom;
            _definitions.Add(type, definition);
            _library.Types.Add(definition);
            if (importedFrom is null)
            {
                _pending.Enqueue((type, definition));
            }
            else
            {
                // The types of other libraries are only referred to, so their members are not read.
                _importedLibraries.TryAdd(importedFrom.Guid, importedFrom);
                if (type.Assembly != _assembly && !string.IsNullOrEmpty(type.Assembly.Location))
                {
                    _importedFiles.Add(type.Assembly.Location);
                }

                LogImportingType(definition.Kind, name, importedFrom.Name);
            }

            return definition;
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
            }
        }

        private static void PopulateEnum(Type type, EnumDefinition definition)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(static f => f.IsLiteral).OrderBy(static f => f.MetadataToken))
            {
                object value = field.GetRawConstantValue()!;
                definition.Members.Add(new EnumMember
                {
                    Name = field.Name,
                    Value = value is ulong unsigned ? unchecked((long)unsigned) : Convert.ToInt64(value, CultureInfo.InvariantCulture),
                    Documentation = GetDescription(field.GetCustomAttributesData()),
                });
            }
        }

        private void PopulateRecord(Type type, RecordDefinition definition)
        {
            foreach (var field in GetInstanceFields(type))
            {
                definition.Fields.Add(new Model.FieldDefinition
                {
                    Name = GetFieldName(field),
                    Type = MapField(field, $"{type.Name}.{GetFieldName(field)}"),
                    Documentation = GetDescription(field.GetCustomAttributesData()),
                });
            }
        }

        // The field that the compiler declares for an auto-property, <Name>k__BackingField, has the name of the property.
        private static string GetFieldName(FieldInfo field) =>
            field.Name.StartsWith('<') && field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal) ? field.Name[1..^16] : field.Name;

        private void PopulateCoClass(Type type, CoClassDefinition definition)
        {
            var attributes = type.GetCustomAttributesData();
            var interfaces = type.GetInterfaces().Where(i => IsExportedInterface(i) && IsComVisible(i)).ToList();

            // The bases of the other interfaces are implemented through them.
            interfaces.RemoveAll(i => interfaces.Any(other => other != i && other.GetInterfaces().Contains(i)));
            Type? defaultInterface = GetArgument<Type>(attributes, InteropNamespace + "ComDefaultInterfaceAttribute");
            if (defaultInterface is not null && !interfaces.Contains(defaultInterface))
            {
                LogUnresolvedInterface(defaultInterface.Name, definition.Name, "default");
                defaultInterface = null;
            }

            defaultInterface ??= interfaces.FirstOrDefault();
            foreach (Type implemented in interfaces.OrderBy(i => i == defaultInterface ? 0 : 1))
            {
                definition.Interfaces.Add(new CoClassInterface(MapCoClassInterface(implemented), IsDefault: implemented == defaultInterface, IsSource: false, IsRestricted: false));
            }

            bool first = true;
            foreach (Type source in GetSourceInterfaces(type, definition.Name))
            {
                definition.Interfaces.Add(new CoClassInterface(MapCoClassInterface(source), IsDefault: first, IsSource: true, IsRestricted: false));
                first = false;
            }
        }

        // An interface that a class implements for COM: a source-generated one, or IDispatch itself.
        private bool IsExportedInterface(Type type) =>
            IsGeneratedComInterface(type) && (GetWellKnownInterface(type) is { } wellKnown ? wellKnown == IID_IDispatch : type.IsVisible || GetImportedLibrary(type) is not null);

        private TypeReference MapCoClassInterface(Type type) => GetWellKnownInterface(type) switch
        {
            { } guid when guid == IID_IDispatch => GetPrimitive(VarEnum.VT_DISPATCH),
            { } guid when guid == IID_IEnumVARIANT => new WellKnownTypeReference(WellKnownType.IEnumVariant),
            { } => GetPrimitive(VarEnum.VT_UNKNOWN),
            null => new UserDefinedTypeReference(GetOrCreateDefinition(type)),
        };

        // [ComSourceInterfaces] names the event interfaces of a class: types, or names separated by null characters. A
        // dispinterface of the code generated from a type library is named by its late-bound wrapper class.
        private IEnumerable<Type> GetSourceInterfaces(Type type, string className)
        {
            if (Find(type.GetCustomAttributesData(), InteropNamespace + "ComSourceInterfacesAttribute") is not { } attribute)
            {
                yield break;
            }

            foreach (var argument in attribute.ConstructorArguments)
            {
                if (argument.Value is Type source)
                {
                    if (IsSourceInterface(source))
                    {
                        yield return source;
                    }
                    else
                    {
                        LogUnresolvedInterface(source.Name, className, "source");
                    }
                }
                else if (argument.Value is string names)
                {
                    foreach (string name in names.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string typeName = name.Split(',')[0].Trim();
                        Type? named = type.Assembly.GetType(typeName)
                            ?? type.Assembly.GetReferencedAssemblies().Select(TryLoad).Select(a => a?.GetType(typeName)).FirstOrDefault(static t => t is not null);
                        if (named is not null && IsSourceInterface(named))
                        {
                            yield return named;
                        }
                        else
                        {
                            LogUnresolvedInterface(name, className, "source");
                        }
                    }
                }
            }

            Assembly? TryLoad(AssemblyName name)
            {
                try
                {
                    return _context.LoadFromAssemblyName(name);
                }
                catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    return null;
                }
            }
        }

        private bool IsSourceInterface(Type type) =>
            type.IsInterface ? IsGeneratedComInterface(type) && GetWellKnownInterface(type) is null : type.IsClass && GetImportedLibrary(type) is not null;

        // Whether the interface derives from IDispatch, which makes it a dual interface.
        private static bool IsDispatchInterface(Type type) =>
            type.GetInterfaces().Any(static i => GetWellKnownInterface(i) == IID_IDispatch);

        // The COM interface that an interface derives from: the one that derives from the others that it lists.
        private static Type? GetBaseInterface(Type type)
        {
            var candidates = type.GetInterfaces().Where(IsGeneratedComInterface).ToList();
            return candidates.OrderByDescending(c => candidates.Count(other => c.GetInterfaces().Contains(other))).FirstOrDefault();
        }

        // The number of interfaces that the interface derives from, IUnknown included, which is in the identifiers that the
        // members of an interface get by default so that they differ from those of its bases.
        private static int GetDepth(Type type)
        {
            int depth = 1;
            for (Type? current = GetBaseInterface(type); current is not null; current = GetBaseInterface(current))
            {
                depth++;
            }

            return depth;
        }

        private static List<FieldInfo> GetInstanceFields(Type type) =>
            [.. type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(static f => f.MetadataToken)];

        private static string? GetDescription(IList<CustomAttributeData> attributes) =>
            GetArgument<string>(attributes, "System.ComponentModel.DescriptionAttribute") is { Length: > 0 } description ? description : null;

        // The names of a type library are not case-sensitive, and two types of different namespaces can have the same name.
        private void RejectDuplicates()
        {
            var own = _library.Types.Where(static t => t.ImportedFrom is null).ToList();
            if (own.GroupBy(static t => t.OriginalName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(static g => g.Count() > 1) is { } names)
            {
                var types = names.Select(n => _definitions.First(d => d.Value == n).Key.FullName);
                throw new CsComException($"The types {string.Join(" and ", types.Select(static t => $"'{t}'"))} have the same name in the type library, where the names are not case-sensitive.");
            }

            if (own.Where(static t => t.Guid is not null).GroupBy(static t => t.Guid).FirstOrDefault(static g => g.Count() > 1) is { } guids)
            {
                throw new CsComException($"The types {string.Join(" and ", guids.Select(static t => $"'{t.OriginalName}'"))} have the same [Guid] {guids.Key:B}.");
            }
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

        // All the logging methods of the session are declared in this part, see InteropAssemblyReader.
        [LoggerMessage(Level = LogLevel.Debug, Message = "Reading {Kind} '{Name}'")]
        private partial void LogReadingType(Model.TypeKind kind, string name);

        [LoggerMessage(Level = LogLevel.Information, Message = "Referring to {Kind} '{Name}' of the type library '{Library}'")]
        private partial void LogImportingType(Model.TypeKind kind, string name, string library);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping the class '{Name}', which has no [Guid]")]
        private partial void LogSkippingClass(string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The {Role} interface '{Name}' of the class '{Class}' is not a COM interface that the class can have in the type library")]
        private partial void LogUnresolvedInterface(string name, string @class, string role);

        [LoggerMessage(Level = LogLevel.Warning, Message = "'{Member}' uses {Type}, which is not supported; it is exported as {Native}")]
        private partial void LogUnsupportedType(string member, string type, string native);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The structure '{Name}' has an explicit layout or size, which a type library cannot describe; it is exported with a sequential layout")]
        private partial void LogExplicitLayout(string name);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "The dual interface '{Name}' is marked oleautomation, which OLE Automation requires of dual interfaces, but '{Member}' is not compatible with Automation")]
        private partial void LogIncompatibleDualInterface(string name, string member);

        [LoggerMessage(Level = LogLevel.Information, Message = "'{Member}' is overloaded: the overload is exported as '{Name}'")]
        private partial void LogOverload(string member, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "'{Member}' is named like an accessor of a property, but does not have the parameters or the return value of one; it is exported as a method")]
        private partial void LogInvalidAccessor(string member);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "'{Type}' of the assembly '{Assembly}' is neither a type of a type library that CsCom generated code from, nor found in a type library of the assembly: export the type library of the assembly and specify the file, or regenerate the code with this version of CsCom")]
        private partial void LogUnknownLibrary(string type, string assembly);

        // The metadata that tells which types of an assembly were generated from which type libraries.
        private sealed record AssemblyMarkers(
            Dictionary<string, TypeLibraryReference> Libraries,
            Dictionary<string, string> OriginalNames,
            HashSet<string> SupportNamespaces,
            TypeLibraryReference? Exported)
        {
            public static AssemblyMarkers Read(Assembly assembly)
            {
                var libraries = new Dictionary<string, TypeLibraryReference>(StringComparer.Ordinal);
                var originalNames = new Dictionary<string, string>(StringComparer.Ordinal);
                var supportNamespaces = new HashSet<string>(StringComparer.Ordinal);
                foreach (var attribute in assembly.GetCustomAttributesData())
                {
                    if (attribute.AttributeType.FullName != "System.Reflection.AssemblyMetadataAttribute"
                        || attribute.ConstructorArguments is not [{ Value: string key }, { Value: string value }])
                    {
                        continue;
                    }

                    switch (key)
                    {
                        case TypeLibraryMarker.LibraryKey when TypeLibraryMarker.TryParseLibrary(value, out string @namespace, out var library):
                            libraries[@namespace] = library;
                            break;
                        case TypeLibraryMarker.TypeKey when TypeLibraryMarker.TryParseType(value, out string fullName, out string originalName):
                            originalNames[fullName] = originalName;
                            break;
                        case TypeLibraryMarker.SupportNamespaceKey:
                            supportNamespaces.Add(value);
                            break;
                    }
                }

                // An assembly with a [Guid] that was not generated by CsCom exports a type library of its own.
                var exported = GetExportedLibrary(assembly);
                return new AssemblyMarkers(libraries, originalNames, supportNamespaces, exported.Guid == Guid.Empty ? null : exported);
            }
        }
    }
}
