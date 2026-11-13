using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace CsCom.Tests;

/// <summary>
/// Tests <c>ref</c> and <c>out</c> arguments of <c>dynamic</c> calls against the standard implementation of <c>IDispatch</c>
/// (<c>CreateStdDispatch</c>), whose <c>ITypeInfo::Invoke</c> checks the types of the arguments like most COM servers do.
/// </summary>
[TestClass]
public sealed partial class DynamicByRefTests
{
    [TestMethod]
    public void PassesRefArgumentsAsTypedReferences()
    {
        dynamic calculator = CreateCalculator();
        int a = 1;
        int b = 2;

        calculator.Swap(ref a, ref b);

        Assert.AreEqual(2, a);
        Assert.AreEqual(1, b);
        Assert.AreEqual(5, (int)calculator.Add(2, 3));
    }

    [TestMethod]
    public void ReceivesOutArguments()
    {
        dynamic calculator = CreateCalculator();

        calculator.Divide(7.0, 2.0, out double quotient, out double remainder);

        Assert.AreEqual(3.0, quotient);
        Assert.AreEqual(1.0, remainder);
    }

    [TestMethod]
    public void ConvertsTheNewValuesToTheTypesOfTheVariables()
    {
        dynamic calculator = CreateCalculator();
        DayOfWeek a = DayOfWeek.Monday;
        int b = 5;

        calculator.Swap(b: ref b, a: ref a);

        Assert.AreEqual(DayOfWeek.Friday, a);
        Assert.AreEqual(1, b);
    }

    [TestMethod]
    public void PassesVariablesAsReferencesToTheirOwnTypes()
    {
        dynamic calculator = CreateCalculator();
        long a = 1;
        int b = 2;

        // A long variable is a VT_I8 reference, but the parameter is a long* (VT_I4) in COM.
        var exception = Assert.ThrowsExactly<COMException>(() => calculator.Swap(ref a, ref b));

        Assert.AreEqual("Type mismatch in argument 1 of 'Swap'.", exception.Message);
    }

    [TestMethod]
    public void PassesObjectVariablesAsVariantReferences()
    {
        dynamic calculator = CreateCalculator();
        object a = 1;
        object b = 2;

        // ITypeInfo::Invoke does not accept VARIANT references for typed by-reference parameters.
        var exception = Assert.ThrowsExactly<COMException>(() => calculator.Swap(ref a, ref b));

        Assert.AreEqual("Type mismatch in argument 1 of 'Swap'.", exception.Message);
    }

    [TestMethod]
    public void PassesReferencesOfTheDeclaredTypes()
    {
        // As the generated late-bound wrappers do: the references have the types of the parameters, whatever the values are.
        IDispatch calculator = CreateDispatch();
        Span<int> ids = stackalloc int[1];

        Assert.AreEqual(0, ComDispatch.GetDispIds(calculator, ["Swap"], ids));
        object?[] args = [1, 2];
        DispatchObject.InvokeByRef(calculator, ids[0], DispatchFlags.Method, args, [VarEnum.VT_I4, VarEnum.VT_I4]);
        CollectionAssert.AreEqual(new object?[] { 2, 1 }, args);

        // A value of another type is converted to the type of the parameter.
        args = [(short)3, 4L];
        DispatchObject.InvokeByRef(calculator, ids[0], DispatchFlags.Method, args, [VarEnum.VT_I4, VarEnum.VT_I4]);
        CollectionAssert.AreEqual(new object?[] { 4, 3 }, args);

        // The out arguments have no values.
        Assert.AreEqual(0, ComDispatch.GetDispIds(calculator, ["Divide"], ids));
        args = [7.0, 2.0, null, null];
        DispatchObject.InvokeByRef(calculator, ids[0], DispatchFlags.Method, args, [VarEnum.VT_EMPTY, VarEnum.VT_EMPTY, VarEnum.VT_R8, VarEnum.VT_R8]);
        CollectionAssert.AreEqual(new object?[] { 7.0, 2.0, 3.0, 1.0 }, args);

        // References to VARIANTs are rejected for these parameters.
        int divide = ids[0];
        Assert.ThrowsExactly<COMException>(() => DispatchObject.InvokeByRef(calculator, divide, DispatchFlags.Method, new object?[] { 7.0, 2.0, null, null }, [false, false, true, true]));
    }

    [TestMethod]
    public void RequiresTheOutputOfByReferenceArgumentsBeforeTheCall()
    {
        // The new values are stored after the call, so a missing output would fail after the member was invoked.
        IDispatch calculator = CreateDispatch();
        Span<int> ids = stackalloc int[1];
        Assert.AreEqual(0, ComDispatch.GetDispIds(calculator, ["Swap"], ids));
        int swap = ids[0];

        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            ComDispatch.Invoke(calculator, swap, DispatchFlags.Method, new object?[] { 1, 2 }, byRef: [DispatchArgumentKind.Typed, DispatchArgumentKind.Typed]));

        Assert.AreEqual("output", exception.ParamName);
    }

    private static dynamic CreateCalculator() => CreateDispatch().AsDynamic();

    private static unsafe IDispatch CreateDispatch()
    {
        Marshal.ThrowExceptionForHR(LoadTypeLibEx(TestEnvironment.TestTypeLibrary, 0, out ComTypes.ITypeLib typeLibrary));
        Guid iid = typeof(ICalculator).GUID;
        typeLibrary.GetTypeInfoOfGuid(ref iid, out ComTypes.ITypeInfo typeInfo);
        nint typeInfoPointer = Marshal.GetComInterfaceForObject<ComTypes.ITypeInfo, ComTypes.ITypeInfo>(typeInfo);

        // The standard dispatch does not hold a reference to the object, so the interface pointer is kept for the test run.
        var calculator = (nint)ComInterfaceMarshaller<ICalculator>.ConvertToUnmanaged(new Calculator());
        try
        {
            Marshal.ThrowExceptionForHR(CreateStdDispatch(0, calculator, typeInfoPointer, out nint unknown));
            var dispatch = ComInterfaceMarshaller<IDispatch>.ConvertToManaged((void*)unknown)!;
            Marshal.Release(unknown);
            return dispatch;
        }
        finally
        {
            Marshal.Release(typeInfoPointer);
        }
    }

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
    private static extern int LoadTypeLibEx(string file, int registerKind, out ComTypes.ITypeLib typeLibrary);

    [DllImport("oleaut32.dll")]
    private static extern int CreateStdDispatch(nint outer, nint instance, nint typeInfo, out nint unknown);

    /// <summary>The first methods of <c>ICalculator</c> of the test type library.</summary>
    [GeneratedComInterface]
    [Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A1010")]
    internal partial interface ICalculator
    {
        int Add(int a, int b);

        void Divide(double a, double b, out double quotient, out double remainder);

        void Swap(ref int a, ref int b);
    }

    [GeneratedComClass]
    internal sealed partial class Calculator : ICalculator
    {
        public int Add(int a, int b) => a + b;

        public void Divide(double a, double b, out double quotient, out double remainder)
        {
            quotient = Math.Floor(a / b);
            remainder = a - (quotient * b);
        }

        public void Swap(ref int a, ref int b) => (a, b) = (b, a);
    }
}
