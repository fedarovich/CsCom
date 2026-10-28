using System;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace CsCom.Runtime;

/// <summary>
/// A <c>BSTR</c>, whose characters are accessed in place rather than copied to a <see cref="string"/>. It also keeps the exact
/// length in bytes of <c>BSTR</c>s that hold binary data.
/// </summary>
/// <remarks>
/// An instance that owns its <c>BSTR</c> frees it when it is disposed or finalized; a view of a <c>BSTR</c> owned by someone
/// else does not. When it is passed to COM methods, the ownership follows the COM rules: <c>[in]</c> strings stay owned by the
/// caller, returned strings are owned by the receiver, and a string replaced by the callee of an <c>[in, out]</c> parameter is
/// detached. The strings received by COM objects implemented in .NET are views that are valid during the call; an owned string
/// returned by them is detached and its ownership is transferred to the caller.
/// </remarks>
[NativeMarshalling(typeof(ComBstrMarshaller))]
public sealed unsafe class ComBstr : IDisposable
{
    private nint _pointer;

    private ComBstr(nint pointer, bool ownsString)
    {
        _pointer = pointer;
        OwnsString = ownsString;
        if (!ownsString)
        {
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Frees the <c>BSTR</c> if it is owned and was not disposed.</summary>
    ~ComBstr()
    {
        Release();
    }

    /// <summary>Gets the <c>BSTR</c> pointer.</summary>
    /// <exception cref="ObjectDisposedException">The instance was disposed or detached.</exception>
    public nint Pointer
    {
        get
        {
            nint pointer = Volatile.Read(ref _pointer);
            ObjectDisposedException.ThrowIf(pointer == 0, this);
            return pointer;
        }
    }

    /// <summary>Gets a value indicating whether the instance frees the <c>BSTR</c> when it is disposed.</summary>
    public bool OwnsString { get; private set; }

    /// <summary>Gets the number of characters.</summary>
    public int Length => (int)OleNativeMethods.SysStringLen(Pointer);

    /// <summary>Gets the number of bytes, which can be odd for <c>BSTR</c>s that hold binary data.</summary>
    public int ByteLength => (int)OleNativeMethods.SysStringByteLen(Pointer);

    /// <summary>Creates a <c>BSTR</c> with the characters of a string.</summary>
    public static ComBstr Create(ReadOnlySpan<char> value)
    {
        fixed (char* characters = value)
        {
            return new(Allocate(OleNativeMethods.SysAllocStringLen(characters, (uint)value.Length)), ownsString: true);
        }
    }

    /// <summary>Creates a <c>BSTR</c> that holds binary data.</summary>
    public static ComBstr CreateFromBytes(ReadOnlySpan<byte> value)
    {
        // An empty span can give a null pointer, for which the content is uninitialized, but there is no content then.
        fixed (byte* bytes = value)
        {
            return new(Allocate(OleNativeMethods.SysAllocStringByteLen(bytes, (uint)value.Length)), ownsString: true);
        }
    }

    /// <summary>
    /// Wraps a <c>BSTR</c> pointer, e.g. a <c>BSTR</c> field of a structure.
    /// </summary>
    /// <param name="pointer">The <c>BSTR</c> pointer.</param>
    /// <param name="ownsString">Whether the instance frees the <c>BSTR</c> when it is disposed.</param>
    /// <returns>The wrapper, or <see langword="null"/> if <paramref name="pointer"/> is 0, which COM treats as an empty string.</returns>
    public static ComBstr? FromPointer(nint pointer, bool ownsString) => pointer == 0 ? null : new(pointer, ownsString);

    /// <summary>Gets the characters, which are valid until the instance is disposed.</summary>
    public ReadOnlySpan<char> AsSpan()
    {
        nint pointer = Pointer;
        return new((void*)pointer, (int)OleNativeMethods.SysStringLen(pointer));
    }

    /// <summary>Gets the bytes, which are valid until the instance is disposed.</summary>
    public ReadOnlySpan<byte> AsBytes()
    {
        nint pointer = Pointer;
        return new((void*)pointer, (int)OleNativeMethods.SysStringByteLen(pointer));
    }

    /// <summary>Copies the characters to a string.</summary>
    /// <exception cref="ObjectDisposedException">The instance was disposed or detached.</exception>
    public override string ToString() => new(AsSpan());

    /// <summary>
    /// Gives up the <c>BSTR</c> without freeing it, e.g. to return it to a COM caller. The instance cannot be used afterwards.
    /// </summary>
    /// <returns>The <c>BSTR</c> pointer, which is owned by the caller if the instance owned it.</returns>
    public nint Detach()
    {
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        ObjectDisposedException.ThrowIf(pointer == 0, this);
        OwnsString = false;
        GC.SuppressFinalize(this);
        return pointer;
    }

    /// <summary>Frees the <c>BSTR</c> if it is owned. The instance cannot be used afterwards.</summary>
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>Gets the <c>BSTR</c> pointer, or 0 if the instance was disposed or detached.</summary>
    internal nint PointerOrZero => Volatile.Read(ref _pointer);

    /// <summary>Copies a <c>BSTR</c>, keeping its exact length in bytes.</summary>
    internal static nint Copy(nint bstr) =>
        bstr == 0 ? 0 : Allocate(OleNativeMethods.SysAllocStringByteLen((byte*)bstr, OleNativeMethods.SysStringByteLen(bstr)));

    // Gives up the BSTR without freeing it, because it was freed by someone else or the instance is a view that is no longer valid.
    internal void Forget()
    {
        Volatile.Write(ref _pointer, 0);
        OwnsString = false;
        GC.SuppressFinalize(this);
    }

    private static nint Allocate(nint pointer) => pointer != 0 ? pointer : throw new OutOfMemoryException();

    private void Release()
    {
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0 && OwnsString)
        {
            OleNativeMethods.SysFreeString(pointer);
        }
    }
}
