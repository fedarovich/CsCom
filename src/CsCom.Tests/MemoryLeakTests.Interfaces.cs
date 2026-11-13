using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// The calls of a generated interface, in which the COM source generator uses the marshallers: from the caller through the
/// table of the interface to a COM object implemented in .NET.
/// </summary>
public sealed unsafe partial class MemoryLeakTests
{
    [TestMethod]
    public void GeneratedInterfacesFreeStringsAndArrays()
    {
        object native = CreateDictionary();
        var managed = new Server();
        IInterop proxy = CreateProxy<IInterop>(new InteropObject());

        LeakDetector.AssertNoLeaks(
            () =>
            {
                Assert.AreEqual("text", proxy.EchoString("text"));
                Assert.IsNull(proxy.EchoString(null));
                foreach (var (value, expected) in ((string?, string?)[])[("text", "text!"), (null, "new"), ("clear", null)])
                {
                    string? text = value;
                    proxy.ReplaceString(ref text);
                    Assert.AreEqual(expected, text);
                }

                CollectionAssert.AreEqual(new[] { "a", null }, proxy.EchoStrings(["a", null]));
                Assert.IsNull(proxy.EchoStrings(null));
                foreach (var (value, expected) in ((string?[]?, string?[]?)[])[(["a"], ["a", "new"]), (null, ["new"]), ([], null)])
                {
                    string?[]? strings = value;
                    proxy.ReplaceStrings(ref strings);
                    CollectionAssert.AreEqual(expected, strings);
                }

                Assert.AreEqual(4, proxy.EchoArray(new string?[,] { { "a", null }, { "c", "d" } })!.Length);
                Assert.AreEqual(3, proxy.EchoArray(new object?[] { "a", native, managed })!.Length);
                CollectionAssert.AreEqual(new[] { native, managed, null }, proxy.EchoObjects([native, managed, null]));
                Assert.AreSame(managed, proxy.EchoWrapper(new Wrapper(managed))!.Dispatch);
            },
            native,
            managed);
    }

    [TestMethod]
    public void GeneratedInterfacesFollowTheOwnershipOfComBstr()
    {
        IInterop proxy = CreateProxy<IInterop>(new InteropObject());

        LeakDetector.AssertNoLeaks(() =>
        {
            // The caller keeps the string it passes and owns the string it receives: a copy made by the object, a copy of the
            // view that the object received, or a copy of a string that the object keeps.
            foreach (string text in (string[])["text", "view", "held"])
            {
                using var input = ComBstr.Create(text);
                using var result = proxy.EchoBstr(input);
                Assert.AreEqual(text, result!.ToString());
                Assert.AreEqual(text, input.ToString());
                Assert.IsTrue(result.OwnsString);
                Assert.AreNotEqual(input.Pointer, result.Pointer);

                // A result that is not disposed is freed by its finalizer.
                _ = proxy.EchoBstr(input);
            }

            Assert.IsNull(proxy.EchoBstr(null));

            // The object frees the string that it replaces, and the caller owns the new one.
            foreach (var (text, expected) in ((string?, string?)[])[("keep", "keep"), ("view", "view"), ("text", "text!"), ("clear", null), ("held", "held"), (null, "new")])
            {
                ComBstr? original = text is null ? null : ComBstr.Create(text);
                ComBstr? value = original;
                proxy.ReplaceBstr(ref value);
                Assert.AreEqual(expected, value?.ToString());
                Assert.AreEqual(text is "keep" or "view", ReferenceEquals(original, value));
                value?.Dispose();
                original?.Dispose();
            }

            using var sentence = ComBstr.Create("first rest");
            proxy.SplitBstr(sentence, out ComBstr? first, out ComBstr? rest);
            using (first)
            using (rest)
            {
                Assert.AreEqual(("first", "rest"), (first!.ToString(), rest!.ToString()));
            }

            // A failed call returns nothing: the caller keeps its string, and the new one of the object is freed by its finalizer.
            ComBstr? kept = sentence;
            Assert.Throws<Exception>(() => proxy.FailBstr(ref kept, out _));
            Assert.AreSame(sentence, kept);
            Assert.AreEqual("first rest", sentence.ToString());
        });
    }

