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

        Assert.ThrowsExactly<NotSupportedException>(() => shape[index: 3]);
        Assert.AreEqual(0, recorder.Calls.Count);

        _ = shape[3];
        AssertCall(recorder.Calls[^1], DispIds.Value, DispatchFlags.PropertyGet | DispatchFlags.Method, [3], []);
    }

    private static void AssertCall(Call call, int dispId, DispatchFlags flags, object?[] positional, (int DispId, object? Value)[] named)
    {
        Assert.AreEqual((dispId, flags), (call.DispId, call.Flags));
        CollectionAssert.AreEqual(positional, call.Positional);
        CollectionAssert.AreEqual(named, call.Named);
    }

    private static ComTypes.ITypeInfo LoadShapeEvents()
    {
        Marshal.ThrowExceptionForHR(LoadTypeLibEx(TestEnvironment.TestTypeLibrary, 0, out ComTypes.ITypeLib typeLibrary));
        Guid iid = DShapeEventsIid;
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

        public int GetTypeInfoCount(out uint pctinfo)
        {
            pctinfo = typeInfo is null ? 0u : 1u;
            return 0;
        }

        public int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
        {
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

            // Cell is read without arguments first, and then indexed by dynamic when that fails.
            if (dispIdMember == CellId && count == 0)
            {
                return DISP_E_BADPARAMCOUNT;
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
