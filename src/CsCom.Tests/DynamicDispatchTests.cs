using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;
using Microsoft.CSharp.RuntimeBinder;

namespace CsCom.Tests;

[TestClass]
public sealed class DynamicDispatchTests
{
    private static readonly Guid DictionaryClsid = new("EE09B103-97E0-11CF-978F-00A02463E06F");
    private static readonly string TempFolder = Path.GetTempPath().TrimEnd('\\');

    [TestMethod]
    public void InvokesMethodsAndProperties()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary.Add("a", 1);
        dictionary.Add("b", "two");

        Assert.AreEqual(2, (int)dictionary.Count);
        Assert.IsTrue((bool)dictionary.Exists("a"));
        Assert.AreEqual(1, (int)dictionary.Item("a"));
        Assert.AreEqual("two", (string)dictionary.Item("b"));
    }

    [TestMethod]
    public void UsesTheDefaultMemberForIndexers()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary["a"] = 1;
        dictionary["a"] = 2;

        Assert.AreEqual(2, (int)dictionary["a"]);
        Assert.AreEqual(1, (int)dictionary.Count);
    }

    [TestMethod]
    public void InvokesTheDefaultMember()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        dictionary.Add("a", 1);

        Assert.AreEqual(1, (int)dictionary("a"));
    }

    [TestMethod]
    public void SetsProperties()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary.CompareMode = 1;
        dictionary.Add("Key", 1);

        Assert.AreEqual(1, (int)dictionary.CompareMode);
        Assert.IsTrue((bool)dictionary.Exists("KEY"));
    }

    [TestMethod]
    public void EnumeratesCollections()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        dictionary.Add("a", 1);
        dictionary.Add("b", 2);

        var keys = new List<string>();
        foreach (var key in dictionary)
        {
            keys.Add((string)key);
        }

        CollectionAssert.AreEqual(new[] { "a", "b" }, keys);
    }

    [TestMethod]
    public void PassesNamedArguments()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");

        Assert.AreEqual(@"C:\a\b", (string)fileSystem.BuildPath(Path: @"C:\a", Name: "b"));
        Assert.AreEqual(@"C:\a\b", (string)fileSystem.BuildPath(Name: "b", Path: @"C:\a"));
        Assert.AreEqual(@"C:\a\b", (string)fileSystem.BuildPath(@"C:\a", Name: "b"));
        Assert.ThrowsExactly<ArgumentException>(() => fileSystem.BuildPath(Path: @"C:\a", Missing: "b"));
    }

    [TestMethod]
    public void WrapsReturnedObjects()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");

        dynamic folder = fileSystem.GetFolder(TempFolder);

        Assert.IsInstanceOfType<DynamicDispatchObject>(folder);
        Assert.AreEqual(Path.GetFileName(TempFolder), (string)folder.Name, ignoreCase: true);
        Assert.IsGreaterThanOrEqualTo(0, (int)fileSystem.GetFolder(TempFolder).Files.Count);
        Assert.IsTrue((bool)folder.Drive.IsReady);
    }

    [TestMethod]
    public void PassesDynamicObjectsAsArguments()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        dictionary.Add("folder", fileSystem.GetFolder(TempFolder));
        dictionary["drive"] = fileSystem.GetFolder(TempFolder).Drive;

        Assert.AreEqual(Path.GetFileName(TempFolder), (string)dictionary.Item("folder").Name, ignoreCase: true);
        Assert.AreEqual(Path.GetPathRoot(TempFolder)![0], ((string)dictionary["drive"].DriveLetter)[0]);
    }

    [TestMethod]
    public void ConvertsToInterfacesAndDefaultMemberValues()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");

        IDispatch dispatch = fileSystem;
        string path = fileSystem.GetFolder(TempFolder);

        Assert.AreEqual(0, dispatch.GetTypeInfoCount(out uint count));
        Assert.AreEqual(1u, count);
        Assert.AreEqual(TempFolder, path, ignoreCase: true);
    }

    [TestMethod]
    public void ReportsUnknownMembersAndComErrors()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        dictionary.Add("a", 1);

        Assert.ThrowsExactly<RuntimeBinderException>(() => dictionary.NoSuchProperty);
        Assert.ThrowsExactly<RuntimeBinderException>(() => dictionary.NoSuchMethod(1));
        var exception = Assert.ThrowsExactly<COMException>(() => dictionary.Add("a", 2));
        Assert.AreEqual(unchecked((int)0x800A01C9), exception.HResult);
    }

    [TestMethod]
    public void WrapsExistingObjects()
    {
        var dispatch = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        dynamic fromInterface = dispatch.AsDynamic();
        fromInterface.Add("a", 1);

        dynamic fromObject = ComDynamic.ToDynamic(ComActivator.CreateInstance<object>("Scripting.Dictionary"))!;
        fromObject.Add("b", 2);

        Assert.AreEqual(1, (int)fromInterface.Count);
        Assert.AreEqual(1, (int)fromObject.Count);
        Assert.IsNull(ComDynamic.ToDynamic(null));
        Assert.ThrowsExactly<InvalidCastException>(() => ComDynamic.ToDynamic("not a COM object"));
    }

    [TestMethod]
    public void ReturnsOneWrapperPerComObject()
    {
        var dispatch = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        dynamic container = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        object wrapper = dispatch.AsDynamic();
        container.Add("item", wrapper);

        Assert.AreSame(wrapper, (object)dispatch.AsDynamic());
        Assert.AreSame(wrapper, (object)ComDynamic.ToDynamic(dispatch)!);
        Assert.AreSame(wrapper, (object)container.Item("item"));
        Assert.IsTrue((bool)(container.Item("item") == container["item"]));
    }

    [TestMethod]
    public void AsDynamicLateBindsLikeToDynamic()
    {
        // A late-bound wrapper stands for its COM object, but one of the application that is exposed to COM and implements
        // IDispatch, e.g. a decorator, is late bound itself, by both overloads of AsDynamic as by ToDynamic; such a class
        // has both, so the calls cast it.
        var dispatch = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        var exposed = new ExposedDispatchObject(dispatch);
        object dynamicObject = ComDynamic.ToDynamic(exposed)!;
        Assert.AreSame(exposed, ((IDispatchProvider)dynamicObject).Dispatch);
        Assert.AreSame(dynamicObject, (object)((DispatchObject)exposed).AsDynamic());
        Assert.AreSame(dynamicObject, (object)((IDispatch)exposed).AsDynamic());

        Assert.AreSame(dispatch, ((IDispatchProvider)(object)new PlainWrapper(dispatch).AsDynamic()).Dispatch);
        Assert.AreSame((object)ComDynamic.ToDynamic(dispatch)!, (object)new PlainWrapper(dispatch).AsDynamic());
    }

    [TestMethod]
    public void WrappersDoNotKeepComObjectsAlive()
    {
        var (comObject, wrapper) = CreateWrappedObject();
        for (int i = 0; i < 3 && (comObject.IsAlive || wrapper.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.IsFalse(comObject.IsAlive);
        Assert.IsFalse(wrapper.IsAlive);
    }

    [TestMethod]
    public void IndexesParameterizedProperties()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        dictionary.Add("a", 1);

        Assert.AreEqual(1, (int)dictionary.Item["a"]);
        dictionary.Item["a"] = 5;
        dictionary.Key["a"] = "b";

        Assert.AreEqual(5, (int)dictionary.Item["b"]);
        Assert.IsFalse((bool)dictionary.Exists("a"));
        Assert.AreEqual(5, (int)dictionary.Item("b"));
        var exception = Assert.ThrowsExactly<COMException>(() => (int)dictionary.Item);
        Assert.AreEqual("'Item' does not take 0 arguments.", exception.Message);
    }

    [TestMethod]
    public void ReportsTheMemberAndTheArgumentOfInvocationErrors()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        var count = Assert.ThrowsExactly<COMException>(() => fileSystem.BuildPath(@"C:\a"));
        var mismatch = Assert.ThrowsExactly<COMException>(() => fileSystem.BuildPath(@"C:\a", new[] { 1, 2 }));
        var defaultMember = Assert.ThrowsExactly<COMException>(() => dictionary());

        Assert.AreEqual(unchecked((int)0x8002000E), count.HResult);
        Assert.AreEqual("'BuildPath' does not take 1 argument.", count.Message);
        Assert.AreEqual(unchecked((int)0x80020005), mismatch.HResult);
        Assert.AreEqual("Type mismatch in argument 2 of 'BuildPath'.", mismatch.Message);
        Assert.AreEqual("The default member does not take 0 arguments.", defaultMember.Message);
    }

    [TestMethod]
    public void ConvertsToLateBoundWrappers()
    {
        dynamic dictionary = ComActivator.CreateDynamicInstance("Scripting.Dictionary");

        DictionaryWrapper wrapper = dictionary;
        IDispatch dispatch = dictionary;

        Assert.AreSame(dispatch, wrapper.Dispatch);
        Assert.ThrowsExactly<RuntimeBinderException>(() => { PlainWrapper plain = dictionary; });
    }

    [TestMethod]
    public void ListsPropertiesForDebuggers()
    {
        dynamic fileSystem = ComActivator.CreateDynamicInstance("Scripting.FileSystemObject");
        object folder = fileSystem.GetFolder(TempFolder);

        var names = ((DynamicDispatchObject)folder).GetDynamicMemberNames().ToList();

        CollectionAssert.IsSubsetOf(new[] { "Path", "Name", "ParentFolder", "Files", "SubFolders" }, names);
        CollectionAssert.DoesNotContain(names, "Copy");
        CollectionAssert.DoesNotContain(names, "QueryInterface");
        Assert.AreEqual("IFolder", ComDynamic.GetTypeName(folder));
        Assert.AreEqual("IDictionary", ComDynamic.GetTypeName(ComActivator.CreateInstance<IDispatch>(DictionaryClsid)));
    }

    [TestMethod]
    public void AnExposedObjectIsLateBoundItselfWhenItImplementsIDispatch()
    {
        // An object of the application that is exposed to COM is passed itself, also when it wraps a COM object and
        // implements IDispatchProvider: it is late bound itself, here without type information, also for its events. One
        // that does not implement IDispatch is late bound through the IDispatch that it provides.
        var dictionary = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        var dispatching = new DispatchingProvider(dictionary);
        var provider = new ProviderObject(dictionary);

        // The conversion to object is no dynamic operation.
        Assert.AreSame(dispatching, ((IDispatchProvider)(object)ComDynamic.ToDynamic(dispatching)!).Dispatch);
        Assert.IsNull(ComDynamic.GetTypeName(dispatching));
        Assert.AreSame(dictionary, ((IDispatchProvider)(object)ComDynamic.ToDynamic(provider)!).Dispatch);
        Assert.AreEqual("IDictionary", ComDynamic.GetTypeName(provider));
        Assert.AreEqual("IDictionary", ComDynamic.GetTypeName(new PlainWrapper(dictionary)));
    }

    [TestMethod]
    public void AnExposedObjectThatACallReturnsIsLateBoundLikeToDynamic()
    {
        // The object is passed itself, and its pointer converts back to it: the dynamic object of the result is that of
        // ComDynamic.ToDynamic, through the IDispatch that it provides when it does not implement IDispatch, e.g. a
        // decorator. One that provides none cannot be late bound, and is returned as it is. The decorated object is
        // another dictionary, so that the container does not hold itself.
        var decorated = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        var provider = new ProviderObject(decorated);
        var dispatching = new DispatchingProvider(decorated);
        var none = new ProviderObject(null!);
        dynamic container = ComActivator.CreateDynamicInstance("Scripting.Dictionary");
        try
        {
            container.Add("provider", provider);
            container.Add("dispatching", dispatching);
            container.Add("none", none);

            Assert.AreSame((object)ComDynamic.ToDynamic(provider)!, (object)container.Item("provider"));
            Assert.AreSame((object)ComDynamic.ToDynamic(decorated)!, (object)container.Item("provider"));
            Assert.AreSame((object)ComDynamic.ToDynamic(dispatching)!, (object)container.Item("dispatching"));
            Assert.AreSame(none, (object)container.Item("none"));
        }
        finally
        {
            container.RemoveAll();
        }
    }

    [TestMethod]
    public void AWrapperWithoutAComObjectIsNoObjectForDynamic()
    {
        // Its Dispatch breaks the contract of IDispatchProvider: it stands for no object, like null, which ToDynamic converts
        // and the other methods reject. An object that is exposed to COM and provides no IDispatch does not implement it,
        // like in a dispinterface wrapper.
        var wrapper = new HandWrittenWrapper(null!);
        Assert.IsNull((object?)ComDynamic.ToDynamic(wrapper));
        Action[] actions =
        [
            () => ComDynamic.GetTypeName(wrapper),
            () => ComDynamic.AddEventHandler(wrapper, "Changed", () => { }),
            () => ComDynamic.RemoveEventHandler(wrapper, "Changed", () => { }),
            () => ComDynamic.RemoveEventHandlers(wrapper),
        ];
        foreach (Action action in actions)
        {
            Assert.AreEqual("comObject", Assert.ThrowsExactly<ArgumentNullException>(action).ParamName);
        }

        var provider = new ProviderObject(null!);
        Assert.ThrowsExactly<InvalidCastException>(() => ComDynamic.ToDynamic(provider));
        Assert.ThrowsExactly<InvalidCastException>(() => ComDynamic.GetTypeName(provider));
    }

    [TestMethod]
    public void AnUnknownWrapperStandsForItsObjectForDynamic()
    {
        // ComDynamic late binds the object that the wrapper names, as where an object is passed to COM: also the object of
        // a nested wrapper, and the COM object of a late-bound wrapper. An UnknownWrapper of null stands for no object, like
        // null, and a DispatchWrapper is rejected.
        var dictionary = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        object expected = ComDynamic.ToDynamic(dictionary)!;
        Assert.AreSame(expected, (object?)ComDynamic.ToDynamic(new UnknownWrapper(dictionary)));
        Assert.AreSame(expected, (object?)ComDynamic.ToDynamic(new UnknownWrapper(new UnknownWrapper(new HandWrittenWrapper(dictionary)))));
        Assert.AreSame(expected, (object?)ComDynamic.ToDynamic(new UnknownWrapper(expected)));
        Assert.AreEqual("IDictionary", ComDynamic.GetTypeName(new UnknownWrapper(dictionary)));

        var none = new UnknownWrapper(null);
        Assert.IsNull((object?)ComDynamic.ToDynamic(none));
        Action[] actions =
        [
            () => ComDynamic.GetTypeName(none),
            () => ComDynamic.AddEventHandler(none, "Changed", () => { }),
            () => ComDynamic.RemoveEventHandler(none, "Changed", () => { }),
            () => ComDynamic.RemoveEventHandlers(none),
        ];
        foreach (Action action in actions)
        {
            var exception = Assert.ThrowsExactly<ArgumentNullException>(action);
            Assert.AreEqual("comObject", exception.ParamName);
            StringAssert.Contains(exception.Message, nameof(UnknownWrapper));
        }

        var dispatchWrapper = new DispatchWrapper(null);
        Assert.ThrowsExactly<ArgumentException>(() => ComDynamic.ToDynamic(dispatchWrapper));
        Assert.ThrowsExactly<ArgumentException>(() => ComDynamic.GetTypeName(dispatchWrapper));
        Assert.ThrowsExactly<ArgumentException>(() => ComDynamic.AddEventHandler(dispatchWrapper, "Changed", () => { }));
    }

    [TestMethod]
    public unsafe void TheDynamicSupportFollowsTheRulesOfTheRuntime()
    {
        // CsCom.Runtime.Dynamic may only use the public API of CsCom.Runtime, so ComDynamic has copies of two of its rules:
        // the IDispatch that a value stands for, as in a SAFEARRAY of IDispatch (ComObjects.GetDispatch), and whether
        // VariantMarshaller passes a value as an object, which dynamic assigns to a property by reference (IsComObject).
        // The first decides the dynamic object of what a COM object returns for the value too (Wrap): an object of the
        // application that is exposed to COM is returned itself, and late bound through the IDispatch of that rule; a
        // value without one is returned as it is.
        var dictionary = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        object other;
        try
        {
            other = new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        object?[] values =
        [
            null,
            dictionary,
            other,
            (object)ComDynamic.ToDynamic(dictionary)!,
            new HandWrittenWrapper(dictionary),
            new HandWrittenWrapper(null!),
            new ProviderObject(dictionary),
            new ProviderObject(null!),
            new DispatchingProvider(dictionary),
            new ExposedWrapper(dictionary),
            new UnknownWrapper(dictionary),
            new UnknownWrapper(null),
            new UnknownWrapper(new UnknownWrapper(new HandWrittenWrapper(dictionary))),
            new UnknownWrapper(new ProviderObject(dictionary)),
            new DispatchWrapper(null),
            "text",
            1,
            new object(),
            new[] { 1 },
            System.Reflection.Missing.Value,
            new QueryCountingObject(Guid.Empty),
        ];

        // A delegate rather than MethodInfo.Invoke, which takes Missing.Value for an omitted argument.
        var isComObject = typeof(DynamicDispatchObject)
            .GetMethod("IsComObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<object?, bool>>();
        var wrap = typeof(DynamicDispatchObject)
            .GetMethod("Wrap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .CreateDelegate<Func<object?, object?>>();
        using var dispatches = ComSafeArray<object?>.Create(VarEnum.VT_DISPATCH, [1]);
        foreach (object? value in values)
        {
            string name = value?.GetType().Name ?? "null";
            string runtime = Outcome(() =>
            {
                dispatches[0] = value;
                return dispatches[0];
            });
            string late = Outcome(() => (object?)ComDynamic.ToDynamic(value) is IDispatchProvider wrapper ? wrapper.Dispatch : null);
            Assert.AreEqual(runtime, late, name);

            ComVariant variant;
            try
            {
                variant = VariantMarshaller.ConvertToUnmanaged(value);
            }
            catch (ArgumentException)
            {
                // A value that cannot be passed is not assigned either way.
                continue;
            }

            try
            {
                bool expected = variant.VarType is VarEnum.VT_DISPATCH or VarEnum.VT_UNKNOWN;
                Assert.AreEqual(expected, isComObject(value), name);

                // A value that is returned as it is cannot be late bound: the runtime finds no IDispatch for it either.
                object? returned = VariantMarshaller.ConvertToManaged(variant);
                string runtimeReturned = Outcome(() =>
                {
                    dispatches[0] = returned;
                    return dispatches[0];
                });
                string wrapped = Outcome(() => wrap(returned) switch
                {
                    DynamicDispatchObject dynamicObject => ((IDispatchProvider)dynamicObject).Dispatch,
                    { } result when ReferenceEquals(result, returned) => throw new InvalidCastException(),
                    var result => result,
                });
                Assert.AreEqual(runtimeReturned, wrapped, $"{name} returned as {returned?.GetType().Name ?? "null"}");
            }
            finally
            {
                VariantMarshaller.Free(variant);
            }
        }

        ComMarshalling.Release(other);

        // The identity of the COM object, or the exception type.
        static string Outcome(Func<object?> get)
        {
            try
            {
                if (get() is not { } result)
                {
                    return "null";
                }

                nint identity = (nint)ComObjectMarshaller<object>.ConvertToUnmanaged(result);
                Marshal.Release(identity);
                return $"0x{identity:X}";
            }
            catch (Exception exception)
            {
                return exception.GetType().Name;
            }
        }
    }

    [TestMethod]
    public void ResolvesProgIds()
    {
        Assert.AreEqual(DictionaryClsid, ComActivator.GetClassId("Scripting.Dictionary"));
        var exception = Assert.ThrowsExactly<COMException>(() => ComActivator.GetClassId("CsCom.No.Such.Class"));
        StringAssert.Contains(exception.Message, "CsCom.No.Such.Class");
    }

    // RegisterActiveObject fails with E_INVALIDARG for objects of the multithreaded apartment of the test host, so the object is
    // registered and retrieved in a single-threaded apartment, like Office applications register themselves.
    [STATestMethod]
    public unsafe void GetsRunningInstances()
    {
        var clsid = Guid.NewGuid();
        var dispatch = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        nint unknown = (nint)ComInterfaceMarshaller<IDispatch>.ConvertToUnmanaged(dispatch);
        int hr = RegisterActiveObject(unknown, clsid, 0, out uint cookie);
        Marshal.Release(unknown);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Assert.AreSame(dispatch, ComActivator.GetActiveInstance<IDispatch>(clsid));
            Assert.AreSame((object)dispatch.AsDynamic(), (object)ComActivator.GetActiveDynamicInstance(clsid));
            Assert.IsTrue(ComActivator.TryGetActiveInstance(clsid, out IDispatch? active));
            Assert.AreSame(dispatch, active);
            Assert.IsTrue(ComActivator.TryGetActiveDynamicInstance(clsid, out dynamic? dynamicActive));
            Assert.AreSame((object)dispatch.AsDynamic(), (object)dynamicActive);
        }
        finally
        {
            Marshal.ThrowExceptionForHR(RevokeActiveObject(cookie, 0));
        }

        var exception = Assert.ThrowsExactly<COMException>(() => ComActivator.GetActiveInstance<IDispatch>(clsid));
        Assert.AreEqual(unchecked((int)0x800401E3), exception.HResult);
        Assert.AreEqual($"No running instance of the class {clsid:B} is registered.", exception.Message);
        Assert.IsFalse(ComActivator.TryGetActiveInstance(clsid, out IDispatch? none));
        Assert.IsNull(none);
        Assert.IsFalse(ComActivator.TryGetActiveDynamicInstance(clsid, out dynamic? noneDynamic));
        Assert.IsNull((object?)noneDynamic);
    }

    [TestMethod]
    public void TryGetActiveInstanceAcceptsUnknownProgIds()
    {
        Assert.IsFalse(ComActivator.TryGetActiveInstance("CsCom.No.Such.Class", out IDispatch? instance));
        Assert.IsNull(instance);
        Assert.IsFalse(ComActivator.TryGetActiveDynamicInstance("CsCom.No.Such.Class", out dynamic? dynamicInstance));
        Assert.IsNull((object?)dynamicInstance);
        Assert.IsFalse(ComActivator.TryGetActiveInstance("Scripting.Dictionary", out instance));
    }

    [TestMethod]
    public void BindsMonikers()
    {
        dynamic dictionary = ComActivator.BindToDynamicMoniker("new:Scripting.Dictionary");
        dictionary.Add("a", 1);

        Assert.AreEqual(1, (int)dictionary.Count);
        var exception = Assert.ThrowsExactly<COMException>(() => ComActivator.BindToMoniker<IDispatch>("cscom-no-such-moniker:x"));
        StringAssert.StartsWith(exception.Message, "The moniker 'cscom-no-such-moniker:x' cannot be bound to an object: ");
    }

    [DllImport("oleaut32.dll")]
    private static extern int RegisterActiveObject(nint unknown, in Guid clsid, uint flags, out uint cookie);

    [DllImport("oleaut32.dll")]
    private static extern int RevokeActiveObject(uint cookie, nint reserved);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference ComObject, WeakReference Wrapper) CreateWrappedObject()
    {
        var dispatch = ComActivator.CreateInstance<IDispatch>(DictionaryClsid);
        dynamic dictionary = dispatch.AsDynamic();
        dictionary.Add("a", 1);
        return (new WeakReference(dispatch), new WeakReference(dictionary));
    }

    private sealed class DictionaryWrapper(IDispatch dispatch) : DispatchObject(dispatch), IDispatchWrapper<DictionaryWrapper>
    {
        public static DictionaryWrapper Create(IDispatch dispatch) => new(dispatch);
    }

    private sealed class PlainWrapper(IDispatch dispatch) : DispatchObject(dispatch);
}
