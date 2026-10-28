using System;
using System.Runtime.InteropServices;

namespace CsCom.Runtime;

/// <summary>
/// A <c>BSTR</c> stored in a field of a structure. It has the layout of the <c>BSTR</c> pointer, so it can be used in
/// blittable structures, and converts implicitly to <see cref="string"/>.
/// </summary>
/// <remarks>
/// Like in C, the string is owned by the owner of the structure: a <c>BSTR</c> created by <see cref="Allocate(string?)"/>
/// must be released by <see cref="Free"/> unless its ownership is transferred, e.g. to a COM method that frees it. The
/// default value is the <see langword="null"/> <c>BSTR</c>, which COM treats as an empty string.
/// </remarks>
/// <param name="Pointer">The <c>BSTR</c> pointer, or 0.</param>
public readonly record struct ComBstrValue(nint Pointer)
{
    /// <summary>Gets a value indicating whether the <c>BSTR</c> pointer is 0.</summary>
    public bool IsNull => Pointer == 0;

    /// <summary>Gets the number of characters of the string.</summary>
    public int Length => Pointer == 0 ? 0 : (int)OleNativeMethods.SysStringLen(Pointer);

    /// <summary>Allocates a <c>BSTR</c> with a copy of a string.</summary>
    /// <returns>The <c>BSTR</c>, or the <see langword="null"/> <c>BSTR</c> for <see langword="null"/>.</returns>
    public static ComBstrValue Allocate(string? value) => value is null ? default : Allocate(value.AsSpan());

    /// <summary>Allocates a <c>BSTR</c> with a copy of characters.</summary>
    public static unsafe ComBstrValue Allocate(ReadOnlySpan<char> value)
    {
        fixed (char* chars = value)
        {
            nint bstr = OleNativeMethods.SysAllocStringLen(chars, (uint)value.Length);
            return bstr != 0 ? new ComBstrValue(bstr) : throw new OutOfMemoryException();
        }
    }

    /// <summary>
    /// Releases the <c>BSTR</c>. The structure must not use it afterwards: assign <see langword="default"/> to the field.
    /// </summary>
    public void Free()
    {
        if (Pointer != 0)
        {
            OleNativeMethods.SysFreeString(Pointer);
        }
    }

    /// <summary>Gets a view of the <c>BSTR</c> that does not own it, or <see langword="null"/> if the pointer is 0.</summary>
    public ComBstr? AsComBstr() => ComBstr.FromPointer(Pointer, ownsString: false);

    /// <summary>Copies the string; an empty string for the <see langword="null"/> <c>BSTR</c>.</summary>
    public override string ToString() => Pointer == 0 ? string.Empty : Marshal.PtrToStringBSTR(Pointer);

    /// <summary>Copies the string of a <c>BSTR</c>; <see langword="null"/> for the <see langword="null"/> <c>BSTR</c>.</summary>
    public static implicit operator string?(ComBstrValue value) => value.Pointer == 0 ? null : Marshal.PtrToStringBSTR(value.Pointer);
}
