using System.Text;
using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// Writes the generated C# files into the <c>Generated</c> folder of a project.
/// </summary>
internal static class GeneratedFiles
{
    /// <summary>The folder of the project that contains the generated files. Its C# files are replaced when the project is updated.</summary>
    public const string FolderName = "Generated";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Replaces the C# files of the <c>Generated</c> folder with the documents.
    /// </summary>
    /// <returns>The paths of the written files.</returns>
    public static IReadOnlyList<string> Write(string projectDirectory, IReadOnlyList<GeneratedDocument> documents)
    {
        string folder = Path.Combine(projectDirectory, FolderName);
        Clean(folder);

        var paths = new List<string>(documents.Count);
        foreach (var document in documents)
        {
            string path = Path.GetFullPath(Path.Combine(folder, document.RelativePath));
            if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The generated file '{document.RelativePath}' is outside of the '{FolderName}' folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, document.GetText(), Utf8WithBom);
            paths.Add(path);
        }

        return paths;
    }

    private static void Clean(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
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
