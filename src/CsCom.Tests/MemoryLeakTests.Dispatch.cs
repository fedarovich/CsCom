using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// The calls of <c>IDispatch::Invoke</c>: the arguments, the values passed by reference, the results and the errors.
/// </summary>
public sealed unsafe partial class MemoryLeakTests
{
    [TestMethod]
    public void InvocationsFreeTheirArgumentsAndResults()
    {
        object native = CreateDictionary();
        var server = new Server();
        var wrapper = new Wrapper(server);

        LeakDetector.AssertNoLeaks(
            () =>
            {
                using var bstr = ComBstr.Create("bstr");
                using var safeArray = ComSafeArray<string?>.Create(["a", null]);
                foreach (object? value in (object?[])[.. CreateValues(native, server), bstr, safeArray])
                {
                    _ = wrapper.Echo(value);
                    wrapper.Value = value;
                    wrapper.SetReference(value);
                    wrapper.SetItem(value, "index", value);
                    _ = wrapper.GetItem("index", value);
                    _ = DispatchObject.Invoke(server, Server.EchoId, DispatchFlags.Method, value, "second", value);
                }

                // An object without a propputref is assigned by value, with the same arguments.
                server.RejectsPutRef = true;
                wrapper.SetReference(native);
                wrapper.SetReference(new[] { "a", "b" });
                server.RejectsPutRef = false;
                Assert.AreEqual("value", wrapper.Value);
            },
            native,
            server);
    }

    [TestMethod]
    public void ValuesPassedByReferenceAreFreed()
    {
        // The server frees the values it replaces, and the caller frees the new ones.
        object native = CreateDictionary();
        var replacement = new Server();
        var server = new Server { Replacement = replacement };
        var wrapper = new Wrapper(server);

        LeakDetector.AssertNoLeaks(
            () =>
            {
                object?[] args = CreateArguments();
                Assert.AreEqual("replaced", wrapper.Replace(args, [true, true, true, true, true, true, true, true]));
                Assert.AreEqual("text!", args[0]);
                Assert.AreSame(replacement, args[4]);

                // Only some of the arguments are passed by reference.
                args = CreateArguments();
                DispatchObject.InvokeByRef(server, Server.ReplaceId, DispatchFlags.Method, args, [false, true, false, true, false, true, false, true]);
                Assert.AreSame(native, args[4]);

                // References of the types of the values, and a separate output.
                args = CreateArguments();
                var output = new object?[args.Length];
                var kinds = new DispatchArgumentKind[args.Length];
                kinds.AsSpan().Fill(DispatchArgumentKind.Typed);
                kinds[^1] = DispatchArgumentKind.Value;
                ComDispatch.Invoke(server, Server.ReplaceId, DispatchFlags.Method, args, output: output, byRef: kinds);
                Assert.AreEqual("new string", output[0]);
                CollectionAssert.AreEqual(new[] { "new", "array" }, (string?[])output[1]!);
                Assert.AreSame(replacement, output[4]);
            },
            native,
            server,
            replacement);

        object?[] CreateArguments() => ["text", new[] { "a", "b" }, new object?[] { "a", native }, new[,] { { "a" } }, native, server, 5, null];
    }

