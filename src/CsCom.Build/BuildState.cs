using System.Globalization;

namespace CsCom.Build;

/// <summary>
/// Reads the state file that the generator writes after a successful generation, see CsCom.LibrarySets.LibrarySetBuild:
/// <c>hash=</c> the hash of the request, <c>input=</c> the last write time of an input file in ticks, <c>|</c> and its path,
/// and <c>output=</c> the path of a generated file.
/// </summary>
internal static class BuildState
{
    /// <summary>
    /// Gets the generated files if they are up to date: the request has the same hash, the input files did not change and
    /// the generated files exist.
    /// </summary>
    /// <returns>The generated files, or <see langword="null"/> if the code has to be generated.</returns>
    public static List<string>? ReadGeneratedFiles(string path, string hash)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        bool sameRequest = false;
        var files = new List<string>();
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith("hash=", StringComparison.Ordinal))
            {
                sameRequest = string.Equals(line.Substring("hash=".Length), hash, StringComparison.Ordinal);
            }
            else if (line.StartsWith("input=", StringComparison.Ordinal))
            {
                int separator = line.IndexOf('|');
                if (separator < 0
                    || !long.TryParse(line.Substring("input=".Length, separator - "input=".Length), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                {
                    return null;
                }

                string input = GetPathOfThisProcess(line.Substring(separator + 1));
                if (!File.Exists(input) || File.GetLastWriteTimeUtc(input).Ticks != ticks)
                {
                    return null;
                }
            }
            else if (line.StartsWith("output=", StringComparison.Ordinal))
            {
                string output = line.Substring("output=".Length);
                if (!File.Exists(output))
                {
                    return null;
                }

                files.Add(output);
            }
        }

        return sameRequest ? files : null;
    }

    // The generator runs as a 64-bit process, while the MSBuild of Visual Studio can be a 32-bit process, for which System32
    // is redirected to SysWOW64, e.g. for scrrun.dll; Sysnative gives it the files that the generator read.
    private static string GetPathOfThisProcess(string path)
    {
        if (Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
        {
            return path;
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string system32 = Path.Combine(windows, "System32") + Path.DirectorySeparatorChar;
        return path.StartsWith(system32, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(windows, "Sysnative", path.Substring(system32.Length))
            : path;
    }
}
