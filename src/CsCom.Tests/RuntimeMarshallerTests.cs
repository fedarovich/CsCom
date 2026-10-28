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
    public void ComDateConvertsToAndFromDateTime()
    {
        ComDate date = new DateTime(1900, 1, 1, 12, 0, 0);
        DateTime converted = new ComDate(0);

        Assert.AreEqual(2.5, date.Value);
        Assert.AreEqual(new DateTime(1899, 12, 30), converted);
        Assert.AreEqual(new DateTime(1899, 12, 30), default(ComDate).ToDateTime());
        Assert.AreEqual(new ComDate(2.5), date);
        Assert.AreNotEqual(new ComDate(2.5), new ComDate(2.75));
        Assert.AreEqual(new DateTime(1900, 1, 1, 12, 0, 0).ToString(), date.ToString());
    }

    [TestMethod]
    public void ComCurrencyConvertsToAndFromDecimal()
    {
        ComCurrency currency = 12.3456m;
        decimal converted = new ComCurrency(-1);

        Assert.AreEqual(123456L, currency.Value);
        Assert.AreEqual(-0.0001m, converted);
        Assert.AreEqual(0m, default(ComCurrency).ToDecimal());
        Assert.AreEqual(new ComCurrency(123456), currency);
        Assert.AreEqual(12.3456m.ToString(), currency.ToString());
        Assert.ThrowsExactly<OverflowException>(() => (ComCurrency)decimal.MaxValue);
    }

    [TestMethod]
    public void ComBoolConvertsToAndFromBool()
    {
        ComBool comTrue = true;
        bool converted = new ComBool(1);

        Assert.AreEqual((short)-1, comTrue.Value);
        Assert.AreEqual((short)0, ((ComBool)false).Value);
        Assert.IsTrue(converted);
        Assert.IsFalse(default(ComBool));
        Assert.AreEqual(ComBool.True, new ComBool(1));
        Assert.IsTrue(new ComBool(1) == ComBool.True);
        Assert.AreEqual(ComBool.True.GetHashCode(), new ComBool(42).GetHashCode());
        Assert.AreNotEqual(ComBool.True, ComBool.False);
        Assert.AreEqual(bool.TrueString, comTrue.ToString());
    }

    [TestMethod]
    public void ComBoolSupportsBooleanOperators()
    {
        ComBool comTrue = new(1);
        ComBool comFalse = ComBool.False;
        int evaluated = 0;

        // The results are normalized to VARIANT_TRUE / VARIANT_FALSE and use the truth values, not the bits (1 ^ -1 is -2).
        Assert.AreEqual((short)-1, (!comFalse).Value);
        Assert.AreEqual((short)0, (!comTrue).Value);
        Assert.AreEqual((short)-1, (comTrue & ComBool.True).Value);
        Assert.AreEqual((short)0, (comTrue & comFalse).Value);
        Assert.AreEqual((short)-1, (comFalse | comTrue).Value);
        Assert.AreEqual((short)0, (comFalse | comFalse).Value);
        Assert.AreEqual((short)0, (comTrue ^ ComBool.True).Value);
        Assert.AreEqual((short)-1, (comTrue ^ comFalse).Value);
        Assert.AreEqual((short)-1, (comTrue & true).Value);
        Assert.AreEqual((short)-1, (false | comTrue).Value);

        // && and || return the left operand as is when it decides the result.
        Assert.AreEqual((short)0, (comFalse && Evaluate(ComBool.True)).Value);
        Assert.AreEqual((short)1, (comTrue || Evaluate(ComBool.False)).Value);
        Assert.AreEqual(0, evaluated);
        Assert.AreEqual((short)-1, (comTrue && Evaluate(new ComBool(2))).Value);
        Assert.AreEqual(1, evaluated);

        ComBool flags = comTrue;
        flags &= comFalse;
        Assert.IsFalse(flags);
        Assert.AreEqual("yes", comTrue ? "yes" : "no");

        ComBool Evaluate(ComBool value)
        {
            evaluated++;
            return value;
        }
    }

    [TestMethod]
    public void PassesWrappersAsTheirVariantTypes()
    {
        AssertVariant(new ComDate(2.5), VarEnum.VT_DATE, new DateTime(1900, 1, 1, 12, 0, 0));
        AssertVariant(new ComCurrency(123456), VarEnum.VT_CY, 12.3456m);
        AssertVariant(ComBool.True, VarEnum.VT_BOOL, true);

        static void AssertVariant(object value, VarEnum expectedType, object expectedValue)
        {
            var variant = VariantMarshaller.ConvertToUnmanaged(value);
            try
            {
                Assert.AreEqual(expectedType, variant.VarType);
                Assert.AreEqual(expectedValue, VariantMarshaller.ConvertToManaged(variant));
            }
            finally
            {
                VariantMarshaller.Free(variant);
            }
        }
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
