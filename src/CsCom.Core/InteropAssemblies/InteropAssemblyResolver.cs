using System.Reflection;
using System.Runtime.InteropServices;

namespace CsCom.InteropAssemblies;

/// <summary>
/// Finds the assemblies referenced by an interop assembly: in the reference paths, next to the interop assembly, among the
/// assemblies of the .NET runtime, whose facades resolve the references to .NET Framework assemblies, and in the global
/// assembly caches of .NET Framework, where the primary interop assemblies are installed.
/// </summary>
internal sealed class InteropAssemblyResolver : MetadataAssemblyResolver
{
    private readonly List<string> _files = [];
    private readonly List<string> _directories = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="InteropAssemblyResolver"/> class.
    /// </summary>
    /// <param name="assemblyPath">The path to the interop assembly.</param>
    /// <param name="referencePaths">Assemblies or directories that contain the referenced assemblies.</param>
    public InteropAssemblyResolver(string assemblyPath, IEnumerable<string> referencePaths)
    {
        foreach (string path in referencePaths)
        {
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                _directories.Add(fullPath);
            }
            else
            {
                _files.Add(fullPath);
            }
        }

        _directories.Add(Path.GetDirectoryName(assemblyPath)!);
        _directories.Add(RuntimeEnvironment.GetRuntimeDirectory());
    }

    /// <summary>Gets the name of the last assembly that was not found.</summary>
    public AssemblyName? Missing { get; private set; }

    /// <inheritdoc/>
    public override Assembly? Resolve(MetadataLoadContext context, AssemblyName assemblyName)
    {
        if (FindPath(assemblyName) is { } path)
        {
            return context.LoadFromAssemblyPath(path);
        }

        Missing = assemblyName;
        return null;
    }

    private string? FindPath(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        string fileName = name + ".dll";
        return _files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase) && File.Exists(f))
            ?? _directories.Select(d => Path.Combine(d, fileName)).FirstOrDefault(File.Exists)
            ?? FindInGlobalAssemblyCache(name, assemblyName.Version);
    }

    // The caches have a directory per assembly name with a subdirectory per version and public key token, e.g.
    // GAC_MSIL\office\15.0.0.0__71e9bce111e9429c\office.dll; the requested version is preferred, otherwise the highest one.
    private static string? FindInGlobalAssemblyCache(string name, Version? version)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string architecture = Environment.Is64BitProcess ? "GAC_64" : "GAC_32";
        string[] roots =
        [
            Path.Combine(windows, "Microsoft.NET", "assembly", "GAC_MSIL"),
            Path.Combine(windows, "Microsoft.NET", "assembly", architecture),
            Path.Combine(windows, "assembly", "GAC_MSIL"),
            Path.Combine(windows, "assembly", "GAC"),
            Path.Combine(windows, "assembly", architecture),
        ];

        var candidates = roots
            .Select(root => Path.Combine(root, name))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(directory => (Path: Path.Combine(directory, name + ".dll"), Version: ParseVersion(Path.GetFileName(directory))))
            .Where(static c => File.Exists(c.Path))
            .ToList();

        return candidates.FirstOrDefault(c => version is not null && c.Version == version).Path
            ?? candidates.OrderByDescending(static c => c.Version).FirstOrDefault().Path;
    }

    // The caches of .NET Framework 4 prefix the version of the runtime: v4.0_15.0.0.0__71e9bce111e9429c.
    private static Version? ParseVersion(string directoryName)
    {
        if (directoryName.StartsWith("v4.0_", StringComparison.Ordinal))
        {
            directoryName = directoryName[5..];
        }

        int separator = directoryName.IndexOf("__", StringComparison.Ordinal);
        return Version.TryParse(separator < 0 ? directoryName : directoryName[..separator], out var version) ? version : null;
    }
}
