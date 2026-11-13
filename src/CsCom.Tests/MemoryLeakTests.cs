using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// Tests that the runtime support frees the native memory it allocates (<c>BSTR</c>s, <c>SAFEARRAY</c>s and the memory of
/// <c>CoTaskMemAlloc</c>) and releases the COM references it takes, see <see cref="LeakDetector"/>. The tests of the detector
/// itself come first; the other parts of the class cover the calls of <c>IDispatch</c>, the events and the generated interfaces.
/// </summary>
[TestClass]
public sealed unsafe partial class MemoryLeakTests
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    private const int DISP_E_TYPEMISMATCH = unchecked((int)0x80020005);
    private const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    private const int DISP_E_EXCEPTION = unchecked((int)0x80020009);
    private const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);

    private static readonly Guid DictionaryClsid = new("EE09B103-97E0-11CF-978F-00A02463E06F");

    [TestMethod]
    public void TheDetectorFindsStringsThatAreNotFreed()
    {
        var leaked = new List<nint>();
        try
        {
            string? leaks = LeakDetector.Find(() => leaked.Add(Marshal.StringToBSTR("leaked")));

            Assert.IsNotNull(leaks);
            StringAssert.Contains(leaks, "3 blocks of the COM task allocator");
            StringAssert.Contains(leaks, nameof(TheDetectorFindsStringsThatAreNotFreed), "The report has the stack of the allocation.");
        }
        finally
        {
            leaked.ForEach(Marshal.FreeBSTR);
        }

        Assert.IsNull(LeakDetector.Find(static () => Marshal.FreeBSTR(Marshal.StringToBSTR("freed"))));
    }

    [TestMethod]
    public void TheDetectorFindsArraysThatAreNotDestroyed()
    {
        var leaked = new List<nint>();
        try
        {
            string? leaks = LeakDetector.Find(() => leaked.Add(SafeArrayMarshaller<int>.ConvertToUnmanaged([1, 2, 3])));

            Assert.IsNotNull(leaks);
            StringAssert.Contains(leaks, "blocks of the COM task allocator");
        }
        finally
        {
            leaked.ForEach(SafeArrayMarshaller<int>.Free);
        }
    }

    [TestMethod]
    public void TheDetectorFindsMemoryThatAFinalizerDoesNotFree()
    {
        // An owner that is not disposed frees its string when it is finalized, which the detector waits for; a view never does.
        Assert.IsNull(LeakDetector.Find(static () => _ = ComBstr.Create("finalized")));

        var leaked = new List<nint>();
        try
        {
            string? leaks = LeakDetector.Find(() =>
            {
                leaked.Add(Marshal.StringToBSTR("leaked"));
                _ = ComBstr.FromPointer(leaked[^1], ownsString: false);
            });

            Assert.IsNotNull(leaks);
        }
        finally
        {
            leaked.ForEach(Marshal.FreeBSTR);
        }
    }

    [TestMethod]
    public void TheDetectorFindsReferencesThatAreNotReleased()
    {
        object managed = new Server();
        object native = CreateDictionary();
        var leaked = new List<nint>();
        try
        {
            string? leaks = LeakDetector.Find(
                () =>
                {
                    leaked.Add((nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(managed));
                    leaked.Add((nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(native));
                },
                managed,
                native);

            Assert.IsNotNull(leaks);
            StringAssert.Contains(leaks, "The reference count of the COM object 0 (Server) changed from 1 to 4");
            StringAssert.Contains(leaks, "The reference count of the COM object 1");
        }
        finally
        {
            leaked.ForEach(static unknown => Marshal.Release(unknown));
        }

        Assert.IsNull(LeakDetector.Find(() => Marshal.Release((nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(native)), managed, native));
    }

    [TestMethod]
    public void VariantsAreFreed()
    {
        object native = CreateDictionary();
        var managed = new Server();

        LeakDetector.AssertNoLeaks(
            () =>
            {
                using var bstr = ComBstr.Create("bstr");
                using var safeArray = ComSafeArray<string?>.Create(["a", null]);
                foreach (object? value in (object?[])[.. CreateValues(native, managed), bstr, safeArray, Missing.Value])
                {
                    var variant = VariantMarshaller.ConvertToUnmanaged(value);
                    try
                    {
                        _ = VariantMarshaller.ConvertToManaged(variant);
                    }
                    finally
                    {
                        VariantMarshaller.Free(variant);
                    }
                }

                var optional = OptionalVariantMarshaller.ConvertToUnmanaged("optional");
                _ = OptionalVariantMarshaller.ConvertToManaged(optional);
                OptionalVariantMarshaller.Free(optional);
            },
            native,
            managed);
    }

    [TestMethod]
    public void SafeArraysAreDestroyed()
    {
        object native = CreateDictionary();
        var managed = new Server();

        LeakDetector.AssertNoLeaks(
            () =>
            {
                RoundTrip<string?>(["a", null, "c"]);
                RoundTrip<object?>(["a", 1, new[] { "nested" }, native, managed]);
                RoundTrip<IDispatch>([managed, (IDispatch)native]);
                RoundTrip<int>([1, 2]);
                RoundTrip<ComDate>([new ComDate(1.5)]);
                RoundTrip<DayOfWeek>([DayOfWeek.Monday]);
                RoundTrip<string>([]);

                // The elements are converted to the type of the parameter.
                nint variants = SafeArrayMarshaller<object?>.ConvertToUnmanaged(["a", "b"]);
                _ = SafeArrayMarshaller<string>.ConvertToManaged(variants);
                SafeArrayMarshaller<object?>.Free(variants);

                nint objects = UnknownSafeArrayMarshaller.ConvertToUnmanaged([native, managed, null]);
                _ = UnknownSafeArrayMarshaller.ConvertToManaged(objects);
                UnknownSafeArrayMarshaller.Free(objects);

                foreach (Array array in (Array[])[new string?[,] { { "a", null }, { "c", "d" } }, new object?[,,] { { { "a", native }, { managed, new[] { "nested" } } } }, new[] { "a" }])
                {
                    nint psa = SafeArrayMarshaller.ConvertToUnmanaged(array);
                    _ = SafeArrayMarshaller.ConvertToManaged(psa);
                    if (array.Rank == 2)
                    {
                        _ = SafeArrayMarshaller.ConvertToManaged2D<object?>(psa);
                        Assert.ThrowsExactly<SafeArrayRankMismatchException>(() => SafeArrayMarshaller.ConvertToManaged3D<string>(psa));
                    }

                    SafeArrayMarshaller.Free(psa);
                }
            },
            native,
            managed);

        static void RoundTrip<T>(T[] values)
        {
            nint psa = SafeArrayMarshaller<T>.ConvertToUnmanaged(values);
            try
            {
                Assert.HasCount(values.Length, SafeArrayMarshaller<T>.ConvertToManaged(psa)!);
            }
            finally
            {
                SafeArrayMarshaller<T>.Free(psa);
            }
        }
    }

    [TestMethod]
    public void TheElementsConvertedBeforeAnErrorAreReleased()
    {
        // The last element has too many dimensions for a SAFEARRAY: the strings, arrays and objects before it are released.
        object native = CreateDictionary();
        var managed = new Server();
        var enumerator = new Enumerator([]);

        LeakDetector.AssertNoLeaks(
            () =>
            {
                object?[] vector = ["a", new[] { "b" }, native, managed, new int[1, 1, 1, 1]];
                object?[,] matrix = { { "a", new[] { "b" } }, { native, managed }, { 1, new int[1, 1, 1, 1] } };

                Assert.ThrowsExactly<NotSupportedException>(() => VariantMarshaller.ConvertToUnmanaged(vector));
                Assert.ThrowsExactly<NotSupportedException>(() => VariantMarshaller.ConvertToUnmanaged(matrix));
                Assert.ThrowsExactly<NotSupportedException>(() => SafeArrayMarshaller<object?>.ConvertToUnmanaged(vector));
                Assert.ThrowsExactly<NotSupportedException>(() => SafeArrayMarshaller.ConvertToUnmanaged(matrix));

                // An object without IDispatch in an array of IDispatch pointers.
                using var dispatches = ComSafeArray<object?>.Create(VarEnum.VT_DISPATCH, [2]);
                dispatches[0] = native;
                Assert.ThrowsExactly<InvalidCastException>(() => dispatches[1] = enumerator);
                Assert.ThrowsExactly<InvalidCastException>(() => dispatches[0] = enumerator);
            },
            native,
            managed,
            enumerator);
    }

    [TestMethod]
    public void ComBstrFreesTheStringsItOwns()
    {
        LeakDetector.AssertNoLeaks(static () =>
        {
            using (var disposed = ComBstr.Create("disposed"))
            {
                // A view does not own the string, and the copy in a VARIANT is owned by the VARIANT.
                using var view = ComBstr.FromPointer(disposed.Pointer, ownsString: false)!;
                Assert.AreEqual("disposed", view.ToString());
                VariantMarshaller.Free(VariantMarshaller.ConvertToUnmanaged(view));
            }

            ComBstr.CreateFromBytes([1, 2, 3]).Dispose();
            ComBstr.Create(string.Empty).Dispose();
            _ = ComBstr.Create("finalized");
            _ = ComBstr.CreateFromBytes([1]);

            // A detached string is owned by the one who detached it.
            nint pointer = ComBstr.Create("detached").Detach();
            ComBstr.FromPointer(pointer, ownsString: true)!.Dispose();
            _ = ComBstr.FromPointer(ComBstr.Create("adopted").Detach(), ownsString: true);

            var field = ComBstrValue.Allocate("field");
            _ = field.AsComBstr();
            field.Free();
        });
    }

    [TestMethod]
    public void ComSafeArrayDestroysTheArraysItOwns()
    {
        object native = CreateDictionary();
        var managed = new Server();

        LeakDetector.AssertNoLeaks(
            () =>
            {
                // Setting an element releases the string, the VARIANT or the object that it replaces.
                using (var strings = ComSafeArray<string?>.Create(["a", null, "c"]))
                {
                    strings[0] = "replaced";
                    strings[1] = "set";
                    strings[2] = null;
                    Assert.AreEqual("replaced", strings[0]);
                    CollectionAssert.AreEqual(new[] { "replaced", "set", null }, strings.ToArray());

                    using var view = ComSafeArray<string?>.FromPointer(strings.Pointer, ownsArray: false)!;
                    view[2] = "view";
                    VariantMarshaller.Free(VariantMarshaller.ConvertToUnmanaged(view));
                }

                using (var variants = ComSafeArray<object?>.Create(["a", native, new[] { "nested" }]))
                {
                    variants[0] = new[] { "array" };
                    variants[1] = "text";
                    variants[2] = managed;
                    variants[2] = native;
                    Assert.AreSame(native, variants[2]);
                    Assert.HasCount(3, variants.ToArray());
                }

                using (var unknowns = ComSafeArray<object?>.Create(VarEnum.VT_UNKNOWN, [2]))
                {
                    unknowns[0] = native;
                    unknowns[0] = managed;
                    unknowns[1] = native;
                    unknowns[1] = null;
                    Assert.AreSame(managed, unknowns[0]);
                }

                using (var dispatches = ComSafeArray<object?>.Create(VarEnum.VT_DISPATCH, [1, 2], [1, 1]))
                {
                    dispatches[1, 1] = native;
                    dispatches[1, 2] = new Wrapper(managed);
                    dispatches[1, 1] = managed;
                    Assert.AreEqual(2, dispatches.ToArray2D().Length);
                }

                using (var matrix = ComSafeArray<string?>.Create(new string?[,] { { "a", null }, { "c", "d" } }))
                {
                    matrix[0, 1] = "b";
                    matrix[1, 1] = "replaced";
                    Assert.AreEqual("replaced", matrix.ToArray2D()[1, 1]);
                }

                _ = ComSafeArray<string?>.Create(["finalized"]);
                _ = ComSafeArray<object?>.Create([native, managed, "finalized"]);

                // A detached array is owned by the one who detached it.
                nint pointer = ComSafeArray<string?>.Create(["detached"]).Detach();
                ComSafeArray<string?>.FromPointer(pointer, ownsArray: true)!.Dispose();
                _ = ComSafeArray<string?>.FromPointer(ComSafeArray<string?>.Create(["adopted"]).Detach(), ownsArray: true);
            },
            native,
            managed);
    }

    private static object CreateDictionary() => ComActivator.CreateInstance<object>(DictionaryClsid);

    // The values that VARIANTs hold: strings, arrays of any rank, and the references to native and managed COM objects.
    private static object?[] CreateValues(object native, Server managed) =>
    [
        "text",
        1.5m,
        new DateTime(2024, 2, 29),
        new[] { "a", "b" },
        new[] { 1, 2 },
        new[] { DayOfWeek.Monday },
        new string?[,] { { "a", null }, { "c", "d" } },
        new object?[] { "a", 1, new[] { "nested" }, native, managed },
        new object?[,,] { { { "a", native }, { managed, null } } },
        new IDispatch[] { managed, (IDispatch)native },
        native,
        managed,
        new Wrapper(managed),
        null,
    ];
}
