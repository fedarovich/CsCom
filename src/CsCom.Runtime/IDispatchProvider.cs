namespace CsCom.Runtime;

/// <summary>
/// Implemented by the objects that wrap an <see cref="IDispatch"/> interface: the late-bound dispinterface wrappers and
/// the <c>dynamic</c> objects. The marshallers pass the wrapped interface instead of the wrapper.
/// </summary>
internal interface IDispatchProvider
{
    /// <summary>
    /// Gets the wrapped <see cref="IDispatch"/> interface.
    /// </summary>
    IDispatch Dispatch { get; }
}
