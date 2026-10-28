using System.Text;
using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// Writes the generated C# files into the <c>Generated</c> folder of a project, or into another folder for CsCom.Build.
/// </summary>
internal static class GeneratedFiles
{
    /// <summary>The folder of the project that contains the generated files. Its C# files are replaced when the project is updated.</summary>
    public const string FolderName = "Generated";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Replaces the C# files of the <c>Generated</c> folder of a project with the documents, see <see cref="WriteFolder"/>.
    /// </summary>
    /// <returns>The paths of the files.</returns>
    public static IReadOnlyList<string> Write(string projectDirectory, IReadOnlyList<GeneratedDocument> documents) =>
        WriteFolder(Path.Combine(projectDirectory, FolderName), documents);

    /// <summary>
    /// Replaces the C# files of a folder with the documents: only the files whose content changed are written, so that the
    /// others keep their timestamps for incremental builds, and the C# files that are no longer generated are deleted.
    /// </summary>
    /// <returns>The paths of the files.</returns>
    public static IReadOnlyList<string> WriteFolder(string folder, IReadOnlyList<GeneratedDocument> documents)
    {
        string root = Path.GetFullPath(folder);
        var paths = new List<string>(documents.Count);
        foreach (var document in documents)
        {
            string path = Path.GetFullPath(Path.Combine(root, document.RelativePath));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The generated file '{document.RelativePath}' is outside of the folder '{root}'.");
            }

            string text = document.GetText();
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text, Utf8WithBom);
            }

            paths.Add(path);
        }

        RemoveStaleFiles(root, paths.ToHashSet(StringComparer.OrdinalIgnoreCase));
        return paths;
    }

    private static void RemoveStaleFiles(string folder, HashSet<string> generated)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Where(f => !generated.Contains(f)).ToList())
        {
            File.Delete(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(static d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }
}
