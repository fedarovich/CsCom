using System.Runtime.InteropServices;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// Tests of <see cref="ComBstr"/>.
/// </summary>
[TestClass]
public sealed class BstrTests
{
    [TestMethod]
    public void CreatesStrings()
    {
        using var bstr = ComBstr.Create("héllo\0world");

        Assert.IsTrue(bstr.OwnsString);
        Assert.AreEqual(11, bstr.Length);
        Assert.AreEqual(22, bstr.ByteLength);
        Assert.AreEqual("héllo\0world", bstr.ToString());
        Assert.IsTrue(bstr.AsSpan().SequenceEqual("héllo\0world"));
        Assert.AreEqual("héllo\0world", Marshal.PtrToStringBSTR(bstr.Pointer));

        using var empty = ComBstr.Create(string.Empty);
        Assert.AreEqual(0, empty.Length);
        Assert.AreEqual(string.Empty, empty.ToString());
    }

    [TestMethod]
    public void KeepsBinaryContent()
    {
        using var bstr = ComBstr.CreateFromBytes([1, 2, 3]);

        Assert.AreEqual(3, bstr.ByteLength);
        Assert.AreEqual(1, bstr.Length);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, bstr.AsBytes().ToArray());

        using var empty = ComBstr.CreateFromBytes([]);
        Assert.AreEqual(0, empty.ByteLength);

        // The copy passed in a VARIANT keeps the odd length.
        var variant = VariantMarshaller.ConvertToUnmanaged(bstr);
        try
        {
            using var copy = ComBstr.FromPointer(variant.GetRawDataRef<nint>(), ownsString: false)!;
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, copy.AsBytes().ToArray());
        }
        finally
        {
            variant.Dispose();
        }
    }

    [TestMethod]
    public void ViewsDoNotFreeTheString()
    {
        using var owner = ComBstr.Create("abc");
        var view = ComBstr.FromPointer(owner.Pointer, ownsString: false)!;

        Assert.IsFalse(view.OwnsString);
        Assert.AreEqual("abc", view.ToString());
        view.Dispose();
        Assert.AreEqual("abc", owner.ToString());
        Assert.IsNull(ComBstr.FromPointer(0, ownsString: true));
    }

    [TestMethod]
    public void Ownership()
    {
        var detached = ComBstr.Create("abc");
        nint pointer = detached.Detach();
        Assert.IsFalse(detached.OwnsString);
        Assert.ThrowsExactly<ObjectDisposedException>(() => detached.Pointer);
        Assert.ThrowsExactly<ObjectDisposedException>(() => detached.Detach());
        detached.Dispose();
        Assert.AreEqual("abc", Marshal.PtrToStringBSTR(pointer));
        Marshal.FreeBSTR(pointer);

        var disposed = ComBstr.Create("abc");
        disposed.Dispose();
        disposed.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => disposed.ToString());
        Assert.ThrowsExactly<ObjectDisposedException>(() => disposed.Length);
    }

    [TestMethod]
    public void IsPassedInVariantsAsACopy()
    {
        using var bstr = ComBstr.Create("text");
        var variant = VariantMarshaller.ConvertToUnmanaged(bstr);
        try
        {
            Assert.AreEqual(VarEnum.VT_BSTR, variant.VarType);
            Assert.AreNotEqual(bstr.Pointer, variant.GetRawDataRef<nint>());
            Assert.AreEqual("text", VariantMarshaller.ConvertToManaged(variant));
        }
        finally
        {
            variant.Dispose();
        }

        Assert.AreEqual("text", bstr.ToString());
    }
}
