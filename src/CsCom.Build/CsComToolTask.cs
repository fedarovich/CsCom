using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace CsCom.Build;

/// <summary>
/// The base of the tasks that run a hidden command of the bundled cscom tool, which runs on .NET 10 as a separate process,
/// unless the result of a previous build is up to date.
/// </summary>
/// <remarks>
/// A task writes the items and the properties into a JSON request file, which the command reads. The tool records the hash of
/// the request, the files it read, the files it wrote and its warnings in a state file; when the hash is the same and none of
/// these files changed, the tool is not run and the warnings are reported again.
/// </remarks>
public abstract class CsComToolTask : ToolTask
{
    /// <summary>The version of the task, which is that of the tool.</summary>
    private protected static readonly string Version = GetVersion();

    private string _commandLine = string.Empty;

    /// <summary>Gets or sets the directory of the project; relative paths are relative to it.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the path of the assembly of the cscom tool.</summary>
    [Required]
    public string CsComPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the path of the <c>dotnet</c> executable that runs the tool.</summary>
    public string DotNetHostPath { get; set; } = string.Empty;

    /// <inheritdoc/>
    protected override string ToolName => "dotnet.exe";

    /// <inheritdoc/>
    protected override MessageImportance StandardOutputLoggingImportance => MessageImportance.Low;

    /// <inheritdoc/>
    protected override Encoding StandardOutputEncoding => Encoding.UTF8;

    /// <summary>
    /// Creates the item of a file. MSBuild takes the specification of an item for an escaped one and replaces its
    /// <c>%xx</c> sequences with characters, e.g. in the path of a project in a folder named <c>My%20Project</c>, so the
    /// percent signs of the path are escaped.
    /// </summary>
    internal static ITaskItem CreateItem(string path) => new TaskItem(path.Replace("%", "%25"));

    /// <summary>
    /// Gets the files that the tool wrote for the request, running its command unless the state file tells that they are
    /// up to date.
    /// </summary>
    /// <param name="command">The hidden command of the tool.</param>
    /// <param name="request">The JSON of the request.</param>
    /// <param name="requestPath">The file that the request is written to for the tool.</param>
    /// <param name="statePath">The state file that the tool writes.</param>
    /// <param name="upToDate">The message of a result that is up to date.</param>
    /// <returns>The files, or <see langword="null"/> if the tool failed.</returns>
    private protected List<string>? RunTool(string command, string request, string requestPath, string statePath, string upToDate)
    {
        string hash = ComputeHash(request);
        var files = BuildState.ReadGeneratedFiles(statePath, hash, out var warnings);
        if (files is null)
        {
            File.WriteAllText(requestPath, request, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _commandLine = $"exec \"{CsComPath}\" {command} \"{requestPath}\" {hash}";
            if (!base.Execute())
            {
                return null;
            }

            // Not ReadGeneratedFiles: an input that was written while the tool ran makes the next build run it again, and
            // does not fail this one.
            files = BuildState.ReadRecordedFiles(statePath, hash);
            if (files is null)
            {
                Log.LogError("CsCom: the cscom tool did not record its result in '{0}'.", statePath);
            }

            return files;
        }

        Log.LogMessage(MessageImportance.Normal, upToDate);

        // The warnings of the tool are reported by every build, like those of the compiler, and not only by the build that
        // ran it.
        foreach (string warning in warnings)
        {
            Log.LogMessageFromText(warning, MessageImportance.High);
        }

        return files;
    }

    /// <inheritdoc/>
    protected override string GenerateFullPathToTool()
    {
        if (!string.IsNullOrEmpty(DotNetHostPath) && File.Exists(DotNetHostPath))
        {
            return DotNetHostPath;
        }

        string? path = Environment.GetEnvironmentVariable("PATH");
        return path?.Split(Path.PathSeparator)
            .Where(static directory => directory.Length > 0)
            .Select(directory => Path.Combine(directory.Trim('"'), ToolName))
            .FirstOrDefault(File.Exists) ?? ToolName;
    }

    /// <inheritdoc/>
    protected override string GenerateCommandLineCommands() => _commandLine;

    /// <inheritdoc/>
    protected override string GetWorkingDirectory() => ProjectDirectory;

    /// <inheritdoc/>
    protected override bool HandleTaskExecutionErrors() => !Log.HasLoggedErrors && base.HandleTaskExecutionErrors();

    private static string ComputeHash(string request)
    {
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(request));
        return string.Concat(hash.Select(static b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private static string GetVersion()
    {
        string? version = typeof(CsComToolTask).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version is null ? string.Empty : version.Split('+')[0];
    }
}
