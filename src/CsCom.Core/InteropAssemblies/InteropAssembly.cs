using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CsCom.InteropAssemblies;

/// <summary>
/// Recognizes .NET assemblies among the sources of the model.
/// </summary>
public static class InteropAssembly
{
    /// <summary>
    /// Whether the file is a .NET assembly, read by <see cref="InteropAssemblyReader"/>, rather than a type library or a native
    /// DLL with a type library resource.
    /// </summary>
    /// <param name="path">The path to the file.</param>
    public static bool IsAssembly(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }
}
