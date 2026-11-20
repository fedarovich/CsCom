using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
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

        // DBNull is no object, like the values; the wrappers of the built-in COM interop of .NET are tested as objects
        // first, and then passed by ComVariantMarshaller.
        AssertVariant(DBNull.Value, VarEnum.VT_NULL, DBNull.Value);
        AssertVariant(new BStrWrapper("text"), VarEnum.VT_BSTR, "text");

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
    public void ObjectsThatAreNullPointersAreNull()
    {
        // Like Nothing of Visual Basic: a VARIANT with the type of an object and no object.
        Assert.IsNull(VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_DISPATCH, (nint)0)));
        Assert.IsNull(VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, (nint)0)));
        Assert.IsNull(OptionalVariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_DISPATCH, (nint)0)));
    }

    [TestMethod]
    public void InvocationsReturnObjectsThatAreNullPointers()
    {
        // The server leaves the objects passed by reference null: it has no object to replace them with.
        var server = new MemoryLeakTests.Server();
        object?[] args = [server, null, server, server];

        DispatchObject.InvokeByRef(server, MemoryLeakTests.Server.ReplaceId, DispatchFlags.Method, args, [VarEnum.VT_DISPATCH, VarEnum.VT_DISPATCH, VarEnum.VT_UNKNOWN, VarEnum.VT_VARIANT]);

        CollectionAssert.AreEqual(new object?[] { null, null, null, null }, args);
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
    public void RejectsArraysWithMoreThanThreeDimensions()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => VariantMarshaller.ConvertToUnmanaged(new int[1, 1, 1, 1]));
    }

    [TestMethod]
    public void RejectsManagedObjectsThatAreNotExposedToCom()
    {
        Assert.ThrowsExactly<ArgumentException>(() => VariantMarshaller.ConvertToUnmanaged(new System.Text.StringBuilder()));
    }

    [TestMethod]
    public void ConvertsNullObjectWrappersToNullPointers()
    {
        ComVariant unknown = VariantMarshaller.ConvertToUnmanaged(new UnknownWrapper(null));
        Assert.AreEqual(VarEnum.VT_UNKNOWN, unknown.VarType);
        Assert.AreEqual(0, unknown.GetRawDataRef<nint>());

        // Like ComVariantMarshaller of .NET, which passes no DispatchWrapper.
        Assert.ThrowsExactly<ArgumentException>(() => VariantMarshaller.ConvertToUnmanaged(new DispatchWrapper(null)));

        // A late-bound wrapper without a COM object, whose Dispatch breaks its contract, stands for no object.
        ComVariant dispatch = VariantMarshaller.ConvertToUnmanaged(new PlainWrapper(null!));
        Assert.AreEqual(VarEnum.VT_DISPATCH, dispatch.VarType);
        Assert.AreEqual(0, dispatch.GetRawDataRef<nint>());
        Assert.IsNull(VariantMarshaller.ConvertToManaged(dispatch));
    }

    [TestMethod]
    public void ConvertsWrappedComObjectsToUnknownPointers()
    {
        // The dictionary implements IDispatch, but the wrapper asks for VT_UNKNOWN.
        var dictionary = ComActivator.CreateInstance<IDispatch>("Scripting.Dictionary");
        ComVariant variant = VariantMarshaller.ConvertToUnmanaged(new UnknownWrapper(dictionary));
        try
        {
            Assert.AreEqual(VarEnum.VT_UNKNOWN, variant.VarType);
            Assert.AreEqual(GetIdentity(dictionary), variant.GetRawDataRef<nint>());
            Assert.AreSame(dictionary, VariantMarshaller.ConvertToManaged(variant));
        }
        finally
        {
            VariantMarshaller.Free(variant);
        }
    }

    [TestMethod]
    public unsafe void PassesTheObjectOfAnUnknownWrapperWhereverAnObjectGoesToCom()
    {
        // The wrapper names an object, in an IUnknown parameter and a SAFEARRAY as in a VARIANT: the interface of the wrapper
        // itself would only have IUnknown. The object is converted as if it were passed itself, also a late-bound wrapper.
        var dictionary = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        nint identity = GetIdentity(dictionary);
        object[] values = [new UnknownWrapper(dictionary), new UnknownWrapper(new UnknownWrapper(dictionary)), new UnknownWrapper(new MemoryLeakTests.Wrapper(dictionary))];
        foreach (object value in values)
        {
            nint shared = (nint)ComObjectMarshaller<object>.ConvertToUnmanaged(value);
            nint unique = (nint)UniqueComObjectMarshaller<object>.ConvertToUnmanaged(value);
            try
            {
                Assert.AreEqual(identity, shared);
                Assert.AreEqual(identity, unique);
            }
            finally
            {
                Marshal.Release(shared);
                Marshal.Release(unique);
            }
        }

        Assert.IsTrue(ComObjectMarshaller<object>.ConvertToUnmanaged(new UnknownWrapper(null)) is null);

        nint psa = UnknownSafeArrayMarshaller.ConvertToUnmanaged([new UnknownWrapper(dictionary), new UnknownWrapper(null)]);
        try
        {
            var result = UnknownSafeArrayMarshaller.ConvertToManaged(psa)!;
            Assert.AreSame(dictionary, result[0]);
            Assert.IsNull(result[1]);
        }
        finally
        {
            UnknownSafeArrayMarshaller.Free(psa);
        }

        using (var unknowns = ComSafeArray<object?>.Create(VarEnum.VT_UNKNOWN, [1]))
        {
            unknowns[0] = new UnknownWrapper(dictionary);
            Assert.AreSame(dictionary, unknowns[0]);
        }

        // The whole rule applies to the object where an IDispatch is required: an object of the application that is exposed
        // to COM without IDispatch stands for the IDispatch that it provides.
        using var dispatches = ComSafeArray<object?>.Create(VarEnum.VT_DISPATCH, [3]);
        dispatches[0] = new UnknownWrapper(dictionary);
        dispatches[1] = new UnknownWrapper(null);
        dispatches[2] = new UnknownWrapper(new ProviderObject(dictionary));
        Assert.AreSame(dictionary, dispatches[0]);
        Assert.IsNull(dispatches[1]);
        Assert.AreSame(dictionary, dispatches[2]);

        // The exception of an object that does not implement IDispatch names that object rather than the wrapper.
        var exception = Assert.ThrowsExactly<InvalidCastException>(() => dispatches[0] = new UnknownWrapper(new ProviderObject(null!)));
        StringAssert.Contains(exception.Message, nameof(ProviderObject));
        Assert.IsFalse(exception.Message.Contains(nameof(UnknownWrapper), StringComparison.Ordinal));
    }

    [TestMethod]
    public unsafe void RejectsDispatchWrappersWhereverAnObjectGoesToCom()
    {
        // Like in a VARIANT, where ComVariantMarshaller rejects it: the interface of the wrapper itself would only have
        // IUnknown.
        Assert.ThrowsExactly<ArgumentException>(() => ComObjectMarshaller<object>.ConvertToUnmanaged(new DispatchWrapper(null)));
        Assert.ThrowsExactly<ArgumentException>(() => UniqueComObjectMarshaller<object>.ConvertToUnmanaged(new DispatchWrapper(null)));
        Assert.ThrowsExactly<ArgumentException>(() => UnknownSafeArrayMarshaller.ConvertToUnmanaged([new DispatchWrapper(null)]));
        using var dispatches = ComSafeArray<object?>.Create(VarEnum.VT_DISPATCH, [1]);
        Assert.ThrowsExactly<ArgumentException>(() => dispatches[0] = new DispatchWrapper(null));
    }

    [TestMethod]
    public void TheDefaultContextCallsTheActivationWithoutUnique()
    {
        // default converts to the unique parameter of the other overloads too, but C# prefers the overload whose parameters
        // all have arguments: the empty context, with which no class is activated, rather than a shared object.
        const int ClassNotRegistered = unchecked((int)0x80040154);
        Assert.AreEqual(ClassNotRegistered, Assert.ThrowsExactly<COMException>(() => ComActivator.CreateInstance<object>(DictionaryClsid, default)).HResult);
        Assert.AreEqual(ClassNotRegistered, Assert.ThrowsExactly<COMException>(() => ComActivator.CreateInstance<object>("Scripting.Dictionary", default)).HResult);
        Assert.AreEqual(ClassNotRegistered, Assert.ThrowsExactly<COMException>(() => ComActivator.CreateDispatchInstance<MemoryLeakTests.Wrapper>(DictionaryClsid, default)).HResult);
    }

    [TestMethod]
    public unsafe void ConvertsNullObjectsPassedByReferenceToNull()
    {
        nint nothing = 0;
        foreach (VarEnum type in new[] { VarEnum.VT_UNKNOWN, VarEnum.VT_DISPATCH })
        {
            // The referenced pointer is null, or there is no reference at all.
            Assert.IsNull(VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_BYREF | type, (nint)(&nothing))));
            Assert.IsNull(VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_BYREF | type, (nint)0)));
        }

        Assert.IsNull(VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_VARIANT, (nint)0)));
        Assert.IsNull(VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_ARRAY | VarEnum.VT_VARIANT, (nint)0)));
        Assert.IsNull(VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_ARRAY | VarEnum.VT_VARIANT, (nint)(&nothing))));
    }

    [TestMethod]
    public unsafe void ConvertsObjectsPassedByReference()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>("Scripting.Dictionary");
        nint identity = GetIdentity(dictionary);
        Assert.AreSame(dictionary, VariantMarshaller.ConvertToManaged(ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_UNKNOWN, (nint)(&identity))));
    }

    [TestMethod]
    public unsafe void MarshalsLateBoundWrappersAsTheirComObjects()
    {
        // The wrapper is a managed object, which is not exposed to COM itself: an IUnknown parameter gets its COM object.
        var dictionary = ComActivator.CreateInstance<IDispatch>("Scripting.Dictionary");
        nint unknown = (nint)ComObjectMarshaller<object>.ConvertToUnmanaged(new MemoryLeakTests.Wrapper(dictionary));
        try
        {
            Assert.AreEqual(GetIdentity(dictionary), unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [TestMethod]
    public unsafe void RejectsInterfacesThatAreNoComInterfaces()
    {
        // Without the identifier of the interface, the IUnknown pointer would be passed as its pointer. The marshaller of
        // such an interface fails also for null, so that it fails at its first use rather than at its first object.
        var dictionary = ComActivator.CreateInstance<IDispatch>("Scripting.Dictionary");
        Assert.ThrowsExactly<NotSupportedException>(() => ComObjectMarshaller<IDisposable>.ConvertToUnmanaged(new MemoryStream()));
        Assert.ThrowsExactly<NotSupportedException>(() => UniqueComObjectMarshaller<IDispatchProvider>.ConvertToUnmanaged(new MemoryLeakTests.Wrapper(dictionary)));
        Assert.ThrowsExactly<NotSupportedException>(() => ComObjectMarshaller<IDisposable>.ConvertToUnmanaged(null));
        Assert.ThrowsExactly<NotSupportedException>(() => ComObjectMarshaller<IDisposable>.ConvertToManaged(null));
        Assert.ThrowsExactly<NotSupportedException>(() => UniqueComObjectMarshaller<IDisposable>.ConvertToManaged(null));

        // Neither is a pointer converted to such an interface, and the activation fails before it creates the object.
        nint unknown = (nint)ComObjectMarshaller<object>.ConvertToUnmanaged(dictionary);
        try
        {
            Assert.ThrowsExactly<NotSupportedException>(() => ComObjectMarshaller<IDisposable>.ConvertToManaged((void*)unknown));
            Assert.ThrowsExactly<NotSupportedException>(() => UniqueComObjectMarshaller<IDisposable>.ConvertToManaged((void*)unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.CreateInstance<IDisposable>("Scripting.Dictionary"));

        // Also before the ProgID is looked up, which is not registered here.
        const string Unregistered = "CsCom.Not.Registered";
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.CreateInstance<IDisposable>(Unregistered));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.GetActiveInstance<IDisposable>(Unregistered));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.TryGetActiveInstance<IDisposable>(Unregistered, out _));
        Assert.IsFalse(ComActivator.TryGetActiveInstance<IDispatch>(Unregistered, out _));

        // And before the name of a moniker is checked.
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.BindToMoniker<IDisposable>(""));
        Assert.ThrowsExactly<ArgumentException>(() => ComActivator.BindToMoniker<IDispatch>(""));

        // And before a class that is not registered, or has no running instance, is looked up.
        Guid unregistered = Guid.NewGuid();
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.CreateInstance<IDisposable>(unregistered));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.GetActiveInstance<IDisposable>(unregistered));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.TryGetActiveInstance<IDisposable>(unregistered, out _));
        Assert.IsFalse(ComActivator.TryGetActiveInstance<IDispatch>(unregistered, out _));
    }

    [TestMethod]
    public void TheActivationRejectsClassesThatNoComObjectIs()
    {
        // A COM object is a ComObject, which converts to no other class than ComObject and object: the activation fails
        // before it looks up the ProgID, the moniker or the class, which are not registered here, rather than after starting
        // the server.
        const string Unregistered = "CsCom.Not.Registered";
        Guid unregistered = Guid.NewGuid();
        var exception = Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.CreateInstance<MemoryLeakTests.Wrapper>(Unregistered));
        StringAssert.Contains(exception.Message, nameof(ComActivator.CreateDispatchInstance));

        // A dynamic object implements no IDispatchWrapper<T>, which CreateDispatchInstance requires: the message names
        // ComDynamic too.
        exception = Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.CreateInstance<DynamicDispatchObject>(Unregistered));
        StringAssert.Contains(exception.Message, "ComDynamic.ToDynamic");
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.CreateInstance<MemoryLeakTests.Wrapper>(unregistered, unique: true));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.CreateInstance<string>(unregistered));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.GetActiveInstance<string>(Unregistered));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.GetActiveInstance<MemoryLeakTests.Wrapper>(unregistered));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.TryGetActiveInstance<MemoryLeakTests.Wrapper>(Unregistered, out _));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.TryGetActiveInstance<string>(unregistered, out _));
        Assert.ThrowsExactly<NotSupportedException>(() => ComActivator.BindToMoniker<MemoryLeakTests.Wrapper>(""));

        // The classes of COM objects are.
        Assert.IsInstanceOfType<ComObject>(ComActivator.CreateInstance<ComObject>("Scripting.Dictionary"));
        Assert.IsInstanceOfType<ComObject>(ComActivator.CreateInstance<object>(DictionaryClsid));
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
