using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace CsCom.Tests;

/// <summary>
/// Tests the named arguments of indexes and of the default member of <c>dynamic</c> objects, against an <c>IDispatch</c>
/// implementation that resolves the names with the type information of <c>DShapeEvents</c> of the test type library, whose
/// default member is <c>Item(index)</c> and whose <c>Cell(row, column)</c> property has two parameters.
/// </summary>
[TestClass]
public sealed unsafe partial class DynamicNamedArgumentTests
{
    private const int CellId = 14;
    private const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    private const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);

    private static readonly Guid DShapeEventsIid = new("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1013");
    private static readonly Guid ShapeEventsIid = new("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1015");

    [TestMethod]
    public void PassesNamedIndexesOfParameterizedProperties()
    {
        var recorder = new Recorder(LoadShapeEvents());
        dynamic shape = ((IDispatch)recorder).AsDynamic();

        _ = shape.Cell[column: 2, row: 1];
        AssertCall(recorder.Calls[^1], CellId, DispatchFlags.PropertyGet | DispatchFlags.Method, [], [(1, 2), (0, 1)]);

        // The value of an assignment is the first named argument (DISPID_PROPERTYPUT), followed by the named indexes.
        shape.Cell[1, column: 2] = 5;
        AssertCall(recorder.Calls[^1], CellId, DispatchFlags.PropertyPut, [1], [(DispIds.PropertyPut, 5), (1, 2)]);

        _ = shape.Cell(column: 2, row: 1);
        AssertCall(recorder.Calls[^1], CellId, DispatchFlags.Method | DispatchFlags.PropertyGet, [], [(1, 2), (0, 1)]);
    }

    [TestMethod]
    public void PassesNamedArgumentsOfTheDefaultMember()
    {
        var recorder = new Recorder(LoadShapeEvents());
        dynamic shape = ((IDispatch)recorder).AsDynamic();

        _ = shape[index: 3];
        AssertCall(recorder.Calls[^1], DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, [], [(0, 3)]);

        shape[index: 3] = "x";
        AssertCall(recorder.Calls[^1], DispIds.Value, DispatchFlags.PropertyPut, [], [(DispIds.PropertyPut, "x"), (0, 3)]);

        _ = shape(index: 3);
        AssertCall(recorder.Calls[^1], DispIds.Value, DispatchFlags.Method | DispatchFlags.PropertyGet, [], [(0, 3)]);
    }

    [TestMethod]
    public void RejectsNamedArgumentsOfTheDefaultMemberWithoutTypeInformation()
    {
        // The name of the default member, which IDispatch::GetIDsOfNames needs to resolve the names of its parameters, is unknown.
        var recorder = new Recorder(typeInfo: null);
        dynamic shape = ((IDispatch)recorder).AsDynamic();

        var exception = Assert.ThrowsExactly<NotSupportedException>(() => shape[index: 3]);
        Assert.AreEqual(0, recorder.Calls.Count);

        _ = shape[3];
        AssertCall(recorder.Calls[^1], DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, [3], []);
        StringAssert.Contains(exception.Message, "provides no type information");
    }

    [TestMethod]
    public void RejectsNamedArgumentsOfTheDefaultMemberMissingFromTheTypeInformation()
    {
        // _ShapeEvents has no member with DISPID_VALUE, so the object has type information, but not the name of the member.
        var recorder = new Recorder(LoadTypeInfo(ShapeEventsIid));
        dynamic shape = ((IDispatch)recorder).AsDynamic();

        var exception = Assert.ThrowsExactly<NotSupportedException>(() => shape[index: 3]);

        Assert.AreEqual(0, recorder.Calls.Count);
        StringAssert.Contains(exception.Message, "whose type information describes no default member");
    }

    [TestMethod]
    public void ReadsTheNameOfTheDefaultMemberOnce()
    {
        // Out-of-process servers would otherwise be asked for the type information on each access with named arguments.
        var recorder = new Recorder(LoadShapeEvents());
        dynamic shape = ((IDispatch)recorder).AsDynamic();

        _ = shape[index: 1];
        shape[index: 2] = "x";
        _ = shape(index: 3);

        Assert.AreEqual(3, recorder.Calls.Count);
        Assert.AreEqual(1, recorder.TypeInfoRequests);
    }

    [TestMethod]
    public void ReadsMembersAsTheTypeInformationDescribesThem()
    {
        var recorder = new Recorder(LoadShapeEvents());
        IDispatch dispatch = recorder;
        dynamic shape = dispatch.AsDynamic();

        Assert.AreEqual(DispatchMemberKinds.Method, ComDispatch.GetMemberKinds(dispatch, 16));
        Assert.AreEqual(DispatchMemberKinds.Property, ComDispatch.GetMemberKinds(dispatch, 13));
        Assert.AreEqual(DispatchMemberKinds.Property, ComDispatch.GetMemberKinds(dispatch, 1));
        Assert.AreEqual(DispatchMemberKinds.Property | DispatchMemberKinds.IndexedProperty, ComDispatch.GetMemberKinds(dispatch, CellId));
        Assert.AreEqual(DispatchMemberKinds.None, ComDispatch.GetMemberKinds(dispatch, 999));

        // A property whose getter takes arguments is not read before it is indexed: the result of a getter whose arguments
        // are optional would be indexed otherwise.
        _ = shape.Cell[1, 2];
        Assert.AreEqual(0, recorder.ReadsWithoutArguments);
        AssertCall(recorder.Calls.Single(), CellId, DispatchFlags.PropertyGet | DispatchFlags.Method, [1, 2], []);

        // A method that is read is not invoked: it is called through the result.
        var names = shape.Names;
        Assert.AreEqual(1, recorder.Calls.Count);
        names();
        AssertCall(recorder.Calls[^1], 16, DispatchFlags.Method | DispatchFlags.PropertyGet, [], []);

        // A property is read.
        _ = shape.Title;
        AssertCall(recorder.Calls[^1], 13, DispatchFlags.PropertyGet | DispatchFlags.Method, [], []);
        Assert.AreEqual(3, recorder.Calls.Count);
    }

    [TestMethod]
    public void ReadsAPropertyAgainAfterTheObjectDidNotHaveIt()
    {
        // An object can report DISP_E_MEMBERNOTFOUND for a property that it cannot read at the moment.
        var recorder = new Recorder(LoadShapeEvents()) { Failure = static (dispId, _) => dispId == 13 ? unchecked((int)0x80020003) : 0 };
        dynamic shape = ((IDispatch)recorder).AsDynamic();

        _ = shape.Title;
        Assert.AreEqual(0, recorder.Calls.Count);

        recorder.Failure = null;
        _ = shape.Title;
        AssertCall(recorder.Calls.Single(), 13, DispatchFlags.PropertyGet | DispatchFlags.Method, [], []);
    }

    private static void AssertCall(Call call, int dispId, DispatchFlags flags, object?[] positional, (int DispId, object? Value)[] named)
    {
        Assert.AreEqual((dispId, flags), (call.DispId, call.Flags));
        CollectionAssert.AreEqual(positional, call.Positional);
        CollectionAssert.AreEqual(named, call.Named);
    }

    private static ComTypes.ITypeInfo LoadShapeEvents() => LoadTypeInfo(DShapeEventsIid);

    private static ComTypes.ITypeInfo LoadTypeInfo(Guid iid)
    {
        Marshal.ThrowExceptionForHR(LoadTypeLibEx(TestEnvironment.TestTypeLibrary, 0, out ComTypes.ITypeLib typeLibrary));
        typeLibrary.GetTypeInfoOfGuid(ref iid, out ComTypes.ITypeInfo typeInfo);
        return typeInfo;
    }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadTypeLibEx(string file, int registerKind, out ComTypes.ITypeLib typeLibrary);

    /// <summary>A call of <c>IDispatch::Invoke</c>: the positional arguments in order, and the named ones as passed.</summary>
    private sealed record Call(int DispId, DispatchFlags Flags, object?[] Positional, (int DispId, object? Value)[] Named);

    /// <summary>
    /// Resolves names with the type information, like <c>DispGetIDsOfNames</c>, and records the calls of <c>IDispatch::Invoke</c>.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class Recorder(ComTypes.ITypeInfo? typeInfo) : IDispatch
    {
        public List<Call> Calls { get; } = [];

        public int TypeInfoRequests { get; private set; }

        /// <summary>Gets the number of times that Cell was read without arguments.</summary>
        public int ReadsWithoutArguments { get; private set; }

        /// <summary>Gets or sets the <c>HRESULT</c> of the invocations of a member that fail, which are not recorded.</summary>
        public Func<int, DispatchFlags, int>? Failure { get; set; }

        public int GetTypeInfoCount(out uint pctinfo)
        {
            pctinfo = typeInfo is null ? 0u : 1u;
            return 0;
        }

        public int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
        {
            TypeInfoRequests++;
            ppTInfo = typeInfo is null ? 0 : Marshal.GetComInterfaceForObject<ComTypes.ITypeInfo, ComTypes.ITypeInfo>(typeInfo);
            return typeInfo is null ? unchecked((int)0x8002000B) : 0;
        }

        public int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId)
        {
            var names = new string[cNames];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = new string(rgszNames[i]);
            }

            var ids = new int[cNames];
            int hr = 0;
            try
            {
                if (typeInfo is null)
                {
                    throw new COMException(null, DISP_E_UNKNOWNNAME);
                }

                typeInfo.GetIDsOfNames(names, names.Length, ids);
            }
            catch (COMException e)
            {
                Array.Fill(ids, DispIds.Unknown);
                hr = e.HResult;
            }

            ids.CopyTo(new Span<int>(rgDispId, ids.Length));
            return hr;
        }

        public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr)
        {
            int count = (int)pDispParams->cArgs;
            int named = (int)pDispParams->cNamedArgs;

            // Cell is read without arguments first, and then indexed by dynamic when that fails, unless the type information
            // tells that its getter takes arguments.
            if (dispIdMember == CellId && count == 0)
            {
                ReadsWithoutArguments++;
                return DISP_E_BADPARAMCOUNT;
            }

            if (Failure?.Invoke(dispIdMember, (DispatchFlags)wFlags) is { } failure and < 0)
            {
                return failure;
            }

            // rgvarg holds the named arguments first, then the positional ones in reverse order.
            var positional = new object?[count - named];
            for (int i = 0; i < positional.Length; i++)
            {
                positional[i] = VariantMarshaller.ConvertToManaged(pDispParams->rgvarg[count - 1 - i]);
            }

            var namedArguments = new (int, object?)[named];
            for (int i = 0; i < named; i++)
            {
                namedArguments[i] = (pDispParams->rgdispidNamedArgs[i], VariantMarshaller.ConvertToManaged(pDispParams->rgvarg[i]));
            }

            Calls.Add(new Call(dispIdMember, (DispatchFlags)wFlags, positional, namedArguments));
            if (pVarResult != null)
            {
                *pVarResult = default;
            }

            return 0;
        }
    }
}
