using Microsoft.Build.Framework;

namespace CsCom.SupportCode;

/// <summary>
/// Checks the sources of CsCom.Runtime and CsCom.Runtime.Dynamic and writes their copies whose names of .NET are
/// qualified, which CsCom.Core embeds as its support code (see <see cref="SupportCodeBuilder"/>).
/// </summary>
/// <remarks>
/// CsCom.Core runs it in a task host of .NET (<c>TaskHostFactory</c>, <c>Runtime="NET"</c>): in the MSBuild node, the
/// assembly would stay loaded after the build, so that the next build could not replace it, and Visual Studio runs
/// MSBuild on .NET Framework, with a Roslyn of its own.
/// </remarks>
public sealed class QualifySupportCode : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Gets or sets the sources, with the logical names under which CsCom.Core embeds them (<c>LogicalName</c>).
    /// </summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets the assemblies that the sources are bound against: the compile references of CsCom.Runtime.
    /// </summary>
    [Required]
    public ITaskItem[] References { get; set; } = [];

    /// <summary>Gets or sets the directory that receives the qualified copies, at their logical names.</summary>
    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    /// <inheritdoc/>
    public override bool Execute()
    {
        var errors = SupportCodeBuilder.Build(
            [.. Sources.Select(static s => (s.GetMetadata("FullPath"), s.GetMetadata("LogicalName")))],
            [.. References.Select(static r => r.GetMetadata("FullPath"))],
            Path.GetFullPath(OutputDirectory));
        foreach (var error in errors)
        {
            Log.LogError(null, error.Code, null, error.File, error.Line, error.Column, 0, 0, error.Message);
        }

        return !Log.HasLoggedErrors;
    }
}