    [TestMethod]
    public void ReferencesOfTheParameterTypesAreFreed()
    {
        object native = CreateDictionary();
        var replacement = new Server();
        var server = new Server { Replacement = replacement };
        var wrapper = new Wrapper(server);
        VarEnum[] types =
        [
            VarEnum.VT_BSTR, VarEnum.VT_BSTR, VarEnum.VT_ARRAY | VarEnum.VT_BSTR, VarEnum.VT_ARRAY | VarEnum.VT_BSTR,
            VarEnum.VT_ARRAY | VarEnum.VT_I4, VarEnum.VT_DISPATCH, VarEnum.VT_DISPATCH, VarEnum.VT_UNKNOWN, VarEnum.VT_I4,
            VarEnum.VT_CY, VarEnum.VT_VARIANT, VarEnum.VT_EMPTY, VarEnum.VT_I4,
        ];

        LeakDetector.AssertNoLeaks(
            () =>
            {
                // The arrays are passed as references to VARIANTs, which a server like those of Visual Basic accepts.
                object?[] args = CreateArguments();
                Assert.AreEqual("replaced", wrapper.Replace(Server.ReplaceId, args, types));
                AssertReplaced(args);
                CollectionAssert.AreEqual(new object?[] { "new", 1 }, (object?[])args[2]!);

                // A server that checks the types rejects them: the call is repeated with references to the SAFEARRAYs.
                args = CreateArguments();
                Assert.AreEqual("replaced", wrapper.Replace(Server.TypedId, args, types));
                AssertReplaced(args);
                CollectionAssert.AreEqual(new[] { "new", "array" }, (string?[])args[2]!);
                CollectionAssert.AreEqual(new[] { "new", "array" }, (string?[])args[3]!);
                CollectionAssert.AreEqual(new[] { 4, 5 }, (int[])args[4]!);

                void AssertReplaced(object?[] args)
                {
                    Assert.AreEqual("new string", args[0]);
                    Assert.AreEqual("new string", args[1]);
                    Assert.AreSame(replacement, args[5]);
                    Assert.AreSame(replacement, args[6]);
                    Assert.AreSame(replacement, args[7]);
                    Assert.AreEqual(6, args[8]);
                    Assert.AreEqual(1m, args[9]);
                    Assert.AreEqual("variant!", args[10]);
                    Assert.AreEqual("by value", args[11]);
                    Assert.AreEqual("not a number!", args[12]);
                }
            },
            native,
            server,
            replacement);

        // The last string cannot be converted to the type of its parameter, so it is passed as a reference to a VARIANT.
        object?[] CreateArguments() =>
            ["text", null, new[] { "a", "b" }, null, new[] { 1, 2 }, native, null, server, 5, 1.5m, "variant", "by value", "not a number"];
    }

    [TestMethod]
    public void FailedInvocationsFreeTheirArguments()
    {
        object native = CreateDictionary();
        var server = new Server();

        LeakDetector.AssertNoLeaks(
            () =>
            {
                object?[] values = ["text", new[] { "a", "b" }, new object?[] { "a", native }, native, server];
                bool[] byRef = [true, true, true, true, true];
                VarEnum[] types = [VarEnum.VT_BSTR, VarEnum.VT_ARRAY | VarEnum.VT_BSTR, VarEnum.VT_VARIANT, VarEnum.VT_DISPATCH, VarEnum.VT_UNKNOWN];

                // The server raises an exception, whose strings the caller frees.
                var exception = Assert.ThrowsExactly<COMException>(() => DispatchObject.Invoke(server, Server.FailId, DispatchFlags.Method, values));
                Assert.AreEqual(("failed", "server", E_FAIL), (exception.Message, exception.Source, exception.ErrorCode));
                Assert.ThrowsExactly<COMException>(() => DispatchObject.InvokeByRef(server, Server.FailId, DispatchFlags.Method, [.. values], byRef));
                Assert.ThrowsExactly<COMException>(() => DispatchObject.InvokeByRef(server, Server.FailId, DispatchFlags.Method, [.. values], types));
                Assert.ThrowsExactly<COMException>(() => DispatchObject.Invoke(server, Server.FailId, DispatchFlags.PropertyPut, values));

                // The server rejects the call.
                Assert.ThrowsExactly<COMException>(() => DispatchObject.Invoke(server, Server.RejectId, DispatchFlags.Method, values));
                Assert.ThrowsExactly<COMException>(() => DispatchObject.InvokeByRef(server, Server.RejectId, DispatchFlags.Method, [.. values], byRef));
                Assert.ThrowsExactly<COMException>(() => DispatchObject.InvokeByRef(server, Server.RejectId, DispatchFlags.Method, [.. values], types));
                int hr = ComDispatch.TryInvoke(server, Server.RejectId, DispatchFlags.Method, values, default, default, default, out object? result, out int argument);
                Assert.AreEqual((DISP_E_BADPARAMCOUNT, null, -1), (hr, result, argument));

                // The last argument cannot be passed in a VARIANT: the arguments converted before it are freed.
                Assert.ThrowsExactly<NotSupportedException>(() => DispatchObject.Invoke(server, Server.EchoId, DispatchFlags.Method, [.. values, new int[1, 1, 1, 1]]));
                Assert.ThrowsExactly<NotSupportedException>(
                    () => DispatchObject.InvokeByRef(server, Server.ReplaceId, DispatchFlags.Method, [.. values, new int[1, 1, 1, 1]], [.. byRef, true]));
            },
            native,
            server);
    }

