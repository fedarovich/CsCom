using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// Tests of the runtime support of COM events. The connection to COM objects is covered by the integration tests.
/// </summary>
[TestClass]
public sealed unsafe class EventRuntimeTests
{
    private const int DISP_E_PARAMNOTFOUND = unchecked((int)0x80020004);
    private const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    private const int DISP_E_EXCEPTION = unchecked((int)0x80020009);

    public enum TestColor
    {
        Red = 1,
        Blue = 3,
    }

    [TestMethod]
    public void ReadsThePositionalArgumentsInDeclarationOrder()
    {
        // DISPPARAMS stores the positional arguments in reverse order.
        var variants = stackalloc ComVariant[2];
        variants[0] = ComVariant.Create(2);
        variants[1] = ComVariant.Create(1);
        var parameters = new DispParams { rgvarg = variants, cArgs = 2 };

        var arguments = new EventArguments(&parameters, null);

        Assert.AreEqual(2, arguments.Count);
        Assert.AreEqual(1, arguments.Get<int>(0));
        Assert.AreEqual(2, arguments.Get<int>(1));
        Assert.AreEqual(0, arguments.Get<int>(2));
        Assert.IsNull(arguments.GetValue(-1));
    }

    [TestMethod]
    public void ReadsNamedAndOmittedArguments()
    {
        // The named arguments come first; their identifiers are the positions of the parameters.
        var variants = stackalloc ComVariant[3];
        variants[0] = ComVariant.Create(30);
        variants[1] = ComVariant.CreateRaw(VarEnum.VT_ERROR, DISP_E_PARAMNOTFOUND);
        variants[2] = ComVariant.Create(10);
        int* named = stackalloc int[] { 2 };
        var parameters = new DispParams { rgvarg = variants, cArgs = 3, rgdispidNamedArgs = named, cNamedArgs = 1 };

        var arguments = new EventArguments(&parameters, null);

        Assert.AreEqual(10, arguments.Get<int>(0));
        Assert.IsNull(arguments.GetValue(1));
        Assert.AreEqual(0, arguments.Get<int>(1));
        Assert.AreEqual(30, arguments.Get<int>(2));
    }

    [TestMethod]
    public void ConvertsArgumentsToTheParameterTypes()
    {
        var variants = stackalloc ComVariant[6];
        variants[5] = ComVariant.Create(3);
        variants[4] = ComVariant.Create(5);
        variants[3] = ComVariant.CreateRaw(VarEnum.VT_BOOL, (short)-1);
        variants[2] = ComVariant.CreateRaw(VarEnum.VT_DATE, new DateTime(2024, 2, 29).ToOADate());
        variants[1] = VariantMarshaller.ConvertToUnmanaged(new[] { 1, 2 });
        variants[0] = ComVariant.Create("text");
        var parameters = new DispParams { rgvarg = variants, cArgs = 6 };
        try
        {
            var arguments = new EventArguments(&parameters, null);

            Assert.AreEqual(TestColor.Blue, arguments.Get<TestColor>(0));
            Assert.AreEqual(5L, arguments.Get<long>(1));
            Assert.AreEqual(ComBool.True, arguments.Get<ComBool>(2));
            Assert.AreEqual(new DateTime(2024, 2, 29), (DateTime)arguments.Get<ComDate>(3));
            CollectionAssert.AreEqual(new long[] { 1, 2 }, arguments.GetArray<long>(4));
            Assert.AreEqual("text", arguments.Get<string?>(5));
            Assert.AreEqual("text", arguments.Get<object?>(5));
        }
        finally
        {
            for (int i = 0; i < 6; i++)
            {
                variants[i].Dispose();
            }
        }
    }

    [TestMethod]
    public void ConvertsArraysToTheExactElementType()
    {
        // The CLR also considers an int[] to be a TestColor[] and a string[] to be an object[], so the arrays are copied: the
        // elements of a string[] seen as an object[] could not be set to other objects.
        var variants = stackalloc ComVariant[2];
        variants[1] = VariantMarshaller.ConvertToUnmanaged(new[] { 1, 2 });
        variants[0] = VariantMarshaller.ConvertToUnmanaged(new[] { "a", "b" });
        var parameters = new DispParams { rgvarg = variants, cArgs = 2 };
        try
        {
            var arguments = new EventArguments(&parameters, null);

            var colors = arguments.GetArray<TestColor>(0)!;
            Assert.AreEqual(typeof(TestColor[]), colors.GetType());
            CollectionAssert.AreEqual(new[] { TestColor.Red, (TestColor)2 }, colors);

            var objects = arguments.GetArray<object?>(1)!;
            Assert.AreEqual(typeof(object[]), objects.GetType());
            objects[0] = 1;
            CollectionAssert.AreEqual(new object?[] { 1, "b" }, objects);
        }
        finally
        {
            variants[0].Dispose();
            variants[1].Dispose();
        }
    }

