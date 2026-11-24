using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCom.Model;
using CsCom.Output;

namespace CsCom.Tests;

/// <summary>
/// Describes models as text, to compare the models that are read from different sources.
/// </summary>
internal static partial class ModelDescriber
{
    /// <summary>
    /// The listing of <c>cscom list</c> by type, with the identifiers, flags and layouts that it does not show. The names of
    /// the [retval] parameters, which interop assemblies do not keep, are replaced.
    /// </summary>
    /// <param name="library">The model.</param>
    /// <param name="detailed">Whether to add the help strings and the identifiers of the members of vtable interfaces.</param>
    public static Dictionary<string, List<string>> Describe(TypeLibrary library, bool detailed = false)
    {
        using var writer = new StringWriter();
        ModelPrinter.Write(library, writer);

        // The listing has the types in the order of the model, each with a header that is not indented.
        var blocks = new Dictionary<string, List<string>>();
        int index = -1;
        List<string>? block = null;
        foreach (string line in writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(' '))
            {
                blocks[library.Types[++index].Name] = block = [line];
            }
            else
            {
                block!.Add(detailed ? line : RetvalName().Replace(line, "[out, retval] $1 retval)"));
            }
        }

        foreach (var type in library.Types)
        {
            var lines = blocks[type.Name];
            lines.Add($"    ~ guid {type.Guid?.ToString() ?? "none"}, flags {type.Flags}");
            if (detailed && type.Documentation is not null)
            {
                lines.Add($"    ~ help {type.Documentation}");
            }

            switch (type)
            {
                case InterfaceDefinition @interface:
                    lines.Add($"    ~ base slots {@interface.BaseVtableSlots}");
                    lines.AddRange(@interface.Methods.Select(m => @interface.InterfaceKind == InterfaceKind.Vtable && !detailed
                        ? $"    ~ {m.InvokeKind} {m.Name}: slot {m.VtableSlot}, {m.Flags}"
                        : $"    ~ {m.InvokeKind} {m.Name}: id {m.MemberId.ToString(CultureInfo.InvariantCulture)}, slot {m.VtableSlot}, {m.Flags}{Help(m.Documentation)}"));
                    lines.AddRange(@interface.Properties.Select(p => $"    ~ property {p.Name}: id {p.DispId.ToString(CultureInfo.InvariantCulture)}, {p.Flags}{Help(p.Documentation)}"));
                    break;
                case RecordDefinition record:
                    lines.Add($"    ~ size {record.Size}, alignment {record.Alignment}, offsets {string.Join(" ", record.Fields.Select(static f => f.Offset))}");
                    break;
                case ModuleDefinition module when detailed:
                    lines.AddRange(module.Functions.Select(static f => $"    ~ {f.Name}: {f.DllName} {f.EntryPoint ?? "#" + f.EntryPointOrdinal}, {f.CallingConvention}"));
                    break;
            }
        }

        return blocks;

        string Help(string? documentation) => detailed && documentation is not null ? $", help {documentation}" : string.Empty;
    }

    /// <summary>Describes the library itself.</summary>
    public static string DescribeLibrary(TypeLibrary library) =>
        $"library {library.Name} {library.MajorVersion}.{library.MinorVersion} {library.Guid}, lcid {library.Lcid}, help {library.Documentation ?? "none"}, " +
        $"imports {string.Join(" ", library.ImportedLibraries.Select(static l => $"{l.Name} {l.MajorVersion}.{l.MinorVersion}").Order(StringComparer.Ordinal))}";

    /// <summary>Lists the lines that only the first (-) or only the second (+) description has.</summary>
    public static string Compare(Dictionary<string, List<string>> first, Dictionary<string, List<string>> second)
    {
        var report = new StringBuilder();
        foreach (string key in first.Keys.Union(second.Keys).Order(StringComparer.Ordinal))
        {
            var removed = Except(first.GetValueOrDefault(key) ?? [], second.GetValueOrDefault(key) ?? []);
            var added = Except(second.GetValueOrDefault(key) ?? [], first.GetValueOrDefault(key) ?? []);
            if (removed.Count == 0 && added.Count == 0)
            {
                continue;
            }

            report.AppendLine(key);
            removed.ForEach(line => report.AppendLine("- " + line.Trim()));
            added.ForEach(line => report.AppendLine("+ " + line.Trim()));
        }

        return report.ToString();
    }

    private static List<string> Except(List<string> lines, List<string> others)
    {
        var remaining = others.GroupBy(static l => l).ToDictionary(static g => g.Key, static g => g.Count());
        var result = new List<string>();
        foreach (string line in lines)
        {
            if (remaining.TryGetValue(line, out int count) && count > 0)
            {
                remaining[line] = count - 1;
            }
            else
            {
                result.Add(line);
            }
        }

        return result;
    }

    [GeneratedRegex(@"\[out, retval\] (\S+) \w+\)")]
    private static partial Regex RetvalName();
}