    [TestMethod]
    public void NamesAreFreed()
    {
        var server = new Server();

        LeakDetector.AssertNoLeaks(
            () =>
            {
                Span<int> ids = stackalloc int[3];
                Assert.AreEqual(0, ComDispatch.GetDispIds(server, ["Echo", "first", "second"], ids));
                CollectionAssert.AreEqual(new[] { Server.EchoId, 0, 1 }, ids.ToArray());
                Assert.AreEqual(DISP_E_UNKNOWNNAME, ComDispatch.GetDispIds(server, ["Unknown", "first"], ids));

                // Named arguments, which follow the positional one.
                Assert.AreEqual("first", ComDispatch.Invoke(server, Server.EchoId, DispatchFlags.Method, ["first", new[] { "third" }, "second"], [2, 1]));
            },
            server);
    }

    [TestMethod]
    public void EnumeratorsFreeTheirItems()
    {
        object native = CreateDictionary();
        var server = new Server();
        var wrapper = new Wrapper(server);
        server.Items = CreateValues(native, server);

        LeakDetector.AssertNoLeaks(
            () =>
            {
                int count = 0;
                using var enumerator = wrapper.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    count++;
                }

                Assert.AreEqual(server.Items.Length, count);
                enumerator.Reset();
                Assert.IsTrue(enumerator.MoveNext());
            },
            native,
            server);
    }

    [TestMethod]
    public void TheMembersOfARealObjectAreInvokedWithoutLeaks()
    {
        // The Dictionary of the Scripting Runtime: a native object that checks its arguments, keeps and returns copies of the
        // values, raises exceptions and has type information.
        var dictionary = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        var other = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);

        LeakDetector.AssertNoLeaks(
            () =>
            {
                int add = GetDispId("Add");
                DispatchObject.Invoke(dictionary, add, DispatchFlags.Method, "text", "value");
                DispatchObject.Invoke(dictionary, add, DispatchFlags.Method, "array", new[] { "a", "b" });
                DispatchObject.Invoke(dictionary, add, DispatchFlags.Method, "object", other);
                DispatchObject.Invoke(dictionary, DispIds.Value, DispatchFlags.PropertyPut, "text", new object?[] { "replaced", other });
                DispatchObject.Invoke(dictionary, DispIds.Value, DispatchFlags.PropertyPutRef, "reference", other);

                Assert.AreEqual(4, DispatchObject.Invoke(dictionary, GetDispId("Count"), DispatchFlags.PropertyGet));
                CollectionAssert.AreEqual(new[] { "a", "b" }, (string[])DispatchObject.Invoke(dictionary, DispIds.Value, DispatchFlags.PropertyGet, "array")!);
                Assert.AreSame(other, DispatchObject.Invoke(dictionary, DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, "object"));
                CollectionAssert.AreEqual(new object?[] { "text", "array", "object", "reference" }, (object?[])DispatchObject.Invoke(dictionary, GetDispId("Keys"), DispatchFlags.Method)!);
                Assert.HasCount(4, (object?[])DispatchObject.Invoke(dictionary, GetDispId("Items"), DispatchFlags.Method)!);

                var keys = new List<object?>();
                using (var enumerator = ComEnumerator.FromObject(DispatchObject.Invoke(dictionary, DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet)))
                {
                    while (enumerator.MoveNext())
                    {
                        keys.Add(enumerator.Current);
                    }
                }

                Assert.HasCount(4, keys);

                // The key exists: the exception has a description, which is freed.
                var exception = Assert.ThrowsExactly<COMException>(() => DispatchObject.Invoke(dictionary, add, DispatchFlags.Method, "text", new[] { "again" }));
                Assert.IsFalse(string.IsNullOrEmpty(exception.Message));
                Assert.ThrowsExactly<COMException>(() => DispatchObject.Invoke(dictionary, add, DispatchFlags.Method, "too", "many", "arguments"));

                DispatchObject.Invoke(dictionary, GetDispId("RemoveAll"), DispatchFlags.Method);

                Assert.AreEqual("IDictionary", ComDispatch.GetTypeName(dictionary));
                Assert.AreEqual("Item", ComDispatch.GetMemberName(dictionary, DispIds.Value));
                Assert.AreEqual(DispatchMemberKinds.Method, ComDispatch.GetMemberKinds(dictionary, add));
                CollectionAssert.Contains(ComDispatch.GetPropertyNames(dictionary).ToList(), "Count");
                Assert.IsEmpty(ComDispatch.GetEventInterfaces(dictionary));
            },
            dictionary,
            other);

        int GetDispId(string name)
        {
            Span<int> ids = stackalloc int[1];
            Marshal.ThrowExceptionForHR(ComDispatch.GetDispIds(dictionary, [name], ids));
            return ids[0];
        }
    }

    /// <summary>
    /// An automation server that follows the COM ownership rules like a native one: it copies the values it returns, frees
    /// the values passed by reference that it replaces and gives the caller the ownership of the new ones.
    /// </summary>
    [GeneratedComClass]
    internal sealed partial class Server : IDispatch
    {
        /// <summary>Returns a copy of its first argument.</summary>
        public const int EchoId = 1;

        /// <summary>Replaces the values of the arguments passed by reference and returns a string.</summary>
        public const int ReplaceId = 2;

        /// <summary>Raises an exception with a source, a description and a help file.</summary>
        public const int FailId = 3;

        /// <summary>Like <see cref="ReplaceId"/>, but rejects the arrays passed as references to <c>VARIANT</c>s.</summary>
        public const int TypedId = 4;

        /// <summary>Fails with <c>DISP_E_BADPARAMCOUNT</c>.</summary>
        public const int RejectId = 5;

        /// <summary>A property with any number of indices, whose value is a string.</summary>
        public const int PropertyId = 6;

        private static readonly Dictionary<string, int> DispIdsByName = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Echo"] = EchoId,
            ["Replace"] = ReplaceId,
            ["Fail"] = FailId,
            ["Property"] = PropertyId,
        };

        /// <summary>Gets or sets the object that replaces the objects passed by reference.</summary>
        public object? Replacement { get; set; }

        /// <summary>Gets or sets a value indicating whether the property cannot be set by reference.</summary>
        public bool RejectsPutRef { get; set; }

        /// <summary>Gets or sets the items of the enumerators.</summary>
        public object?[] Items { get; set; } = [];

        public int GetTypeInfoCount(out uint pctinfo)
        {
            pctinfo = 0;
            return 0;
        }

        public int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
        {
            ppTInfo = 0;
            return E_NOTIMPL;
        }

        // The identifiers of the parameters are their positions.
        public int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId)
        {
            bool known = DispIdsByName.TryGetValue(new string(rgszNames[0]), out int dispId);
            rgDispId[0] = known ? dispId : -1;
            for (uint i = 1; i < cNames; i++)
            {
                rgDispId[i] = known ? (int)i - 1 : -1;
            }

            return known ? 0 : DISP_E_UNKNOWNNAME;
        }

        public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr)
        {
            var flags = (DispatchFlags)wFlags;
            bool isSetter = (flags & (DispatchFlags.PropertyPut | DispatchFlags.PropertyPutRef)) != 0;
            switch (dispIdMember)
            {
                case EchoId:
                    return pVarResult is null ? 0 : VariantCopyInd(Initialize(pVarResult), Find(pDispParams, 0));

                case ReplaceId or TypedId:
                    for (uint i = 0; i < pDispParams->cArgs && dispIdMember == TypedId; i++)
                    {
                        ComVariant* argument = pDispParams->rgvarg + i;
                        if (argument->VarType == (VarEnum.VT_BYREF | VarEnum.VT_VARIANT) && (((ComVariant*)argument->GetRawDataRef<nint>())->VarType & VarEnum.VT_ARRAY) != 0)
                        {
                            *puArgErr = i;
                            return DISP_E_TYPEMISMATCH;
                        }
                    }

                    for (uint i = 0; i < pDispParams->cArgs; i++)
                    {
                        Replace(pDispParams->rgvarg + i);
                    }

                    SetResult(pVarResult, "replaced");
                    return 0;

                case FailId:
                    *pExcepInfo = default;
                    pExcepInfo->bstrSource = Marshal.StringToBSTR("server");
                    pExcepInfo->bstrDescription = Marshal.StringToBSTR("failed");
                    pExcepInfo->bstrHelpFile = Marshal.StringToBSTR("server.chm");
                    pExcepInfo->scode = E_FAIL;
                    return DISP_E_EXCEPTION;

                case RejectId:
                    return DISP_E_BADPARAMCOUNT;

                case PropertyId when isSetter:
                    return (flags & DispatchFlags.PropertyPutRef) != 0 && RejectsPutRef ? DISP_E_MEMBERNOTFOUND : 0;

                case PropertyId:
                    SetResult(pVarResult, "value");
                    return 0;

                case DispIds.NewEnum:
                    *Initialize(pVarResult) = ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, (nint)ComInterfaceMarshaller<IEnumVARIANT>.ConvertToUnmanaged(new Enumerator(Items)));
                    return 0;

                default:
                    return DISP_E_MEMBERNOTFOUND;
            }
        }

        // The result is an [out] parameter: the variable of the caller is not read.
        private static ComVariant* Initialize(ComVariant* result)
        {
            *result = default;
            return result;
        }

        private static void SetResult(ComVariant* result, string value)
        {
            if (result is not null)
            {
                *result = ComVariant.CreateRaw(VarEnum.VT_BSTR, Marshal.StringToBSTR(value));
            }
        }

        // The argument at a position of the declaration: the named ones come first, the positional ones follow in reverse order.
        private static ComVariant* Find(DispParams* parameters, int position)
        {
            for (uint i = 0; i < parameters->cNamedArgs; i++)
            {
                if (parameters->rgdispidNamedArgs[i] == position)
                {
                    return parameters->rgvarg + i;
                }
            }

            return parameters->rgvarg + (parameters->cArgs - 1 - (uint)position);
        }

        // Frees the value of an argument passed by reference and stores a new one, which the caller owns.
        private void Replace(ComVariant* argument)
        {
            if ((argument->VarType & VarEnum.VT_BYREF) == 0)
            {
                return;
            }

            VarEnum type = argument->VarType & ~VarEnum.VT_BYREF;
            nint target = argument->GetRawDataRef<nint>();
            if (type == VarEnum.VT_VARIANT)
            {
                var variant = (ComVariant*)target;
                object? value = VariantMarshaller.ConvertToManaged(*variant);
                variant->Dispose();
                *variant = VariantMarshaller.ConvertToUnmanaged(value switch
                {
                    null => "out",
                    string text => text + "!",
                    int number => number + 1,
                    Array => new object?[] { "new", 1 },
                    _ => Replacement,
                });
            }
            else if ((type & VarEnum.VT_ARRAY) != 0)
            {
                SafeArrayMarshaller.Free(*(nint*)target);
                *(nint*)target = (type & ~VarEnum.VT_ARRAY) switch
                {
                    VarEnum.VT_BSTR => SafeArrayMarshaller<string>.ConvertToUnmanaged(["new", "array"]),
                    VarEnum.VT_VARIANT => SafeArrayMarshaller<object?>.ConvertToUnmanaged(["new", 1]),
                    VarEnum.VT_I4 => SafeArrayMarshaller<int>.ConvertToUnmanaged([4, 5]),
                    _ => throw new NotSupportedException(),
                };
            }
            else
            {
                switch (type)
                {
                    case VarEnum.VT_BSTR:
                        Marshal.FreeBSTR(*(nint*)target);
                        *(nint*)target = Marshal.StringToBSTR("new string");
                        break;
                    case VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN:
                        if (*(nint*)target != 0)
                        {
                            Marshal.Release(*(nint*)target);
                        }

                        *(nint*)target = type == VarEnum.VT_DISPATCH
                            ? (nint)ComInterfaceMarshaller<IDispatch>.ConvertToUnmanaged((IDispatch?)Replacement)
                            : (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(Replacement);
                        break;
                    case VarEnum.VT_I4:
                        *(int*)target += 1;
                        break;
                    case VarEnum.VT_CY:
                        *(long*)target = 10000;
                        break;
                    default:
                        throw new NotSupportedException();
                }
            }
        }

        [DllImport("oleaut32.dll")]
        private static extern int VariantCopyInd(ComVariant* destination, ComVariant* source);
    }

    /// <summary>An enumerator of values, which gives the caller copies of them.</summary>
    [GeneratedComClass]
    internal sealed partial class Enumerator(object?[] items) : IEnumVARIANT
    {
        private int _index;

        public int Next(uint celt, ComVariant* rgVar, uint* pCeltFetched)
        {
            uint fetched = 0;
            while (fetched < celt && _index < items.Length)
            {
                rgVar[fetched++] = VariantMarshaller.ConvertToUnmanaged(items[_index++]);
            }

            if (pCeltFetched is not null)
            {
                *pCeltFetched = fetched;
            }

            return fetched == celt ? 0 : 1;
        }

        public int Skip(uint celt)
        {
            _index += (int)celt;
            return 0;
        }

        public int Reset()
        {
            _index = 0;
            return 0;
        }

        public int Clone(out IEnumVARIANT? ppEnum)
        {
            ppEnum = null;
            return E_NOTIMPL;
        }
    }

    /// <summary>A late-bound wrapper like the generated ones, which calls the members of <see cref="Server"/>.</summary>
    internal sealed class Wrapper(IDispatch dispatch) : DispatchObject(dispatch), IDispatchWrapper<Wrapper>
    {
        public object? Value
        {
            get => InvokeGet(Server.PropertyId);
            set => InvokePut(Server.PropertyId, value);
        }

        public static Wrapper Create(IDispatch dispatch) => new(dispatch);

        public object? Echo(object? value) => InvokeMethod(Server.EchoId, value);

        public object? GetItem(params ReadOnlySpan<object?> indices) => InvokeGet(Server.PropertyId, indices);

        public void SetItem(object? value, params ReadOnlySpan<object?> indices) => InvokePut(Server.PropertyId, value, indices);

        public void SetReference(object? value) => InvokePutRef(Server.PropertyId, value);

        public object? Replace(Span<object?> args, ReadOnlySpan<bool> byRef) => InvokeMethodByRef(Server.ReplaceId, args, byRef);

        public object? Replace(int dispId, Span<object?> args, ReadOnlySpan<VarEnum> types) => InvokeMethodByRef(dispId, args, types);

        public ComEnumerator GetEnumerator() => GetEnumeratorCore();
    }
}