    [TestMethod]
    public void CopiesTheArraysOfTheUnderlyingTypeOfEnums()
    {
        // The int arrays of SAFEARRAYs of enums are copied as a block into zero-based arrays of the same shape; the elements of
        // other types are converted one by one.
        var variants = stackalloc ComVariant[3];
        variants[2] = VariantMarshaller.ConvertToUnmanaged(new[,] { { 1, 3, 1 }, { 3, 1, 3 } });
        variants[1] = VariantMarshaller.ConvertToUnmanaged(new[,,] { { { 1, 3 } }, { { 3, 1 } } });
        variants[0] = VariantMarshaller.ConvertToUnmanaged(new byte[] { 3, 1 });
        var parameters = new DispParams { rgvarg = variants, cArgs = 3 };
        try
        {
            var arguments = new EventArguments(&parameters, null);

            var grid = arguments.GetArray2D<TestColor>(0)!;
            Assert.AreEqual(typeof(TestColor[,]), grid.GetType());
            Assert.AreEqual((2, 3), (grid.GetLength(0), grid.GetLength(1)));
            Assert.AreEqual((TestColor.Blue, TestColor.Blue), (grid[0, 1], grid[1, 2]));

            var cube = arguments.GetArray3D<TestColor>(1)!;
            Assert.AreEqual((2, 1, 2), (cube.GetLength(0), cube.GetLength(1), cube.GetLength(2)));
            Assert.AreEqual((TestColor.Blue, TestColor.Blue), (cube[0, 0, 1], cube[1, 0, 0]));

            CollectionAssert.AreEqual(new[] { TestColor.Blue, TestColor.Red }, arguments.GetArray<TestColor>(2));
        }
        finally
        {
            for (int i = 0; i < 3; i++)
            {
                variants[i].Dispose();
            }
        }

        // Arrays with other lower bounds than zero, e.g. those of VB, are copied from their first elements.
        var vector = Array.CreateInstance(typeof(int), [2], [1]);
        vector.SetValue(3, 2);
        var matrix = Array.CreateInstance(typeof(int), [2, 2], [1, 5]);
        matrix.SetValue(3, 1, 6);
        matrix.SetValue(1, 2, 5);
        CollectionAssert.AreEqual(new[] { default, TestColor.Blue }, Conversions.ToArray<TestColor>(vector));
        var copy = Conversions.ToArray2D<TestColor>(matrix)!;
        Assert.AreEqual((0, 0), (copy.GetLowerBound(0), copy.GetLowerBound(1)));
        Assert.AreEqual((TestColor.Blue, TestColor.Red), (copy[0, 1], copy[1, 0]));
    }