    [TestMethod]
    public void GeneratedInterfacesFollowTheOwnershipOfComSafeArray()
    {
        object native = CreateDictionary();
        IInterop proxy = CreateProxy<IInterop>(new InteropObject());

        LeakDetector.AssertNoLeaks(
            () =>
            {
                foreach (string text in (string[])["text", "view", "held"])
                {
                    using var input = ComSafeArray<object?>.Create([text, native, new[] { "nested" }]);
                    using var result = proxy.EchoSafeArray(input);
                    Assert.AreEqual(text, result![0]);
                    Assert.AreEqual(text, input[0]);
                    Assert.IsTrue(result.OwnsArray);
                    Assert.AreNotEqual(input.Pointer, result.Pointer);

                    // A result that is not disposed is destroyed by its finalizer.
                    _ = proxy.EchoSafeArray(input);
                }

                Assert.IsNull(proxy.EchoSafeArray(null));

                // The object destroys the array that it replaces, and the caller owns the new one.
                foreach (var (text, expected) in ((string?, string?)[])[("keep", "keep"), ("view", "view"), ("change", "changed"), ("text", "text!"), ("clear", null), ("held", "held"), (null, "new")])
                {
                    ComSafeArray<object?>? original = text is null ? null : ComSafeArray<object?>.Create([text, native, new[] { "nested" }]);
                    ComSafeArray<object?>? value = original;
                    proxy.ReplaceSafeArray(ref value);
                    Assert.AreEqual(expected, value?[0]);
                    Assert.AreEqual(text is "keep" or "view" or "change", ReferenceEquals(original, value));
                    value?.Dispose();
                    original?.Dispose();
                }
            },
            native);
    }

    [TestMethod]
    public void FailedCallsOfGeneratedInterfacesFreeTheirArguments()
    {
        object native = CreateDictionary();
        var managed = new Server();
        IInterop proxy = CreateProxy<IInterop>(new InteropObject());

        LeakDetector.AssertNoLeaks(
            () =>
            {
                using var bstr = ComBstr.Create("bstr");
                using var safeArray = ComSafeArray<object?>.Create(["a", native]);
                Assert.Throws<Exception>(() => proxy.Fail("text", ["a", null], new object?[,] { { "a", native } }, [native, managed], bstr, safeArray));
                Assert.AreEqual("bstr", bstr.ToString());
                Assert.AreSame(native, safeArray[1]);

                // An argument that cannot be converted: the arguments before it are freed, and the object is not called.
                Assert.ThrowsExactly<NotSupportedException>(() => proxy.Fail("text", ["a", null], new int[1, 1, 1, 1], [native, managed], bstr, safeArray));
            },
            native,
            managed);
    }

