namespace CsCom.CodeGeneration;

/// <summary>
/// The code generated for the event (source) interfaces of coclasses.
/// </summary>
public enum EventStyle
{
    /// <summary>A sink class with a .NET event for each event of the interface, and a handler interface.</summary>
    Events,

    /// <summary>A sink class with an <see cref="System.IObservable{T}"/> for each event of the interface, and a handler interface.</summary>
    Observable,

    /// <summary>A handler interface, which is implemented and connected to the events.</summary>
    Interface,

    /// <summary>No code for the events.</summary>
    None,
}
