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

    private static readonly PrimitiveTypeReference Variant = new(VarEnum.VT_VARIANT);
    private static readonly PrimitiveTypeReference Void = new(VarEnum.VT_VOID);

    [TestMethod]
    public void InvokesAccessorsWithByRefParametersAsProperties()
    {
        // Like the properties of VB6 classes, whose parameters are ByRef by default, the accessors are methods of the wrapper.
        // Objects reject DISPATCH_METHOD for them, and setters must pass their value as the DISPID_PROPERTYPUT named argument.
        var byRef = new PointerTypeReference(Variant);
        var (wrapper, recorder) = CreateWrapper(
            "Cells",
            Method("Cell", CellId, InvokeKind.PropertyGet, Variant, Parameter("Row", byRef, isByRef: true), Parameter("Column", Variant)),
            Method("Cell", CellId, InvokeKind.PropertyPut, Void, Parameter("Row", byRef, isByRef: true), Parameter("Column", Variant), Parameter("value", Variant)),
            Method("Source", SourceId, InvokeKind.PropertyPutRef, Void, Parameter("Key", byRef, isByRef: true), Parameter("value", Variant)),
            Method("Swap", SwapId, InvokeKind.Method, Void, Parameter("Value", byRef, isByRef: true)));

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

    [TestMethod]
    public void ConvertsTheElementsOfOneDimensionalArrays()
    {
        // VB and script servers return SAFEARRAYs of VARIANTs, whatever the element type that the type library declares.
        var strings = new SafeArrayTypeReference(new PrimitiveTypeReference(VarEnum.VT_BSTR));
        var integers = new SafeArrayTypeReference(new PrimitiveTypeReference(VarEnum.VT_I4));
        var (wrapper, recorder) = CreateWrapper(
            "Lists",
            Method("Names", 1, InvokeKind.PropertyGet, strings),
            Method("Numbers", 2, InvokeKind.Method, integers),
            Method("Fill", 3, InvokeKind.Method, Void, Parameter("Values", new PointerTypeReference(integers), isByRef: true)));

        recorder.Result = new object?[] { "a", null, "c" };
        CollectionAssert.AreEqual(new[] { "a", null, "c" }, (string?[])wrapper.GetType().GetProperty("Names")!.GetValue(wrapper)!);

        recorder.Result = new object?[] { 1, (short)2, 3.0 };
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, (int[])wrapper.GetType().GetMethod("Numbers")!.Invoke(wrapper, [])!);

        // The new value of a by-reference argument is converted too: the server stores an array of VARIANTs in the int[] variable.
        recorder.ByRefValue = static _ => new object?[] { 4, "5" };
        object?[] args = [new[] { 1 }];
        wrapper.GetType().GetMethod("Fill")!.Invoke(wrapper, args);
        CollectionAssert.AreEqual(new[] { 4, 5 }, (int[])args[0]!);
    }

    [TestMethod]
    public void ConvertsArraysOfEnums()
    {
        // A SAFEARRAY(Color), here also through an alias of the enum, is a SAFEARRAY of VT_I4. It is converted to a Color[] rather
        // than returned as the int[], which the CLR also considers to be a Color[], and Color[] arguments are passed as VT_I4.
        var color = new EnumDefinition { Name = "Color", OriginalName = "Color" };
        color.Members.Add(new EnumMember { Name = "Red", Value = 1 });
        color.Members.Add(new EnumMember { Name = "Green", Value = 2 });
        var shade = new AliasDefinition { Name = "Shade", OriginalName = "Shade", Target = new UserDefinedTypeReference(color) };
        var colors = new SafeArrayTypeReference(new UserDefinedTypeReference(color));
        var (wrapper, recorder) = CreateWrapper(
            "Palette",
            [color, shade],
            Method("Mix", 1, InvokeKind.Method, colors, Parameter("Values", new SafeArrayTypeReference(new UserDefinedTypeReference(shade)))),
            Method("Fill", 2, InvokeKind.Method, Void, Parameter("Values", new PointerTypeReference(colors), isByRef: true)));

        var colorType = wrapper.GetType().Assembly.GetType("Test.Color", throwOnError: true)!;
        var arrayType = colorType.MakeArrayType();
        var mix = wrapper.GetType().GetMethod("Mix")!;
        Assert.AreEqual(arrayType, mix.ReturnType);
        Assert.AreEqual(arrayType, mix.GetParameters().Single().ParameterType);

        recorder.Result = new[] { 2, 1 };
        var result = (Array)mix.Invoke(wrapper, [Colors(1, 2)])!;
        Assert.AreEqual(typeof(int[]), recorder.Calls[^1].Arguments[0]!.GetType());
        CollectionAssert.AreEqual(new[] { 1, 2 }, (int[])recorder.Calls[^1].Arguments[0]!);
        Assert.AreEqual(arrayType, result.GetType());
        CollectionAssert.AreEqual(Colors(2, 1), result);

        // An array of VARIANTs, as VB and script servers return.
        recorder.Result = new object?[] { 2, (short)1 };
        CollectionAssert.AreEqual(Colors(2, 1), (Array)mix.Invoke(wrapper, [Colors()])!);

        recorder.ByRefValue = static _ => new[] { 2 };
        object?[] args = [Colors(1)];
        wrapper.GetType().GetMethod("Fill")!.Invoke(wrapper, args);
        Assert.AreEqual(arrayType, args[0]!.GetType());
        CollectionAssert.AreEqual(Colors(2), (Array)args[0]!);

        Array Colors(params int[] values)
        {
            var array = Array.CreateInstance(colorType, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                array.SetValue(Enum.ToObject(colorType, values[i]), i);
            }

            return array;
        }
    }

    private static void AssertCall(Call call, int dispId, DispatchFlags flags, int[] namedDispIds, params object?[] arguments)
    {
        Assert.AreEqual((dispId, flags), (call.DispId, call.Flags));
        CollectionAssert.AreEqual(namedDispIds, call.NamedDispIds);
        CollectionAssert.AreEqual(arguments, call.Arguments);
    }

    private static (object Wrapper, Recorder Recorder) CreateWrapper(string name, params MethodDefinition[] methods) => CreateWrapper(name, [], methods);

    // Generates the wrapper of a dispinterface next to the types that it uses, compiles it and creates an instance of it for a recorder.
    private static (object Wrapper, Recorder Recorder) CreateWrapper(string name, IEnumerable<TypeDefinition> types, params MethodDefinition[] methods)
    {
        var definition = new InterfaceDefinition
        {
            Name = name,
            OriginalName = name,
            Guid = Guid.NewGuid(),
            InterfaceKind = InterfaceKind.Dispatch,
            BaseInterface = new PrimitiveTypeReference(VarEnum.VT_DISPATCH),
            BaseVtableSlots = 7,
        };
        definition.Methods.AddRange(methods);
        var library = new TypeLibrary { Name = "Synthetic" };
        library.Types.AddRange(types);
        library.Types.Add(definition);

        // A single file, which the in-memory compilation takes.
        string source = new InteropPipeline().GenerateCode(library, new CodeGenerationOptions { Namespace = "Test", SingleFile = true, SingleFileName = "Test.cs" })
            .Single()
            .GetText();
        using var runtime = new MemoryStream(File.ReadAllBytes(typeof(DispatchObject).Assembly.Location));
        var assembly = Assembly.Load(TestCompiler.Compile(name, source, reference: runtime).ToArray());

        var recorder = new Recorder();
        object wrapper = assembly.GetType("Test." + name, throwOnError: true)!.GetMethod("Create")!.Invoke(null, [recorder])!;
        return (wrapper, recorder);
    }

    private static MethodDefinition Method(string name, int dispId, InvokeKind kind, TypeReference returnType, params ParameterDefinition[] parameters)
    {
        var method = new MethodDefinition { Name = name, OriginalName = name, MemberId = dispId, InvokeKind = kind, ReturnType = returnType };
        method.Parameters.AddRange(parameters);
        return method;
    }

    private static ParameterDefinition Parameter(string name, TypeReference type, bool isByRef = false) =>
        new() { Name = name, Type = type, Flags = ParameterFlags.In | (isByRef ? ParameterFlags.Out : ParameterFlags.None) };

    /// <summary>A call of <c>IDispatch::Invoke</c>, with its arguments in the declaration order.</summary>
    private sealed record Call(int DispId, DispatchFlags Flags, int[] NamedDispIds, object?[] Arguments);

    /// <summary>
    /// Records the calls of <c>IDispatch::Invoke</c>, replaces the values passed by reference with <see cref="ByRefValue"/>
    /// and returns <see cref="Result"/>.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class Recorder : IDispatch
    {
        public List<Call> Calls { get; } = [];

        public object? Result { get; set; }

        /// <summary>Gets the new value of an argument passed by reference; by default, its string followed by "!".</summary>
        public Func<object?, object?> ByRefValue { get; set; } = static value => value + "!";

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
                    *target = VariantMarshaller.ConvertToUnmanaged(ByRefValue(arguments[index]));
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
