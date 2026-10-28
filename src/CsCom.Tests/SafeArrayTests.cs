using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// Tests of the conversions of SAFEARRAYs of any rank and of <see cref="ComSafeArray{T}"/>.
/// </summary>
[TestClass]
public sealed class SafeArrayTests
{
    [TestMethod]
    public void MultidimensionalArraysRoundTripThroughVariants()
    {
        AssertRoundTrip(new[,] { { 1, 2, 3 }, { 4, 5, 6 } });
        AssertRoundTrip(new string?[,] { { "a", null }, { "c", "d" } });
        AssertRoundTrip(new object?[,] { { 1, "two" }, { 3.5, null }, { true, DateTime.Today } });
        AssertRoundTrip(new[,] { { new DateTime(2024, 2, 29) }, { new DateTime(2025, 1, 1) } });

        var cube = new int[2, 3, 4];
        for (int i = 0; i < 2; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                for (int k = 0; k < 4; k++)
                {
                    cube[i, j, k] = (i * 100) + (j * 10) + k;
                }
            }
        }

        AssertRoundTrip(cube);

        static void AssertRoundTrip(Array array)
        {
            var variant = VariantMarshaller.ConvertToUnmanaged(array);
            try
            {
                var result = (Array)VariantMarshaller.ConvertToManaged(variant)!;
                Assert.AreEqual(array.GetType(), result.GetType());
                CollectionAssert.AreEqual(array, result);
                for (int i = 0; i < array.Rank; i++)
                {
                    Assert.AreEqual(array.GetLength(i), result.GetLength(i));
                }
            }
            finally
            {
                variant.Dispose();
            }
        }
    }

    [TestMethod]
    public void ElementsKeepTheirIndices()
    {
        var matrix = new[,] { { 1, 2, 3 }, { 4, 5, 6 } };
        var variant = VariantMarshaller.ConvertToUnmanaged(matrix);
        try
        {
            // ComSafeArray<T> reads the elements through SafeArrayPtrOfIndex.
            var view = ComSafeArray<int>.FromPointer(variant.GetRawDataRef<nint>(), ownsArray: false)!;
            Assert.AreEqual(2, view.Rank);
            Assert.AreEqual(2, view.GetLength(0));
            Assert.AreEqual(3, view.GetLength(1));
            for (int i = 0; i < 2; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    Assert.AreEqual(matrix[i, j], view[i, j], $"[{i}, {j}]");
                }
            }
        }
        finally
        {
            variant.Dispose();
        }
    }

    [TestMethod]
    public void NativeBoundsAreKeptByComSafeArrayAndRemovedFromNetArrays()
    {
        using var array = ComSafeArray<int>.Create(VarEnum.VT_I4, [2, 3], [1, 1]);
        for (int i = 1; i <= 2; i++)
        {
            for (int j = 1; j <= 3; j++)
            {
                array[i, j] = (i * 10) + j;
            }
        }

        Assert.AreEqual(1, array.GetLowerBound(0));
        Assert.AreEqual(3, array.GetUpperBound(1));
        Assert.AreEqual(6, array.Length);
        Assert.AreEqual(VarEnum.VT_I4, array.ElementType);
        Assert.ThrowsExactly<IndexOutOfRangeException>(() => array[0, 0]);
        Assert.ThrowsExactly<ArgumentException>(() => array[1]);

        var converted = (int[,])SafeArrayMarshaller.ConvertToManaged(array.Pointer)!;
        CollectionAssert.AreEqual(new[,] { { 11, 12, 13 }, { 21, 22, 23 } }, converted);
        CollectionAssert.AreEqual(converted, array.ToArray2D());
        Assert.ThrowsExactly<InvalidOperationException>(() => array.ToArray());
    }

    [TestMethod]
    public void ArraysWithOtherLowerBoundsKeepThemInSafeArrays()
    {
        var array = Array.CreateInstance(typeof(int), [2, 2], [1, 5]);
        array.SetValue(42, 2, 6);

        nint pointer = SafeArrayMarshaller.ConvertToUnmanaged(array);
        try
        {
            var view = ComSafeArray<int>.FromPointer(pointer, ownsArray: false)!;
            Assert.AreEqual(5, view.GetLowerBound(1));
            Assert.AreEqual(42, view[2, 6]);
        }
        finally
        {
            SafeArrayMarshaller.Free(pointer);
        }
    }

    [TestMethod]
    public void ComSafeArrayConvertsTheElements()
    {
        using var names = ComSafeArray<string?>.Create(new string?[,] { { "a", null }, { "c", "d" } });
        Assert.AreEqual(VarEnum.VT_BSTR, names.ElementType);
        Assert.AreEqual("c", names[1, 0]);
        Assert.IsNull(names[0, 1]);

        names[0, 1] = "b";
        names.SetValue("x", 1, 1);
        CollectionAssert.AreEqual(new[,] { { "a", "b" }, { "c", "x" } }, names.ToArray2D());

        using var dates = ComSafeArray<ComDate>.Create([new DateTime(2024, 2, 29)]);
        Assert.AreEqual(VarEnum.VT_DATE, dates.ElementType);
        Assert.AreEqual(new DateTime(2024, 2, 29), (DateTime)dates[0]);

        using var cube = ComSafeArray<long>.Create(new long[,,] { { { 1, 2 }, { 3, 4 } }, { { 5, 6 }, { 7, 8 } } });
        Assert.AreEqual(8, cube.Length);
        Assert.AreEqual(VarEnum.VT_I8, cube.ElementType);
        Assert.AreEqual(6L, cube[1, 0, 1]);
        Assert.AreEqual(7L, cube[1, 1, 0]);
    }

    [TestMethod]
    public void ComSafeArrayCanBePassedInVariants()
    {
        using var array = ComSafeArray<double>.Create(new[,] { { 1.5, 2.5 } });
        var variant = VariantMarshaller.ConvertToUnmanaged(array);
        try
        {
            Assert.AreEqual(VarEnum.VT_ARRAY | VarEnum.VT_R8, variant.VarType);
            Assert.AreNotEqual(array.Pointer, variant.GetRawDataRef<nint>(), "The VARIANT holds a copy.");
            CollectionAssert.AreEqual(new[,] { { 1.5, 2.5 } }, (double[,])VariantMarshaller.ConvertToManaged(variant)!);
        }
        finally
        {
            variant.Dispose();
        }
    }

    [TestMethod]
    public void ComSafeArrayOwnership()
    {
        var array = ComSafeArray<int>.Create([1, 2, 3]);
        Assert.IsTrue(array.OwnsArray);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, array.ToArray());

        nint pointer = array.Detach();
        Assert.IsFalse(array.OwnsArray);
        Assert.ThrowsExactly<ObjectDisposedException>(() => array.Pointer);
        Assert.ThrowsExactly<ObjectDisposedException>(() => array.Detach());

        var owner = ComSafeArray<int>.FromPointer(pointer, ownsArray: true)!;
        Assert.AreEqual(3, owner.Length);
        owner.Dispose();
        owner.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => owner[0]);
        Assert.IsNull(ComSafeArray<int>.FromPointer(0, ownsArray: true));
    }

    [TestMethod]
    public void HigherRanksAreOnlySupportedByComSafeArray()
    {
        using var array = ComSafeArray<int>.Create(VarEnum.VT_I4, [1, 2, 1, 2]);
        array.SetValue(7, 0, 1, 0, 1);

        Assert.AreEqual(7, array.GetValue(0, 1, 0, 1));
        Assert.ThrowsExactly<NotSupportedException>(() => SafeArrayMarshaller.ConvertToManaged(array.Pointer));
    }

    [TestMethod]
    public void OneDimensionalMarshallerRejectsMultidimensionalArrays()
    {
        using var matrix = ComSafeArray<int>.Create(new int[2, 2]);

        var exception = Assert.ThrowsExactly<NotSupportedException>(() => SafeArrayMarshaller<int>.ConvertToManaged(matrix.Pointer));
        StringAssert.Contains(exception.Message, "--safearray-marshalling");
    }
}
