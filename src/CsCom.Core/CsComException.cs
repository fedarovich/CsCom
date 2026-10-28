namespace CsCom;

/// <summary>
/// An error caused by invalid input, reported to the user without a stack trace.
/// </summary>
public class CsComException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="CsComException"/> class.</summary>
    public CsComException(string message)
        : base(message)
    {
        Detail = message;
    }

    /// <summary>Initializes a new instance of the <see cref="CsComException"/> class.</summary>
    public CsComException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Detail = message;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CsComException"/> class for an error in a file, e.g. a configuration file;
    /// the message starts with the location.
    /// </summary>
    internal CsComException(string location, string detail, string? filePath, int line, int column, Exception? innerException)
        : base(location + detail, innerException)
    {
        Detail = detail;
        FilePath = filePath;
        Line = line;
        Column = column;
    }

    /// <summary>The message without the location of the error.</summary>
    public string Detail { get; }

    /// <summary>The file that contains the error, if any.</summary>
    public string? FilePath { get; }

    /// <summary>The line of the error in <see cref="FilePath"/>, or 0 if it is unknown.</summary>
    public int Line { get; }

    /// <summary>The column of the error in <see cref="FilePath"/>, or 0 if it is unknown.</summary>
    public int Column { get; }
}
