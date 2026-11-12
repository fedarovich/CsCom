using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.CodeGeneration;
using CsCom.Model;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// Runs the late-bound wrappers generated for dispinterfaces, compiled in memory, against an <c>IDispatch</c> implementation
/// that records the calls.
/// </summary>
[TestClass]
public sealed unsafe partial class DispatchWrapperTests
{
    private const int CellId = 5;
    private const int SourceId = 6;
    private const int SwapId = 7;

    [TestMethod]
    public void InvokesAccessorsWithByRefParametersAsProperties()
    {
        // Like the properties of VB6 classes, whose parameters are ByRef by default, the accessors are methods of the wrapper.
        // Objects reject DISPATCH_METHOD for them, and setters must pass their value as the DISPID_PROPERTYPUT named argument.
        var (wrapper, recorder) = CreateWrapper();

        recorder.Result = "cell";
        object?[] args = ["A", 2];
        Assert.AreEqual("cell", wrapper.GetType().GetMethod("get_Cell")!.Invoke(wrapper, args));
        AssertCall(recorder.Calls[^1], CellId, DispatchFlags.PropertyGet | DispatchFlags.Method, [], "A", 2);
        Assert.AreEqual("A!", args[0]);

        args = ["B", 3, "value"];
        wrapper.GetType().GetMethod("set_Cell")!.Invoke(wrapper, args);
        AssertCall(recorder.Calls[^1], CellId, DispatchFlags.PropertyPut, [DispIds.PropertyPut], "B", 3, "value");
        Assert.AreEqual("B!", args[0]);

        args = ["C", "source"];
        wrapper.GetType().GetMethod("putref_Source")!.Invoke(wrapper, args);
        AssertCall(recorder.Calls[^1], SourceId, DispatchFlags.PropertyPutRef, [DispIds.PropertyPut], "C", "source");
        Assert.AreEqual("C!", args[0]);

        // Methods are still invoked as methods.
        args = ["D"];
        wrapper.GetType().GetMethod("Swap")!.Invoke(wrapper, args);
        AssertCall(recorder.Calls[^1], SwapId, DispatchFlags.Method, [], "D");
        Assert.AreEqual("D!", args[0]);
    }

    private static void AssertCall(Call call, int dispId, DispatchFlags flags, int[] namedDispIds, params object?[] arguments)
    {
        Assert.AreEqual((dispId, flags), (call.DispId, call.Flags));
        CollectionAssert.AreEqual(namedDispIds, call.NamedDispIds);
        CollectionAssert.AreEqual(arguments, call.Arguments);
    }

    // The wrapper of a dispinterface whose property accessors have [in, out] VARIANT* parameters, next to a method that has one.
    private static (object Wrapper, Recorder Recorder) CreateWrapper()
    {
        var variant = new PrimitiveTypeReference(VarEnum.VT_VARIANT);
        var byRef = new PointerTypeReference(variant);
        var @void = new PrimitiveTypeReference(VarEnum.VT_VOID);
        var cells = new InterfaceDefinition
        {
            Name = "Cells",
            OriginalName = "Cells",
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };
        cells.Methods.Add(Method("Cell", CellId, InvokeKind.PropertyGet, variant, Parameter("Row", byRef, isByRef: true), Parameter("Column", variant)));
        cells.Methods.Add(Method("Cell", CellId, InvokeKind.PropertyPut, @void, Parameter("Row", byRef, isByRef: true), Parameter("Column", variant), Parameter("value", variant)));
        cells.Methods.Add(Method("Source", SourceId, InvokeKind.PropertyPutRef, @void, Parameter("Key", byRef, isByRef: true), Parameter("value", variant)));
        cells.Methods.Add(Method("Swap", SwapId, InvokeKind.Method, @void, Parameter("Value", byRef, isByRef: true)));
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.Add(cells);

        string source = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test" })
            .Single(static d => d.RelativePath == "Cells.cs")
            .GetText();
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(DispatchObject).Assembly.Location));
        var assembly = Assembly.Load(TestCompiler.Compile("Cells", source, reference: runtime).ToArray());

        var recorder = new Recorder();
        object wrapper = assembly.GetType("Test.Cells", throwOnError: true)!.GetMethod("Create")!.Invoke(null, [recorder])!;
        return (wrapper, recorder);

        static MethodDefinition Method(string name, int dispId, InvokeKind kind, TypeReference returnType, params ParameterDefinition[] parameters)
        {
            var method = new MethodDefinition { Name = name, OriginalName = name, MemberId = dispId, InvokeKind = kind, ReturnType = returnType };
            method.Parameters.AddRange(parameters);
            return method;
        }

        static ParameterDefinition Parameter(string name, TypeReference type, bool isByRef = false) =>
            new() { Name = name, Type = type, Flags = ParameterFlags.In | (isByRef ? ParameterFlags.Out : ParameterFlags.None) };
    }

    /// <summary>A call of <c>IDispatch::Invoke</c>, with its arguments in the declaration order.</summary>
    private sealed record Call(int DispId, DispatchFlags Flags, int[] NamedDispIds, object?[] Arguments);

    /// <summary>
    /// Records the calls of <c>IDispatch::Invoke</c>, appends "!" to the strings passed by reference and returns <see cref="Result"/>.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class Recorder : IDispatch
    {
        public List<Call> Calls { get; } = [];

        public object? Result { get; set; }

        public int GetTypeInfoCount(out uint pctinfo)
        {
            pctinfo = 0;
            return 0;
        }

        public int GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo)
        {
            ppTInfo = 0;
            return unchecked((int)0x80004001);
        }

        public int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId) => unchecked((int)0x80004001);

        public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr)
        {
            // The named arguments come first, then the positional ones in reverse order; the named ones follow in the declaration order.
            int count = (int)pDispParams->cArgs;
            int named = (int)pDispParams->cNamedArgs;
            var namedDispIds = new int[named];
            var arguments = new object?[count];
            for (int i = 0; i < count; i++)
            {
                ref ComVariant argument = ref pDispParams->rgvarg[i];
                int index = i < named ? count - named + i : count - named - 1 - (i - named);
                if (argument.VarType == (VarEnum.VT_BYREF | VarEnum.VT_VARIANT))
                {
                    var target = (ComVariant*)argument.GetRawDataRef<nint>();
                    arguments[index] = VariantMarshaller.ConvertToManaged(*target);
                    target->Dispose();
                    *target = VariantMarshaller.ConvertToUnmanaged(arguments[index] + "!");
                }
                else
                {
                    arguments[index] = VariantMarshaller.ConvertToManaged(argument);
                }
            }

            for (int i = 0; i < named; i++)
            {
                namedDispIds[i] = pDispParams->rgdispidNamedArgs[i];
            }

            Calls.Add(new Call(dispIdMember, (DispatchFlags)wFlags, namedDispIds, arguments));
            if (pVarResult != null)
            {
                *pVarResult = VariantMarshaller.ConvertToUnmanaged(Result);
            }

            return 0;
        }
    }
}
