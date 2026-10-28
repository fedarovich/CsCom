using Microsoft.Extensions.Logging;

namespace CsCom.LibrarySets;

/// <summary>
/// Formats the messages of the generator in the canonical format of MSBuild, e.g.
/// <c>cscom.xml(3,6): error CSCOM1000: Unknown model transformer &lt;Rename&gt;.</c>, which MSBuild reports as errors and warnings.
/// </summary>
public static class MSBuildOutput
{
    /// <summary>The code of the errors.</summary>
    public const string ErrorCode = "CSCOM1000";

    /// <summary>The code of the warnings.</summary>
    public const string WarningCode = "CSCOM2000";

    private const string Origin = "CsCom";

    /// <summary>
    /// Formats an error, with the location of a <see cref="CsComException"/> in a file if it has one.
    /// </summary>
    public static string FormatError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            CsComException { FilePath: { } path, Line: > 0 } e => $"{path}({e.Line},{e.Column}): error {ErrorCode}: {OneLine(e.Detail)}",
            CsComException { FilePath: { } path } e => $"{path}: error {ErrorCode}: {OneLine(e.Detail)}",
            CsComException or IOException or UnauthorizedAccessException => $"{Origin} : error {ErrorCode}: {OneLine(exception.Message)}",
            _ => $"{Origin} : error {ErrorCode}: Unexpected error: {OneLine(exception.ToString())}",
        };
    }

    /// <summary>Formats a warning.</summary>
    public static string FormatWarning(string message) => $"{Origin} : warning {WarningCode}: {OneLine(message)}";

    // MSBuild recognizes a message on a single line.
    private static string OneLine(string message) => message.ReplaceLineEndings(" ");

    /// <summary>
    /// A logger provider that writes the warnings and errors in the canonical format of MSBuild and the other messages as they are.
    /// </summary>
    public sealed class LoggerProvider(TextWriter writer, LogLevel minimumLevel) : ILoggerProvider
    {
        /// <inheritdoc/>
        public ILogger CreateLogger(string categoryName) => new Logger(writer, minimumLevel);

        /// <inheritdoc/>
        public void Dispose()
        {
        }

        private sealed class Logger(TextWriter writer, LogLevel minimumLevel) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                string message = formatter(state, exception);
                string line = logLevel switch
                {
                    >= LogLevel.Error => $"{Origin} : error {ErrorCode}: {OneLine(message)}",
                    LogLevel.Warning => FormatWarning(message),
                    _ => message,
                };

                lock (writer)
                {
                    writer.WriteLine(line);
                }
            }
        }
    }
}
