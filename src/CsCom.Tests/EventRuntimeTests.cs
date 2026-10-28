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
                _ => false,
            };
        }
    }

    private sealed class TestObserver<T>(Action<T> next, Action? completed = null) : IObserver<T>
    {
        public void OnNext(T value) => next(value);

        public void OnError(Exception error) => throw new AssertFailedException("Unexpected error.", error);

        public void OnCompleted() => completed?.Invoke();
    }
}
