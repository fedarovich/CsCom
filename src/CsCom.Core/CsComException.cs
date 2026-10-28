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
    }

    /// <summary>Initializes a new instance of the <see cref="CsComException"/> class.</summary>
    public CsComException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