    [TestMethod]
    public void WritesBackByReferenceArguments()
    {
        short flag = 0;
        int number = 5;
        nint text = Marshal.StringToBSTR("old");
        ComVariant any = ComVariant.Create(1);
        var variants = stackalloc ComVariant[5];
        variants[4] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_BOOL, (nint)(&flag));
        variants[3] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_I4, (nint)(&number));
        variants[2] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_BSTR, (nint)(&text));
        variants[1] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_VARIANT, (nint)(&any));
        variants[0] = ComVariant.Create(7);
        var parameters = new DispParams { rgvarg = variants, cArgs = 5 };
        try
        {
            var arguments = new EventArguments(&parameters, null);
            Assert.IsFalse(arguments.Get<bool>(0));
            Assert.AreEqual(5, arguments.Get<int>(1));
            Assert.AreEqual("old", arguments.Get<string?>(2));
            Assert.AreEqual(1, arguments.Get<object?>(3));

            arguments.Set(0, true);
            arguments.Set(1, "42");
            arguments.Set(2, "new");
            arguments.Set(3, "any");
            arguments.Set(4, 8);

            Assert.AreEqual((short)-1, flag);
            Assert.AreEqual(42, number);
            Assert.AreEqual("new", Marshal.PtrToStringBSTR(text));
            Assert.AreEqual("any", any.As<string>());
            Assert.AreEqual(7, arguments.Get<int>(4), "Arguments passed by value are not changed.");
        }
        finally
        {
            Marshal.FreeBSTR(text);
            any.Dispose();
        }
    }

    [TestMethod]
    public void WritesBackNullStringsObjectsAndArrays()
    {
        // E.g. the ppDisp of an event like NewWindow2, which the handler leaves null: the values that follow are still set.
        nint text = Marshal.StringToBSTR("old");
        nint dispatch = 0;
        nint array = 0;
        short cancel = 0;
        var variants = stackalloc ComVariant[4];
        variants[3] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_BSTR, (nint)(&text));
        variants[2] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_DISPATCH, (nint)(&dispatch));
        variants[1] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_ARRAY | VarEnum.VT_I4, (nint)(&array));
        variants[0] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_BOOL, (nint)(&cancel));
        var parameters = new DispParams { rgvarg = variants, cArgs = 4 };
        try
        {
            var arguments = new EventArguments(&parameters, null);

            arguments.Set<string?>(0, null);
            arguments.Set<object?>(1, null);
            arguments.Set<int[]?>(2, null);
            arguments.Set(3, true);

            Assert.AreEqual((nint)0, text, "The previous string is freed.");
            Assert.AreEqual((nint)0, dispatch);
            Assert.AreEqual((nint)0, array);
            Assert.AreEqual((short)-1, cancel);
        }
        finally
        {
            Marshal.FreeBSTR(text);
        }
    }

    [TestMethod]
    public void OptionalVariantsAreNullWhenTheArgumentIsOmitted()
    {
        // A COM caller of an interface implemented in .NET omits the argument: it is not the integer of the error code.
        var omitted = OptionalVariantMarshaller.ConvertToUnmanaged(null);
        Assert.AreEqual(VarEnum.VT_ERROR, omitted.VarType);
        Assert.IsNull(OptionalVariantMarshaller.ConvertToManaged(omitted));

        var value = OptionalVariantMarshaller.ConvertToUnmanaged(5);
        Assert.AreEqual(5, OptionalVariantMarshaller.ConvertToManaged(value));
    }

    [TestMethod]
    public void SetsTheResultIfTheCallerExpectsOne()
    {
        var parameters = new DispParams();
        ComVariant result = default;

        new EventArguments(&parameters, &result).SetResult("done");
        new EventArguments(&parameters, null).SetResult("ignored");

        try
        {
            Assert.AreEqual("done", result.As<string>());
        }
        finally
        {
            result.Dispose();
        }
    }

    [TestMethod]
    public void InvokeHandlerReportsUnknownEventsAndExceptions()
    {
        var parameters = new DispParams();
        ExcepInfo excepInfo = default;
        var handler = new TestHandler();

        Assert.AreEqual(0, DispatchEventSink.InvokeHandler(handler, 1, &parameters, null, &excepInfo));
        Assert.AreEqual(DISP_E_MEMBERNOTFOUND, DispatchEventSink.InvokeHandler(handler, 2, &parameters, null, &excepInfo));
        Assert.AreEqual(DISP_E_EXCEPTION, DispatchEventSink.InvokeHandler(handler, 3, &parameters, null, &excepInfo));
        try
        {
            Assert.AreEqual("boom", Marshal.PtrToStringBSTR(excepInfo.bstrDescription));
            Assert.AreEqual(new InvalidOperationException().HResult, excepInfo.scode);
        }
        finally
        {
            Marshal.FreeBSTR(excepInfo.bstrSource);
            Marshal.FreeBSTR(excepInfo.bstrDescription);
        }

        Assert.AreEqual(new InvalidOperationException().HResult, DispatchEventSink.InvokeHandler(handler, 3, &parameters, null, null));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 3 }, handler.DispIds);
    }

    [TestMethod]
    public void InvokeHandlerDoesNotReadTheResultVariableOfTheCaller()
    {
        // The result is an [out] parameter, so the caller may pass a variable that is not initialized. Clearing a VARIANT
        // with this invalid type fails; one with a pointer in it would free memory that is not allocated.
        var parameters = new DispParams();
        var handler = new TestHandler();
        ComVariant result;
        new Span<byte>(&result, sizeof(ComVariant)).Fill(0);
        *(ushort*)&result = 0xFF;

        Assert.AreEqual(0, DispatchEventSink.InvokeHandler(handler, 4, &parameters, &result, null));
        try
        {
            Assert.AreEqual("done", result.As<string>());
        }
        finally
        {
            result.Dispose();
        }

        // An event without a result returns an empty VARIANT, and so does a handler that fails after it set the result.
        *(ushort*)&result = 0xFF;
        Assert.AreEqual(0, DispatchEventSink.InvokeHandler(handler, 1, &parameters, &result, null));
        Assert.AreEqual(VarEnum.VT_EMPTY, result.VarType);

        *(ushort*)&result = 0xFF;
        Assert.AreEqual(new InvalidOperationException().HResult, DispatchEventSink.InvokeHandler(handler, 5, &parameters, &result, null));
        Assert.AreEqual(VarEnum.VT_EMPTY, result.VarType);
    }

    [TestMethod]
    public void EventSubjectPushesValuesToTheSubscribersInOrder()
    {
        var subject = new EventSubject<int>();
        var values = new List<string>();
        using var first = subject.Subscribe(new TestObserver<int>(v => values.Add($"first {v}")));
        var second = subject.Subscribe(new TestObserver<int>(v => values.Add($"second {v}")));

        subject.OnNext(1);
        second.Dispose();
        second.Dispose();
        subject.OnNext(2);

        CollectionAssert.AreEqual(new[] { "first 1", "second 1", "first 2" }, values);
    }

    [TestMethod]
    public void EventSubjectPropagatesExceptionsOfTheSubscribers()
    {
        var subject = new EventSubject<int>();
        subject.Subscribe(new TestObserver<int>(static _ => throw new InvalidOperationException("boom")));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => subject.OnNext(1));
        Assert.AreEqual("boom", exception.Message);
    }

    [TestMethod]
    public void EventSubjectToleratesUnsubscribingWhilePushing()
    {
        var subject = new EventSubject<int>();
        var values = new List<int>();
        IDisposable? subscription = null;
        subscription = subject.Subscribe(new TestObserver<int>(v =>
        {
            values.Add(v);
            subscription!.Dispose();
        }));

        subject.OnNext(1);
        subject.OnNext(2);

        CollectionAssert.AreEqual(new[] { 1 }, values);
    }

    [TestMethod]
    public void EventSubjectCompletesTheSubscribersOnce()
    {
        var subject = new EventSubject<int>();
        int completed = 0;
        subject.Subscribe(new TestObserver<int>(static _ => Assert.Fail("No values after the completion."), () => completed++));

        subject.OnCompleted();
        subject.OnCompleted();
        subject.OnNext(1);
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => completed++)).Dispose();

        Assert.AreEqual(2, completed);
    }

    private sealed class TestHandler : IDispatchEventHandler
    {
        public List<int> DispIds { get; } = [];

        public bool Invoke(int dispId, EventArguments arguments)
        {
            DispIds.Add(dispId);
            return dispId switch
            {
                1 => true,
                3 => throw new InvalidOperationException("boom"),
                4 => SetResult(arguments, "done"),
                5 => SetResult(arguments, "lost") ? throw new InvalidOperationException("boom") : false,
                _ => false,
            };
        }

        private static bool SetResult(EventArguments arguments, string value)
        {
            arguments.SetResult(value);
            return true;
        }
    }

    private sealed class TestObserver<T>(Action<T> next, Action? completed = null) : IObserver<T>
    {
        public void OnNext(T value) => next(value);

        public void OnError(Exception error) => throw new AssertFailedException("Unexpected error.", error);

        public void OnCompleted() => completed?.Invoke();
    }

    /// <summary>Exposes the array conversions of the generated late-bound wrappers.</summary>
    private abstract class Conversions(IDispatch dispatch) : DispatchObject(dispatch)
    {
        public static new T[]? ToArray<T>(object? value) => DispatchObject.ToArray<T>(value);

        public static new T[,]? ToArray2D<T>(object? value) => DispatchObject.ToArray2D<T>(value);
    }
}
