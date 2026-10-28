using System;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals <see cref="ComBstr"/> as <c>BSTR</c> pointers, following the COM ownership rules.
/// </summary>
[CustomMarshaller(typeof(ComBstr), MarshalMode.ManagedToUnmanagedIn, typeof(ManagedToUnmanagedIn))]
[CustomMarshaller(typeof(ComBstr), MarshalMode.ManagedToUnmanagedOut, typeof(ManagedToUnmanagedOut))]
[CustomMarshaller(typeof(ComBstr), MarshalMode.ManagedToUnmanagedRef, typeof(ManagedToUnmanagedRef))]
[CustomMarshaller(typeof(ComBstr), MarshalMode.UnmanagedToManagedIn, typeof(UnmanagedToManagedIn))]
[CustomMarshaller(typeof(ComBstr), MarshalMode.UnmanagedToManagedOut, typeof(UnmanagedToManagedOut))]
[CustomMarshaller(typeof(ComBstr), MarshalMode.UnmanagedToManagedRef, typeof(UnmanagedToManagedRef))]
public static class ComBstrMarshaller
{
    /// <summary>Passes a string to a COM method; the caller keeps owning it.</summary>
    public struct ManagedToUnmanagedIn
    {
        private ComBstr? _managed;

        /// <summary>Takes the string.</summary>
        public void FromManaged(ComBstr? managed) => _managed = managed;

        /// <summary>Gets the <c>BSTR</c> pointer.</summary>
        public readonly nint ToUnmanaged() => _managed?.Pointer ?? 0;

        /// <summary>Keeps the string alive until the call returns, so that it is not finalized during the call.</summary>
        public readonly void Free() => GC.KeepAlive(_managed);
    }

    /// <summary>Receives a string returned by a COM method; the caller owns it.</summary>
    public static class ManagedToUnmanagedOut
    {
        /// <summary>Wraps the returned <c>BSTR</c>.</summary>
        public static ComBstr? ConvertToManaged(nint unmanaged) => ComBstr.FromPointer(unmanaged, ownsString: true);
    }

    /// <summary>
    /// Passes a string by reference to a COM method, which can replace it. A replaced string was freed by the callee, so its
    /// wrapper is detached.
    /// </summary>
    public struct ManagedToUnmanagedRef
    {
        private ComBstr? _original;
        private nint _originalPointer;
        private nint _unmanaged;

        /// <summary>Takes the string.</summary>
        public void FromManaged(ComBstr? managed)
        {
            _original = managed;
            _originalPointer = managed?.Pointer ?? 0;
        }

        /// <summary>Gets the <c>BSTR</c> pointer.</summary>
        public readonly nint ToUnmanaged() => _originalPointer;

        /// <summary>Takes the <c>BSTR</c> pointer returned by the callee.</summary>
        public void FromUnmanaged(nint unmanaged) => _unmanaged = unmanaged;

        /// <summary>Gets the original wrapper if the string was not replaced, otherwise a wrapper that owns the new string.</summary>
        public readonly ComBstr? ToManaged()
        {
            if (_unmanaged == _originalPointer)
            {
                return _original;
            }

            _original?.Forget();
            return ComBstr.FromPointer(_unmanaged, ownsString: true);
        }

        /// <summary>Keeps the string alive until the call returns.</summary>
        public readonly void Free() => GC.KeepAlive(_original);
    }

    /// <summary>Receives a string passed to a COM object implemented in .NET: a view that is valid during the call.</summary>
    public struct UnmanagedToManagedIn
    {
        private ComBstr? _view;

        /// <summary>Wraps the <c>BSTR</c> pointer.</summary>
        public void FromUnmanaged(nint unmanaged) => _view = ComBstr.FromPointer(unmanaged, ownsString: false);

        /// <summary>Gets the view.</summary>
        public readonly ComBstr? ToManaged() => _view;

        /// <summary>Invalidates the view after the call.</summary>
        public readonly void Free() => _view?.Forget();
    }

    /// <summary>
    /// Returns a string from a COM object implemented in .NET; the caller owns it. An owned string is detached, a view is copied.
    /// </summary>
    public static class UnmanagedToManagedOut
    {
        /// <summary>Gets the <c>BSTR</c> pointer whose ownership is transferred to the caller.</summary>
        public static nint ConvertToUnmanaged(ComBstr? managed) =>
            managed is null ? 0 : managed.OwnsString ? managed.Detach() : ComBstr.Copy(managed.Pointer);
    }

    /// <summary>
    /// Receives a string by reference in a COM object implemented in .NET, which can replace it. A replaced string is freed,
    /// as the callee of an <c>[in, out]</c> parameter must do.
    /// </summary>
    public struct UnmanagedToManagedRef
    {
        private nint _original;
        private ComBstr? _view;
        private ComBstr? _managed;

        /// <summary>Wraps the <c>BSTR</c> pointer passed by the caller.</summary>
        public void FromUnmanaged(nint unmanaged)
        {
            _original = unmanaged;
            _view = ComBstr.FromPointer(unmanaged, ownsString: false);
        }

        /// <summary>Gets a view of the string passed by the caller.</summary>
        public readonly ComBstr? ToManaged() => _view;

        /// <summary>Takes the string returned by the method.</summary>
        public void FromManaged(ComBstr? managed) => _managed = managed;

        /// <summary>Gets the <c>BSTR</c> pointer returned to the caller.</summary>
        public readonly nint ToUnmanaged()
        {
            // The string was not replaced, possibly passed back through another view.
            if (_managed is not null && _managed.PointerOrZero == _original && _original != 0)
            {
                return _original;
            }

            nint result = _managed is null ? 0 : _managed.OwnsString ? _managed.Detach() : ComBstr.Copy(_managed.Pointer);
            if (_original != 0)
            {
                _view?.Forget();
                OleNativeMethods.SysFreeString(_original);
            }

            return result;
        }

        /// <summary>Invalidates the view after the call.</summary>
        public readonly void Free() => _view?.Forget();
    }
}
