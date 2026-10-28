using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace CsCom;

/// <summary>
/// Formats log messages as <c>level: message</c>, which suits a command line tool better than the default console format.
/// </summary>
internal sealed class CompactConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "cscom";

    private const string DefaultForeground = "\x1B[39m\x1B[22m";
    private const string Red = "\x1B[1m\x1B[31m";
    private const string Yellow = "\x1B[1m\x1B[33m";
    private const string Gray = "\x1B[90m";

    private readonly bool _useColors = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

    public CompactConsoleFormatter()
        : base(FormatterName)
    {
    }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        string message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        (string? prefix, string? color) = logEntry.LogLevel switch
        {
            LogLevel.Trace or LogLevel.Debug => (null, Gray),
            LogLevel.Warning => ("warning: ", Yellow),
            LogLevel.Error or LogLevel.Critical => ("error: ", Red),
            _ => ((string?)null, (string?)null),
        };

        if (_useColors && color is not null)
        {
            textWriter.Write(color);
        }

        textWriter.Write(prefix);
        textWriter.Write(message);
        if (logEntry.Exception is not null && logEntry.LogLevel == LogLevel.Critical)
        {
            textWriter.WriteLine();
            textWriter.Write(logEntry.Exception);
        }

        if (_useColors && color is not null)
        {
            textWriter.Write(DefaultForeground);
        }

        textWriter.WriteLine();
    }
}
