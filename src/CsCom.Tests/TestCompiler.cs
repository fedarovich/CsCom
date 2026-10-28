using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;

namespace CsCom.Tests;

/// <summary>
/// Compiles test assemblies, e.g. interop assemblies with hand-written [ComImport] declarations, in memory.
/// </summary>
internal static class TestCompiler
{
    /// <summary>
    /// Compiles C# source into a library, written to <paramref name="path"/> if specified.
    /// </summary>
    public static MemoryStream Compile(string name, string source, string? path = null, MemoryStream? reference = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(static f => Path.GetFileName(f).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(f) is "mscorlib.dll" or "netstandard.dll")
            .Select(static f => MetadataReference.CreateFromFile(f))
            .ToList<MetadataReference>();
        if (reference is not null)
        {
            references.Add(MetadataReference.CreateFromImage(reference.ToArray()));
        }

        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        if (path is not null)
        {
            File.WriteAllBytes(path, stream.ToArray());
        }

        return stream;
    }
}

/// <summary>
/// A logger that collects the messages of warnings and errors.
/// </summary>
internal sealed class WarningCollector<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
