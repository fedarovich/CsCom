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
    public void ReadsArgumentsPassedByReferenceWithoutAReferenceAsNull()
    {
        var variants = stackalloc ComVariant[4];
        variants[3] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_DISPATCH, (nint)0);
        variants[2] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_VARIANT, (nint)0);
        variants[1] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_ARRAY | VarEnum.VT_VARIANT, (nint)0);
        variants[0] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_I4, (nint)0);
        var parameters = new DispParams { rgvarg = variants, cArgs = 4 };

        var arguments = new EventArguments(&parameters, null);

        Assert.IsNull(arguments.Get<IDispatch?>(0));
        Assert.IsNull(arguments.GetValue(1));
        Assert.IsNull(arguments.GetArray<object?>(2));
        Assert.AreEqual(0, arguments.Get<int>(3));
    }

    [TestMethod]
    public void ReadsObjectsAsUniqueObjects()
    {
        // An interface pointer that the vtable of a dual event interface passes as a unique object is read as one from the
        // VARIANT of IDispatch::Invoke too, also by reference and in a VARIANT passed by reference. An argument without an
        // object is read like Get reads it, with the same error for a value of another type.
        var dictionary = ComActivator.CreateInstance<IDispatch>("Scripting.Dictionary");
        nint pointer = (nint)ComObjectMarshaller<IDispatch>.ConvertToUnmanaged(dictionary);
        ComVariant inner = ComVariant.CreateRaw(VarEnum.VT_DISPATCH, pointer);
        var variants = stackalloc ComVariant[7];
        variants[6] = ComVariant.CreateRaw(VarEnum.VT_DISPATCH, pointer);
        variants[5] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_DISPATCH, (nint)(&pointer));
        variants[4] = ComVariant.CreateRaw(VarEnum.VT_BYREF | VarEnum.VT_VARIANT, (nint)(&inner));
        variants[3] = ComVariant.CreateRaw(VarEnum.VT_DISPATCH, (nint)0);
        variants[2] = ComVariant.CreateRaw(VarEnum.VT_ERROR, DISP_E_PARAMNOTFOUND);
        variants[1] = default;
        variants[0] = ComVariant.Create(5);
        var parameters = new DispParams { rgvarg = variants, cArgs = 7 };
        try
        {
            var arguments = new EventArguments(&parameters, null);
            for (int i = 0; i < 3; i++)
            {
                IDispatch? unique = arguments.GetUnique<IDispatch>(i);
                Assert.IsInstanceOfType<ComObject>(unique);
                Assert.AreNotSame(dictionary, unique);
                Assert.AreSame(dictionary, arguments.Get<IDispatch?>(i));
                ComMarshalling.Release(unique);
                Assert.ThrowsExactly<ObjectDisposedException>(() => ComObjectMarshaller<object>.ConvertToUnmanaged(unique));

                var wrapper = arguments.GetUniqueWrapper<MemoryLeakTests.Wrapper>(i);
                Assert.IsNotNull(wrapper);
                Assert.AreNotSame(dictionary, wrapper.Dispatch);
                ComMarshalling.Release(wrapper);
            }

            for (int i = 3; i < 6; i++)
            {
                Assert.IsNull(arguments.GetUnique<IDispatch>(i));
                Assert.IsNull(arguments.GetUniqueWrapper<MemoryLeakTests.Wrapper>(i));
            }

            // A ref struct cannot be used in a lambda.
            Type? expected = null;
            Type? actual = null;
            try
            {
                _ = arguments.Get<IDispatch?>(6);
            }
            catch (Exception e)
            {
                expected = e.GetType();
            }

            try
            {
                _ = arguments.GetUnique<IDispatch>(6);
            }
            catch (Exception e)
            {
                actual = e.GetType();
            }

            Assert.IsNotNull(expected);
            Assert.AreEqual(expected, actual);
        }
        finally
        {
            // The VARIANTs borrow the one reference of the pointer.
            Marshal.Release(pointer);
        }
    }

    [TestMethod]
    public void GetUniqueRejectsAnInterfaceThatIsNoComInterfaceAlsoWithoutAnObject()
    {
        // Like the marshallers of such an interface, at its first use rather than at the first event that passes an object:
        // an omitted, empty, null or missing argument, and one of another type.
        var variants = stackalloc ComVariant[4];
        variants[3] = ComVariant.CreateRaw(VarEnum.VT_ERROR, DISP_E_PARAMNOTFOUND);
        variants[2] = default;
        variants[1] = ComVariant.CreateRaw(VarEnum.VT_DISPATCH, (nint)0);
        variants[0] = ComVariant.Create("text");
        var parameters = new DispParams { rgvarg = variants, cArgs = 4 };
        try
        {
            var arguments = new EventArguments(&parameters, null);
            for (int i = 0; i <= 4; i++)
            {
                Assert.AreEqual(typeof(NotSupportedException), GetUniqueDisposable(arguments, i)?.GetType(), $"Argument {i}");
            }
        }
        finally
        {
            variants[0].Dispose();
        }

        // A ref struct cannot be used in a lambda.
        static Exception? GetUniqueDisposable(EventArguments arguments, int index)
        {
            try
            {
                _ = arguments.GetUnique<IDisposable>(index);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
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
            Assert.IsNull(arguments.GetValue(1), "An object that is a null pointer is read as null.");
            Assert.IsNull(arguments.GetArray<int>(2));

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
    [DataRow(1)]
    [DataRow(0)]
    public void InvokeHandlerReportsAnExceptionWithoutAnErrorCodeAsAFailure(int code)
    {
        // An exception whose HResult is a success, e.g. S_FALSE, would tell the source that the event succeeded.
        const int E_FAIL = unchecked((int)0x80004005);
        var parameters = new DispParams();
        var handler = new ThrowingHandler(() => new COMException("x", code));

        Assert.AreEqual(E_FAIL, DispatchEventSink.InvokeHandler(handler, 1, &parameters, null, null));

        ExcepInfo excepInfo = default;
        Assert.AreEqual(DISP_E_EXCEPTION, DispatchEventSink.InvokeHandler(handler, 1, &parameters, null, &excepInfo));
        try
        {
            Assert.AreEqual(E_FAIL, excepInfo.scode);
            Assert.AreEqual("x", Marshal.PtrToStringBSTR(excepInfo.bstrDescription));
        }
        finally
        {
            Marshal.FreeBSTR(excepInfo.bstrSource);
            Marshal.FreeBSTR(excepInfo.bstrDescription);
        }
    }

    [TestMethod]
    public void InvokeHandlerReportsAnExceptionWhoseMessageThrowsWithoutItsDescription()
    {
        // The exception of the handler is reported, not that of its message, and without strings, which are set completely
        // or not at all: the caller only frees them with DISP_E_EXCEPTION.
        var parameters = new DispParams();
        var handler = new ThrowingHandler(static () => new UnreadableException());
        ExcepInfo excepInfo = default;

        Assert.AreEqual(DISP_E_EXCEPTION, DispatchEventSink.InvokeHandler(handler, 1, &parameters, null, &excepInfo));
        Assert.AreEqual(UnreadableException.Code, excepInfo.scode);
        Assert.AreEqual((nint)0, excepInfo.bstrSource);
        Assert.AreEqual((nint)0, excepInfo.bstrDescription);
        Assert.AreEqual(UnreadableException.Code, DispatchEventSink.InvokeHandler(handler, 1, &parameters, null, null));
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

    [TestMethod]
    public void EventSubjectCompletesEveryObserverWhenOneThrows()
    {
        // The completion is final and removes the observers: the exception of one does not stop the others, and the
        // exceptions are thrown together once every observer is completed.
        var subject = new EventSubject<int>();
        var completed = new List<string>();
        subject.Subscribe(new TestObserver<int>(static _ => { }, () =>
        {
            completed.Add("first");
            throw new InvalidOperationException("first");
        }));
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => completed.Add("second")));
        subject.Subscribe(new TestObserver<int>(static _ => { }, () =>
        {
            completed.Add("third");
            throw new InvalidOperationException("third");
        }));

        var exception = Assert.ThrowsExactly<AggregateException>(subject.OnCompleted);
        CollectionAssert.AreEqual(new[] { "first", "second", "third" }, completed);
        CollectionAssert.AreEqual(new[] { "first", "third" }, exception.InnerExceptions.Select(static e => e.Message).ToArray());

        subject.OnCompleted();
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => completed.Add("later"))).Dispose();
        CollectionAssert.AreEqual(new[] { "first", "second", "third", "later" }, completed);
    }

    [TestMethod]
    public void EventSubjectThrowsTheExceptionOfASingleObserverAsItIs()
    {
        var subject = new EventSubject<int>();
        var failure = new InvalidOperationException("only");
        bool completed = false;
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => throw failure));
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => completed = true));

        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(subject.OnCompleted));
        Assert.IsTrue(completed);
    }

    [TestMethod]
    public void EventSubjectsAreCompletedTogether()
    {
        // The observables of an event sink: the exception of an observer of one does not stop the completion of the others.
        var first = new EventSubject<int>();
        var second = new EventSubject<string>();
        var failure = new InvalidOperationException("first");
        bool completed = false;
        first.Subscribe(new TestObserver<int>(static _ => { }, () => throw failure));
        second.Subscribe(new TestObserver<string>(static _ => { }, () => completed = true));

        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => EventSubject.Complete(first, second)));
        Assert.IsTrue(completed);

        // The subjects are checked before any of them is completed.
        var third = new EventSubject<int>();
        bool thirdCompleted = false;
        third.Subscribe(new TestObserver<int>(static _ => { }, () => thirdCompleted = true));
        Assert.ThrowsExactly<ArgumentNullException>(() => EventSubject.Complete(third, null!));
        Assert.IsFalse(thirdCompleted);
    }

    [TestMethod]
    public void EventSubjectsAreCompletedAfterTheDisconnectionInTheContextOfTheCaller()
    {
        // Like the code after an await: once the disconnection has completed, in the synchronization context of the caller.
        var subject = new EventSubject<int>();
        bool completed = false;
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => completed = true));
        var disconnection = new TaskCompletionSource();
        var context = new QueueingContext();
        var original = SynchronizationContext.Current;
        Task task;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            task = EventSubject.CompleteAfterAsync(new ValueTask(disconnection.Task), subject).AsTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
        }

        Assert.IsFalse(task.IsCompleted);
        disconnection.SetResult();
        Assert.IsFalse(completed);
        Assert.IsFalse(task.IsCompleted);

        context.RunPosts();
        Assert.IsTrue(completed);
        Assert.IsTrue(task.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void TheExceptionOfTheDisconnectionIsThrownBeforeThoseOfTheObservers()
    {
        // Alone, it is thrown as it is, after the completion, so that a COMException of a sink that stays connected can be
        // caught; with those of the observers, all of them are thrown, the one of the disconnection first.
        var failure = new COMException("The sink stays connected.", unchecked((int)0x8001010E));
        var subject = new EventSubject<int>();
        bool completed = false;
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => completed = true));
        Assert.AreSame(failure, Assert.ThrowsExactly<COMException>(() => Wait(EventSubject.CompleteAfterAsync(ValueTask.FromException(failure), subject))));
        Assert.IsTrue(completed);

        var observerFailure = new InvalidOperationException("observer");
        var failing = new EventSubject<int>();
        failing.Subscribe(new TestObserver<int>(static _ => { }, () => throw observerFailure));
        var both = Assert.ThrowsExactly<AggregateException>(() => Wait(EventSubject.CompleteAfterAsync(ValueTask.FromException(failure), failing)));
        CollectionAssert.AreEqual(new Exception[] { failure, observerFailure }, both.InnerExceptions.ToArray());

        var alone = new EventSubject<int>();
        alone.Subscribe(new TestObserver<int>(static _ => { }, () => throw observerFailure));
        Assert.AreSame(observerFailure, Assert.ThrowsExactly<InvalidOperationException>(() => Wait(EventSubject.CompleteAfterAsync(default, alone))));

        static void Wait(ValueTask task) => task.AsTask().GetAwaiter().GetResult();
    }

    [TestMethod]
    public void CompleteAfterAsyncChecksTheSubjectsAtOnce()
    {
        // A null subject is rejected when the method is called, not by the task, and no subject is completed then. The
        // subjects are copied, so that the caller cannot replace one with null while the disconnection runs.
        var subject = new EventSubject<int>();
        bool completed = false;
        subject.Subscribe(new TestObserver<int>(static _ => { }, () => completed = true));
        var disconnection = new TaskCompletionSource();

        Assert.ThrowsExactly<ArgumentNullException>(() => EventSubject.CompleteAfterAsync(new ValueTask(disconnection.Task), subject, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => EventSubject.CompleteAfterAsync(new ValueTask(disconnection.Task), null!));
        disconnection.SetResult();
        Assert.IsFalse(completed);

        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventSubject[] subjects = [subject];
        var task = EventSubject.CompleteAfterAsync(new ValueTask(pending.Task), subjects).AsTask();
        subjects[0] = null!;
        pending.SetResult();
        Assert.IsTrue(task.Wait(TimeSpan.FromSeconds(30)));
        Assert.IsTrue(completed);
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

    /// <summary>A handler that throws the exception that it creates, for every event.</summary>
    internal sealed class ThrowingHandler(Func<Exception> create) : IDispatchEventHandler
    {
        public bool Invoke(int dispId, EventArguments arguments) => throw create();
    }

    /// <summary>An exception whose message cannot be read.</summary>
    internal sealed class UnreadableException : Exception
    {
        public const int Code = unchecked((int)0x80040001);

        public UnreadableException()
        {
            HResult = Code;
        }

        public override string Message => throw new InvalidOperationException("The message cannot be read.");
    }

    private sealed class TestObserver<T>(Action<T> next, Action? completed = null) : IObserver<T>
    {
        public void OnNext(T value) => next(value);

        public void OnError(Exception error) => throw new AssertFailedException("Unexpected error.", error);

        public void OnCompleted() => completed?.Invoke();
    }

    // A synchronization context that runs what is posted to it only when asked, like the one of a user interface thread.
    private sealed class QueueingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posts = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_posts)
            {
                _posts.Enqueue((d, state));
            }
        }

        public void RunPosts()
        {
            var original = Current;
            SetSynchronizationContext(this);
            try
            {
                while (true)
                {
                    (SendOrPostCallback Callback, object? State) post;
                    lock (_posts)
                    {
                        if (!_posts.TryDequeue(out post))
                        {
                            return;
                        }
                    }

                    post.Callback(post.State);
                }
            }
            finally
            {
                SetSynchronizationContext(original);
            }
        }
    }

    /// <summary>Exposes the array conversions of the generated late-bound wrappers.</summary>
    private abstract class Conversions(IDispatch dispatch) : DispatchObject(dispatch)
    {
        public static new T[]? ToArray<T>(object? value) => DispatchObject.ToArray<T>(value);

        public static new T[,]? ToArray2D<T>(object? value) => DispatchObject.ToArray2D<T>(value);
    }
}
