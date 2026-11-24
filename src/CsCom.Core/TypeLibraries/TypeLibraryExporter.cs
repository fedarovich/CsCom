using System.Globalization;
using CsCom.InteropAssemblies;
using CsCom.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CsCom.TypeLibraries;

/// <summary>
/// What a <see cref="TypeLibraryExporter"/> exports.
/// </summary>
public sealed class TypeLibraryExportRequest
{
    /// <summary>The assembly with the source-generated COM types.</summary>
    public required string AssemblyPath { get; init; }

    /// <summary>The type library to write; the path of the assembly with the extension <c>.tlb</c> when <see langword="null"/>.</summary>
    public string? OutputPath { get; init; }

    /// <summary>The name of the library; the name of the assembly when <see langword="null"/> or empty.</summary>
    public string? Name { get; init; }

    /// <summary>The identifier of the library; the <c>[Guid]</c> of the assembly when <see langword="null"/> or empty.</summary>
    public string? Guid { get; init; }

    /// <summary>
    /// The version of the library, <c>major.minor</c>; <c>[TypeLibVersion]</c> or else the version of the assembly when
    /// <see langword="null"/> or empty.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>The platform of the type library.</summary>
    public TypeLibraryPlatform Platform { get; init; } = TypeLibraryPlatform.Win64;

    /// <summary>Assemblies or directories that contain the assemblies referenced by the assembly.</summary>
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];

    /// <summary>
    /// The files of the type libraries that the code of the assembly or of the assemblies that it references was generated
    /// from. A type library that is not among them is looked up in the registry.
    /// </summary>
    public IReadOnlyList<string> TypeLibraryPaths { get; init; } = [];
}

/// <summary>
/// The result of a <see cref="TypeLibraryExporter"/>.
/// </summary>
/// <param name="Path">The type library that was written.</param>
/// <param name="Library">The model of the type library.</param>
/// <param name="InputFiles">
/// The files that were read: the assembly, the referenced assemblies whose types the library uses, and the type libraries
/// that it refers to, except that of OLE Automation.
/// </param>
public sealed record TypeLibraryExportResult(string Path, TypeLibrary Library, IReadOnlyList<string> InputFiles);

/// <summary>
/// Exports the type library of an assembly with source-generated COM types, as TlbExp does for the COM types of the built-in
/// COM interop: <see cref="GeneratedComAssemblyReader"/> reads the model that <see cref="TypeLibraryWriter"/> writes.
/// </summary>
public sealed partial class TypeLibraryExporter
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeLibraryExporter"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public TypeLibraryExporter(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<TypeLibraryExporter>();
    }

    /// <summary>
    /// Exports the type library.
    /// </summary>
    /// <param name="request">The assembly and the options.</param>
    /// <returns>The type library that was written.</returns>
    /// <exception cref="CsComException">The request is not valid, or the type library cannot be read from the assembly or written.</exception>
    public TypeLibraryExportResult Export(TypeLibraryExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string assemblyPath = Path.GetFullPath(request.AssemblyPath);
        if (!File.Exists(assemblyPath))
        {
            throw new CsComException($"The assembly '{request.AssemblyPath}' was not found.");
        }

        var typeLibraryPaths = new List<string>();
        foreach (string path in request.TypeLibraryPaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(TypeLibrarySource.GetFilePath(fullPath)))
            {
                throw new CsComException($"The type library '{path}' was not found.");
            }

            typeLibraryPaths.Add(fullPath);
        }

        var identity = new ExportedLibraryIdentity
        {
            Name = ParseName(request.Name),
            Guid = ParseGuid(request.Guid),
            Version = ParseVersion(request.Version),
        };
        string outputPath = Path.GetFullPath(string.IsNullOrEmpty(request.OutputPath) ? Path.ChangeExtension(assemblyPath, ".tlb") : request.OutputPath);
        if (string.Equals(outputPath, assemblyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new CsComException($"The type library cannot be written to the assembly '{assemblyPath}' itself.");
        }

        var catalog = new TypeLibraryCatalog(typeLibraryPaths);
        var library = new GeneratedComAssemblyReader(_loggerFactory.CreateLogger<GeneratedComAssemblyReader>(), request.ReferencePaths).Read(assemblyPath, identity, catalog);
        int count = library.Types.Count(static t => t.ImportedFrom is null);
        if (count == 0)
        {
            LogNoTypes(Path.GetFileName(assemblyPath));
        }

        var imported = new TypeLibraryWriter(_loggerFactory.CreateLogger<TypeLibraryWriter>())
            .Write(library, outputPath, new TypeLibraryWriterOptions { Platform = request.Platform, Catalog = catalog });
        LogExported(library.Name, library.MajorVersion, library.MinorVersion, count, outputPath);
        return new TypeLibraryExportResult(
            outputPath,
            library,
            [.. new[] { assemblyPath }.Concat(library.ImportedFiles).Concat(imported).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static string? ParseName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        bool isValid = (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');
        return isValid ? name : throw new CsComException($"'{name}' is not a valid name of a type library: a name consists of letters, digits and underscores, and does not start with a digit.");
    }

    private static Guid? ParseGuid(string? guid)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return null;
        }

        return Guid.TryParse(guid, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new CsComException($"'{guid}' is not a valid identifier of a type library, e.g. 6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1000.");
    }

    private static (ushort Major, ushort Minor)? ParseVersion(string? version)
    {
        if (string.IsNullOrEmpty(version))
        {
            return null;
        }

        return version.Split('.') is [var major, var minor]
            && ushort.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out ushort majorVersion)
            && ushort.TryParse(minor, NumberStyles.None, CultureInfo.InvariantCulture, out ushort minorVersion)
            ? (majorVersion, minorVersion)
            : throw new CsComException($"'{version}' is not a valid version of a type library: a version is MAJOR.MINOR, e.g. 1.0, with numbers up to 65535.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{Assembly}' has no public [GeneratedComInterface] interface and no public [GeneratedComClass] class with a [Guid]: its type library is empty")]
    private partial void LogNoTypes(string assembly);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exported the type library '{Name}' {Major}.{Minor} with {Count} types to '{Path}'")]
    private partial void LogExported(string name, int major, int minor, int count, string path);
}
