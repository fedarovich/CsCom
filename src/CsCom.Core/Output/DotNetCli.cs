using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CsCom.Output;

/// <summary>
/// Runs the <c>dotnet</c> command line.
/// </summary>
internal static partial class DotNetCli
{
    // Set by a parent MSBuild or test host; they would make the child build use the parent's MSBuild.
    private static readonly string[] InheritedVariables =
        ["MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly", "MSBUILDNOINPROCNODE"];

    /// <summary>
    /// Runs <c>dotnet</c> with the arguments and logs its output.
    /// </summary>
    /// <returns>The exit code and the output lines.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled; the process and its child processes are stopped.</exception>
    public static (int ExitCode, IReadOnlyList<string> Output) Run(
        IReadOnlyList<string> arguments, string workingDirectory, ILogger logger, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(GetDotNetPath())
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (string variable in InheritedVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        // The worker nodes of a build that uses several, e.g. for several target frameworks, inherit the redirected output, and
        // MSBuild keeps them running for later builds: the output would only end, and WaitForExit return, minutes after the build.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        LogRunning(logger, string.Join(' ', arguments));
        var output = new List<string>();
        using var process = new Process { StartInfo = startInfo };
        void OnData(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.Add(e.Data);
            }

            LogOutput(logger, e.Data);
        }

        process.OutputDataReceived += OnData;
        process.ErrorDataReceived += OnData;
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new CsComException($"Failed to run '{startInfo.FileName}'. Make sure the .NET SDK is installed: {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // A cancelled build is stopped with the compilers and the other processes that it started, which would otherwise
        // keep the files of the project open.
        using (cancellationToken.Register(() => Kill(process)))
        {
            process.WaitForExit();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (process.ExitCode, output);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException)
        {
            // The process has exited, or a child process cannot be stopped.
        }
    }

    private static string GetDotNetPath() =>
        FindDotNet(Environment.GetEnvironmentVariable, File.Exists)
            ?? throw new CsComException("The dotnet executable of the .NET SDK was not found: it is not in the directories of the PATH variable.");

    /// <summary>
    /// Finds the <c>dotnet</c> executable: <c>DOTNET_HOST_PATH</c>, which the .NET CLI sets, then <c>DOTNET_ROOT</c>, then the
    /// directories of <c>PATH</c>. The full path is returned, since Windows looks for a bare name in the current directory
    /// first, which can be a directory of someone else.
    /// </summary>
    internal static string? FindDotNet(Func<string, string?> getVariable, Func<string, bool> exists)
    {
        if (getVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && Path.IsPathFullyQualified(host) && exists(host))
        {
            return host;
        }

        IEnumerable<string> directories = (getVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Prepend(getVariable("DOTNET_ROOT") ?? string.Empty);
        return directories
            .Select(static directory => directory.Trim().Trim('"'))
            .Where(static directory => directory.Length > 0 && Path.IsPathFullyQualified(directory))
            .Select(static directory => Path.Combine(directory, "dotnet.exe"))
            .FirstOrDefault(exists);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Running dotnet {Arguments}")]
    private static partial void LogRunning(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Line}")]
    private static partial void LogOutput(ILogger logger, string line);
}
