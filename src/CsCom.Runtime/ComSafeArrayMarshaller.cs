using System;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals <see cref="ComSafeArray{T}"/> as <c>SAFEARRAY</c> pointers, following the COM ownership rules.
/// </summary>
/// <typeparam name="T">The type of the elements.</typeparam>
[CustomMarshaller(typeof(ComSafeArray<>), MarshalMode.ManagedToUnmanagedIn, typeof(ComSafeArrayMarshaller<>.ManagedToUnmanagedIn))]
[CustomMarshaller(typeof(ComSafeArray<>), MarshalMode.ManagedToUnmanagedOut, typeof(ComSafeArrayMarshaller<>.ManagedToUnmanagedOut))]
[CustomMarshaller(typeof(ComSafeArray<>), MarshalMode.ManagedToUnmanagedRef, typeof(ComSafeArrayMarshaller<>.ManagedToUnmanagedRef))]
[CustomMarshaller(typeof(ComSafeArray<>), MarshalMode.UnmanagedToManagedIn, typeof(ComSafeArrayMarshaller<>.UnmanagedToManagedIn))]
[CustomMarshaller(typeof(ComSafeArray<>), MarshalMode.UnmanagedToManagedOut, typeof(ComSafeArrayMarshaller<>.UnmanagedToManagedOut))]
[CustomMarshaller(typeof(ComSafeArray<>), MarshalMode.UnmanagedToManagedRef, typeof(ComSafeArrayMarshaller<>.UnmanagedToManagedRef))]
public static class ComSafeArrayMarshaller<T>
{
    /// <summary>Passes an array to a COM method; the caller keeps owning it.</summary>
    public struct ManagedToUnmanagedIn
    {
        private ComSafeArray<T>? _managed;

        /// <summary>Takes the array.</summary>
        public void FromManaged(ComSafeArray<T>? managed) => _managed = managed;

        /// <summary>Gets the <c>SAFEARRAY</c> pointer.</summary>
        public readonly nint ToUnmanaged() => _managed?.Pointer ?? 0;

        /// <summary>Keeps the array alive until the call returns, so that it is not finalized during the call.</summary>
        public readonly void Free() => GC.KeepAlive(_managed);
    }

    /// <summary>Receives an array returned by a COM method; the caller owns it.</summary>
    public static class ManagedToUnmanagedOut
    {
        /// <summary>Wraps the returned <c>SAFEARRAY</c>.</summary>
        public static ComSafeArray<T>? ConvertToManaged(nint unmanaged) => ComSafeArray<T>.FromPointer(unmanaged, ownsArray: true);
    }

    /// <summary>
    /// Passes an array by reference to a COM method, which can change it in place or replace it. A replaced array was
    /// destroyed by the callee, so its wrapper is detached.
    /// </summary>
    public struct ManagedToUnmanagedRef
    {
        private ComSafeArray<T>? _original;
        private nint _originalPointer;
        private nint _unmanaged;

        /// <summary>Takes the array.</summary>
        public void FromManaged(ComSafeArray<T>? managed)
        {
            _original = managed;
            _originalPointer = managed?.Pointer ?? 0;
        }

        /// <summary>Gets the <c>SAFEARRAY</c> pointer.</summary>
        public readonly nint ToUnmanaged() => _originalPointer;

        /// <summary>Takes the <c>SAFEARRAY</c> pointer returned by the callee.</summary>
        public void FromUnmanaged(nint unmanaged) => _unmanaged = unmanaged;

        /// <summary>Gets the original wrapper if the array was not replaced, otherwise a wrapper that owns the new array.</summary>
        public readonly ComSafeArray<T>? ToManaged()
        {
            if (_unmanaged == _originalPointer)
            {
                return _original;
            }

            _original?.Forget();
            return ComSafeArray<T>.FromPointer(_unmanaged, ownsArray: true);
        }

        /// <summary>Keeps the array alive until the call returns.</summary>
        public readonly void Free() => GC.KeepAlive(_original);
    }

    /// <summary>Receives an array passed to a COM object implemented in .NET: a view that is valid during the call.</summary>
    public struct UnmanagedToManagedIn
    {
        private ComSafeArray<T>? _view;

        /// <summary>Wraps the <c>SAFEARRAY</c> pointer.</summary>
        public void FromUnmanaged(nint unmanaged) => _view = ComSafeArray<T>.FromPointer(unmanaged, ownsArray: false);

        /// <summary>Gets the view.</summary>
        public readonly ComSafeArray<T>? ToManaged() => _view;

        /// <summary>Invalidates the view after the call.</summary>
        public readonly void Free() => _view?.Forget();
    }

    /// <summary>
    /// Returns an array from a COM object implemented in .NET; the caller owns it. An owned array is detached, a view is copied.
    /// </summary>
    public static class UnmanagedToManagedOut
    {
        /// <summary>Gets the <c>SAFEARRAY</c> pointer whose ownership is transferred to the caller.</summary>
        public static nint ConvertToUnmanaged(ComSafeArray<T>? managed) =>
            managed is null ? 0 : managed.OwnsArray ? managed.Detach() : SafeArrayConverter.Copy(managed.Pointer);
    }

    /// <summary>
    /// Receives an array by reference in a COM object implemented in .NET, which can change it in place or replace it.
    /// A replaced array is destroyed, as the callee of an <c>[in, out]</c> parameter must do.
    /// </summary>
    public struct UnmanagedToManagedRef
    {
        private nint _original;
        private ComSafeArray<T>? _view;
        private ComSafeArray<T>? _managed;

        /// <summary>Wraps the <c>SAFEARRAY</c> pointer passed by the caller.</summary>
        public void FromUnmanaged(nint unmanaged)
        {
            _original = unmanaged;
            _view = ComSafeArray<T>.FromPointer(unmanaged, ownsArray: false);
        }

        /// <summary>Gets a view of the array passed by the caller.</summary>
        public readonly ComSafeArray<T>? ToManaged() => _view;

        /// <summary>Takes the array returned by the method.</summary>
        public void FromManaged(ComSafeArray<T>? managed) => _managed = managed;

        /// <summary>Gets the <c>SAFEARRAY</c> pointer returned to the caller.</summary>
        public readonly nint ToUnmanaged()
        {
            // The array was changed in place, possibly through another view.
            if (_managed is not null && _managed.PointerOrZero == _original && _original != 0)
            {
                return _original;
            }

            nint result = _managed is null ? 0 : _managed.OwnsArray ? _managed.Detach() : SafeArrayConverter.Copy(_managed.Pointer);
            if (_original != 0)
            {
                _view?.Forget();
                SafeArrayConverter.Destroy(_original);
            }

            return result;
        }

        /// <summary>Invalidates the view after the call.</summary>
        public readonly void Free() => _view?.Forget();
    }
}
