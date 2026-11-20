using System.Text;
using CsCom.CodeGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsCom.SupportCode;

/// <summary>
/// Checks the support code files and writes their copies whose names of .NET are qualified (see
/// <see cref="SupportCodeQualifier"/>), which CsCom.Core embeds.
/// </summary>
internal static class SupportCodeBuilder
{
    /// <summary>The code of the errors of the layout of a file (see <see cref="SupportCodeLayout"/>).</summary>
    public const string LayoutErrorCode = "CSCOMSC001";

    /// <summary>
    /// Checks and qualifies the support code files, and writes the qualified copies if they have no errors.
    /// </summary>
    /// <param name="sources">
    /// The full paths of the files, and their logical names, under which CsCom.Core embeds them, e.g.
    /// <c>CsCom.Runtime/ComMarshalling.cs</c>.
    /// </param>
    /// <param name="references">The full paths of the assemblies that the files are bound against.</param>
    /// <param name="outputDirectory">
    /// The full path of the directory that receives the copies, at their logical names; its other files are deleted.
    /// </param>
    /// <returns>The errors; none if the copies were written.</returns>
    public static IReadOnlyList<SupportCodeError> Build(
        IReadOnlyList<(string Path, string LogicalName)> sources, IReadOnlyList<string> references, string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(references);
        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new ArgumentException($"The output directory '{outputDirectory}' is not a full path.", nameof(outputDirectory));
        }

        // The files are parsed as C# 14, which the support code is bound as, with their documentation comments, whose crefs
        // are qualified too. The layout is checked first: a directive could exclude code from the binding.
        var options = CSharpParseOptions.Default.WithLanguageVersion(SupportCodeQualifier.BindingLanguageVersion);
        var errors = new List<SupportCodeError>();
        var runtimeRoots = new List<(string LogicalName, CompilationUnitSyntax Root)>();
        var dynamicRoots = new List<(string LogicalName, CompilationUnitSyntax Root)>();
        foreach (var (path, logicalName) in sources.OrderBy(static s => s.LogicalName, StringComparer.Ordinal))
        {
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path).GetRoot();
            if (SupportCodeLayout.FindViolation(logicalName, root) is { } violation)
            {
                errors.Add(SupportCodeError.At(violation.Node.GetLocation(), LayoutErrorCode, violation.Message));
            }
            else if (logicalName.StartsWith(SupportCodeLayout.DynamicPrefix, StringComparison.Ordinal))
            {
                dynamicRoots.Add((logicalName, root));
            }
            else if (logicalName.StartsWith(SupportCodeLayout.RuntimePrefix, StringComparison.Ordinal))
            {
                runtimeRoots.Add((logicalName, root));
            }
            else
            {
                errors.Add(new SupportCodeError(path, 0, 0, LayoutErrorCode,
                    $"The support code file {path} has the logical name '{logicalName}', which is neither in {SupportCodeLayout.RuntimePrefix} nor in {SupportCodeLayout.DynamicPrefix}."));
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        // CsCom.Runtime is bound alone, as the package is built, so that no type of CsCom.Runtime.Dynamic, which shares its
        // namespace, can take a name of .NET in it, which a generated project without dynamic would find in .NET. The
        // files of CsCom.Runtime.Dynamic are bound with those of CsCom.Runtime, whose public API they use.
        var metadata = references.Select(static r => (MetadataReference)MetadataReference.CreateFromFile(r)).ToList();
        var runtimeFiles = SupportCodeQualifier.Qualify([.. runtimeRoots.Select(static f => f.Root)], [], metadata);
        var dynamicFiles = dynamicRoots.Count == 0
            ? new QualifiedFiles([], [])
            : SupportCodeQualifier.Qualify([.. dynamicRoots.Select(static f => f.Root)], [.. runtimeRoots.Select(static f => f.Root)], metadata);
        errors.AddRange(runtimeFiles.Errors.Concat(dynamicFiles.Errors).Select(static d => SupportCodeError.At(d.Location, d.Id, d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))));
        if (errors.Count > 0)
        {
            return errors;
        }

        // A file that was deleted or renamed leaves no copy behind. The line breaks are those of the sources, which
        // SupportCodeProvider replaces when it reads them.
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, recursive: true);
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        foreach (var (logicalName, text) in runtimeRoots.Select(static f => f.LogicalName).Zip(runtimeFiles.Texts)
            .Concat(dynamicRoots.Select(static f => f.LogicalName).Zip(dynamicFiles.Texts)))
        {
            string path = Path.Combine(outputDirectory, logicalName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, encoding);
        }

        return errors;
    }
}

/// <summary>An error of a support code file.</summary>
/// <param name="File">The full path of the file.</param>
/// <param name="Line">The line of the error, starting at 1; 0 for the whole file.</param>
/// <param name="Column">The column of the error, starting at 1; 0 for the whole line.</param>
/// <param name="Code">The code of the error, e.g. the identifier of a diagnostic of C#.</param>
/// <param name="Message">The message.</param>
internal sealed record SupportCodeError(string File, int Line, int Column, string Code, string Message)
{
    /// <summary>Creates an error at a location in a file.</summary>
    public static SupportCodeError At(Location location, string code, string message)
    {
        var span = location.GetLineSpan();
        return span.IsValid
            ? new SupportCodeError(span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, code, message)
            : new SupportCodeError(string.Empty, 0, 0, code, message);
    }
}