    /// <summary>
    /// An interface like the generated ones, with the marshallers that CsCom selects for strings, arrays and wrappers. It has no
    /// <c>VARIANT</c> parameters, which need an assembly without runtime marshalling: the P/Invokes of the tests rely on it.
    /// </summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
    [Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A10F0")]
    internal partial interface IInterop
    {
        string? EchoString(string? value);

        void ReplaceString(ref string? value);

        [return: MarshalUsing(typeof(SafeArrayMarshaller<string>))]
        string?[]? EchoStrings([MarshalUsing(typeof(SafeArrayMarshaller<string>))] string?[]? values);

        void ReplaceStrings([MarshalUsing(typeof(SafeArrayMarshaller<string>))] ref string?[]? values);

        [return: MarshalUsing(typeof(SafeArrayMarshaller))]
        Array? EchoArray([MarshalUsing(typeof(SafeArrayMarshaller))] Array? values);

        [return: MarshalUsing(typeof(UnknownSafeArrayMarshaller))]
        object?[]? EchoObjects([MarshalUsing(typeof(UnknownSafeArrayMarshaller))] object?[]? values);

        [return: MarshalUsing(typeof(DispatchWrapperMarshaller<Wrapper>))]
        Wrapper? EchoWrapper([MarshalUsing(typeof(DispatchWrapperMarshaller<Wrapper>))] Wrapper? value);

        ComBstr? EchoBstr(ComBstr? value);

        void ReplaceBstr(ref ComBstr? value);

        void SplitBstr(ComBstr? value, out ComBstr? first, out ComBstr? rest);

        void FailBstr(ref ComBstr? value, out ComBstr? result);

        ComSafeArray<object?>? EchoSafeArray(ComSafeArray<object?>? values);

        void ReplaceSafeArray(ref ComSafeArray<object?>? values);

        void Fail(
            string? text,
            [MarshalUsing(typeof(SafeArrayMarshaller<string>))] string?[]? strings,
            [MarshalUsing(typeof(SafeArrayMarshaller))] Array? array,
            [MarshalUsing(typeof(UnknownSafeArrayMarshaller))] object?[]? objects,
            ComBstr? bstr,
            ComSafeArray<object?>? safeArray);
    }

    /// <summary>
    /// A COM object implemented in .NET. The first string of a <see cref="ComBstr"/> or of a <see cref="ComSafeArray{T}"/>
    /// selects what it returns: a new string or array that it owns, the view that it received, or a view of one that it keeps.
    /// </summary>
    [GeneratedComClass]
    internal sealed partial class InteropObject : IInterop
    {
        private readonly ComBstr _heldBstr = ComBstr.Create("held");
        private readonly ComSafeArray<object?> _heldArray = ComSafeArray<object?>.Create(["held"]);

        public string? EchoString(string? value) => value;

        public void ReplaceString(ref string? value) => value = value switch
        {
            null => "new",
            "clear" => null,
            _ => value + "!",
        };

        public string?[]? EchoStrings(string?[]? values) => values;

        public void ReplaceStrings(ref string?[]? values) => values = values switch
        {
            null => ["new"],
            [] => null,
            _ => [.. values, "new"],
        };

        public Array? EchoArray(Array? values) => values;

        public object?[]? EchoObjects(object?[]? values) => values;

        public Wrapper? EchoWrapper(Wrapper? value) => value;

        public ComBstr? EchoBstr(ComBstr? value) => value?.ToString() switch
        {
            null => null,
            "view" => value,
            "held" => ComBstr.FromPointer(_heldBstr.Pointer, ownsString: false),
            var text => ComBstr.Create(text),
        };

        public void ReplaceBstr(ref ComBstr? value)
        {
            switch (value?.ToString())
            {
                case "keep":
                    break;
                case "view":
                    value = ComBstr.FromPointer(value.Pointer, ownsString: false);
                    break;
                case "held":
                    value = ComBstr.FromPointer(_heldBstr.Pointer, ownsString: false);
                    break;
                case "clear":
                    value = null;
                    break;
                case null:
                    value = ComBstr.Create("new");
                    break;
                case var text:
                    value = ComBstr.Create(text + "!");
                    break;
            }
        }

        public void SplitBstr(ComBstr? value, out ComBstr? first, out ComBstr? rest)
        {
            ReadOnlySpan<char> text = value!.AsSpan();
            int space = text.IndexOf(' ');
            first = ComBstr.Create(text[..space]);
            rest = ComBstr.Create(text[(space + 1)..]);
        }

        public void FailBstr(ref ComBstr? value, out ComBstr? result)
        {
            value = ComBstr.Create("lost");
            result = ComBstr.Create("lost");
            throw new InvalidOperationException("failed");
        }

        public ComSafeArray<object?>? EchoSafeArray(ComSafeArray<object?>? values) => values?[0] switch
        {
            null => null,
            "view" => values,
            "held" => ComSafeArray<object?>.FromPointer(_heldArray.Pointer, ownsArray: false),
            _ => ComSafeArray<object?>.Create(values.ToArray()),
        };

        public void ReplaceSafeArray(ref ComSafeArray<object?>? values)
        {
            switch (values?[0])
            {
                case "keep":
                    break;
                case "change":
                    values[0] = "changed";
                    values[2] = "replaced";
                    break;
                case "view":
                    values = ComSafeArray<object?>.FromPointer(values.Pointer, ownsArray: false);
                    break;
                case "held":
                    values = ComSafeArray<object?>.FromPointer(_heldArray.Pointer, ownsArray: false);
                    break;
                case "clear":
                    values = null;
                    break;
                case null:
                    values = ComSafeArray<object?>.Create(["new"]);
                    break;
                case var text:
                    values = ComSafeArray<object?>.Create([text + "!", values[1]]);
                    break;
            }
        }

        public void Fail(string? text, string?[]? strings, Array? array, object?[]? objects, ComBstr? bstr, ComSafeArray<object?>? safeArray) =>
            throw new InvalidOperationException("failed");
    }
}
