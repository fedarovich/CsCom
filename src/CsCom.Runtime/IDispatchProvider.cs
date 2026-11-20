namespace CsCom.Runtime;

/// <summary>
/// Implemented by the objects that wrap an <see cref="IDispatch"/> interface: the late-bound dispinterface wrappers and
/// the <c>dynamic</c> objects. The marshallers pass the wrapped interface instead of the wrapper.
/// </summary>
/// <remarks>
/// <para>
/// A late-bound or <c>dynamic</c> wrapper, which is not exposed to COM itself (see
/// <see cref="ComMarshalling.IsExposedToCom"/>), stands for the COM object of its <see cref="Dispatch"/> everywhere:
/// the support code passes that object instead of the wrapper, in a <c>VARIANT</c>, a <c>SAFEARRAY</c> and an
/// interface parameter alike, also of a COM interface that the wrapper implements itself, which that object is asked
/// for, <see cref="ComEnumerator.FromObject(object, bool)"/> enumerates it, and
/// <see cref="ComMarshalling.Release"/> releases it. A wrapper whose <see cref="Dispatch"/> is <see langword="null"/>,
/// against the contract, stands for no object, like <see langword="null"/>: it is converted to <see langword="null"/>,
/// and rejected where an object is required. A wrapper whose <see cref="Dispatch"/> is an object of the application
/// that is exposed to COM, e.g. a decorator that a call returned, stands for that object, with its rule below.
/// </para>
/// <para>
/// An object of the application that is exposed to COM is passed itself, also when it implements this interface, so
/// that its pointer converts back to it, and it is never released. It stands for the COM object of its
/// <see cref="Dispatch"/> only where an interface is required that it does not implement: <c>IDispatch</c> in a
/// <c>SAFEARRAY</c> of <c>IDispatch</c>, in a dispinterface wrapper and for <c>dynamic</c>, the interface of a
/// conversion of a late-bound wrapper, a <c>SAFEARRAY</c> or an event argument, that of a parameter that a late-bound
/// wrapper of the object is passed to, <c>IEnumVARIANT</c> in <see cref="ComEnumerator.FromObject(object, bool)"/>, and
/// the source of events. A conversion to an interface asks its COM interface first, as its pointer would be asked, e.g.
/// for another declaration of the interface that its class implements. One whose <see cref="Dispatch"/> is
/// <see langword="null"/> provides none, and does not implement the interface there.
/// </para>
/// </remarks>
public interface IDispatchProvider
{
    /// <summary>
    /// Gets the wrapped <see cref="IDispatch"/> interface.
    /// </summary>
    IDispatch Dispatch { get; }
}
