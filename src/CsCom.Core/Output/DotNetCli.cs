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
    public static (int ExitCode, IReadOnlyList<string> Output) Run(IReadOnlyList<string> arguments, string workingDirectory, ILogger logger)
    {
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
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    private static string GetDotNetPath() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } path && File.Exists(path) ? path : "dotnet";

    [LoggerMessage(Level = LogLevel.Information, Message = "Running dotnet {Arguments}")]
    private static partial void LogRunning(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Line}")]
    private static partial void LogOutput(ILogger logger, string line);
}
