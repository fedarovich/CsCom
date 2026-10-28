using System.Runtime.InteropServices;
using CsCom.Runtime;

namespace CsCom.Tests;

[TestClass]
public sealed class RuntimeMarshallerTests
{
    private static readonly Guid DictionaryClsid = new("EE09B103-97E0-11CF-978F-00A02463E06F");

    public enum TestColor
    {
        Red = 1,
        Green = 2,
        Blue = -3,
    }

    [TestMethod]
    public void DateMarshallerUsesOleAutomationDates()
    {
        Assert.AreEqual(0d, DateMarshaller.ConvertToUnmanaged(new DateTime(1899, 12, 30)));
        Assert.AreEqual(new DateTime(1900, 1, 1, 12, 0, 0), DateMarshaller.ConvertToManaged(2.5));

        var date = new DateTime(2024, 2, 29, 13, 45, 30);
        Assert.AreEqual(date, DateMarshaller.ConvertToManaged(DateMarshaller.ConvertToUnmanaged(date)));
    }

    [TestMethod]
    public void CurrencyMarshallerScalesByTenThousand()
    {
        Assert.AreEqual(123456L, CurrencyMarshaller.ConvertToUnmanaged(12.3456m));
        Assert.AreEqual(-0.0001m, CurrencyMarshaller.ConvertToManaged(-1));
    }

    [TestMethod]
    public void RoundTripsStrings() => AssertRoundTrip<string?>(["a", null, "", "ü"]);

    [TestMethod]
    public void RoundTripsIntegers() => AssertRoundTrip([1, -2, int.MaxValue]);

    [TestMethod]
    public void RoundTripsBooleans() => AssertRoundTrip([true, false, true]);

    [TestMethod]
    public void RoundTripsDoubles() => AssertRoundTrip([1.5, double.NegativeInfinity]);

    [TestMethod]
    public void RoundTripsDecimals() => AssertRoundTrip([1.5m, -79228162514264337593543950335m]);

    [TestMethod]
    public void RoundTripsDates() => AssertRoundTrip([new DateTime(2000, 1, 1), new DateTime(2024, 2, 29, 12, 30, 0)]);

    [TestMethod]
    public void RoundTripsEnums() => AssertRoundTrip([TestColor.Red, TestColor.Blue, (TestColor)42]);

    [TestMethod]
    public void RoundTripsVariants() => AssertRoundTrip<object?>([1, "two", true, null, 2.5, new[] { "nested" }], (expected, actual) =>
    {
        CollectionAssert.AreEqual(expected[..5], actual[..5]);
        CollectionAssert.AreEqual((string[])expected[5]!, (Array)actual[5]!);
    });

    [TestMethod]
    public void RoundTripsNullAndEmptyArrays()
    {
        Assert.AreEqual(0, SafeArrayMarshaller<string>.ConvertToUnmanaged(null));
        Assert.IsNull(SafeArrayMarshaller<string>.ConvertToManaged(0));
        SafeArrayMarshaller<string>.Free(0);

        AssertRoundTrip<int>([]);
    }

    [TestMethod]
    public void ConvertsElementsToTheRequestedType()
    {
        nint psa = SafeArrayMarshaller<int>.ConvertToUnmanaged([1, 2]);
        try
        {
            CollectionAssert.AreEqual(new[] { TestColor.Red, TestColor.Green }, SafeArrayMarshaller<TestColor>.ConvertToManaged(psa));
            CollectionAssert.AreEqual(new long[] { 1, 2 }, SafeArrayMarshaller<long>.ConvertToManaged(psa));
            CollectionAssert.AreEqual(new object[] { 1, 2 }, SafeArrayMarshaller<object>.ConvertToManaged(psa));
        }
        finally
        {
            SafeArrayMarshaller<int>.Free(psa);
        }
    }

    [TestMethod]
    public void PassesComObjectsAsUnknownPointers()
    {
        object dictionary = ComActivator.CreateInstance<object>(DictionaryClsid);

        nint psa = UnknownSafeArrayMarshaller.ConvertToUnmanaged([dictionary, null]);
        try
        {
            var result = UnknownSafeArrayMarshaller.ConvertToManaged(psa)!;

            Assert.HasCount(2, result);
            Assert.IsNull(result[1]);
            Assert.AreEqual(GetIdentity(dictionary), GetIdentity(result[0]!));
        }
        finally
        {
            UnknownSafeArrayMarshaller.Free(psa);
        }
    }

    [TestMethod]
    public void PassesDispatchInterfacesAsDispatchPointers()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);

        nint psa = SafeArrayMarshaller<IDispatch>.ConvertToUnmanaged([dictionary]);
        try
        {
            var result = SafeArrayMarshaller<IDispatch>.ConvertToManaged(psa)!;

            Assert.AreEqual(GetIdentity(dictionary), GetIdentity(result[0]!));
            Assert.AreEqual(0, result[0]!.GetTypeInfoCount(out uint count));
            Assert.AreEqual(1u, count);
        }
        finally
        {
            SafeArrayMarshaller<IDispatch>.Free(psa);
        }
    }

    [TestMethod]
    public void PassesTypedArraysInVariants()
    {
        var variant = VariantMarshaller.ConvertToUnmanaged(new[] { TestColor.Green, TestColor.Blue });
        try
        {
            Assert.AreEqual(VarEnum.VT_ARRAY | VarEnum.VT_I4, variant.VarType);
            CollectionAssert.AreEqual(new[] { 2, -3 }, (int[])VariantMarshaller.ConvertToManaged(variant)!);
        }
        finally
        {
            VariantMarshaller.Free(variant);
        }
    }

    [TestMethod]
    public void RejectsMultidimensionalArrays()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => VariantMarshaller.ConvertToUnmanaged(new int[1, 1]));
    }

    private static void AssertRoundTrip<T>(T[] values, Action<T[], T[]>? assert = null)
    {
        nint psa = SafeArrayMarshaller<T>.ConvertToUnmanaged(values);
        try
        {
            Assert.AreNotEqual(0, psa);
            var result = SafeArrayMarshaller<T>.ConvertToManaged(psa)!;
            if (assert is null)
            {
                CollectionAssert.AreEqual(values, result);
            }
            else
            {
                assert(values, result);
            }
        }
        finally
        {
            SafeArrayMarshaller<T>.Free(psa);
        }
    }

    private static nint GetIdentity(object comObject)
    {
        Assert.IsTrue(ComWrappers.TryGetComInstance(comObject, out nint unknown));
        Marshal.Release(unknown);
        return unknown;
    }
}
