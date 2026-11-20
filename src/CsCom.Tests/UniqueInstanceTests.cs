using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CsCom.Runtime;

namespace CsCom.Tests;

/// <summary>
/// Tests the activation of COM objects in managed objects of their own, against the <c>Scripting.Dictionary</c> of Windows.
/// </summary>
[TestClass]
public unsafe class UniqueInstanceTests
{
    private const string Dictionary = "Scripting.Dictionary";

    [TestMethod]
    public void UniqueInstancesAreReleasedAtOnce()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary, unique: true);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));

        ((ComObject)(object)dictionary).FinalRelease();

        // The reference of the test is the last one.
        Assert.AreEqual(0, Marshal.Release(unknown));
    }

    [TestMethod]
    public void AHandWrittenWrapperStandsForItsComObject()
    {
        // A wrapper of your own that only implements IDispatchWrapper<T> is an IDispatchProvider like a generated one: it is
        // passed as its COM object, and ComMarshalling.Release releases a unique one.
        var wrapper = ComActivator.CreateDispatchInstance<HandWrittenWrapper>(ComActivator.GetClassId(Dictionary), unique: true);
        Assert.IsTrue(ComWrappers.TryGetComInstance(wrapper.Dispatch, out nint unknown));

        ComVariant variant = VariantMarshaller.ConvertToUnmanaged(wrapper);
        try
        {
            Assert.AreEqual(VarEnum.VT_DISPATCH, variant.VarType);
        }
        finally
        {
            variant.Dispose();
        }

        ComMarshalling.Release(wrapper);
        Assert.AreEqual(0, Marshal.Release(unknown));
    }

    [TestMethod]
    public void SharedInstancesAreNotReleasedManually()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));

        ((ComObject)(object)dictionary).FinalRelease();

        Assert.IsGreaterThan(0, Marshal.Release(unknown));
        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void AUniqueInstanceIsReleasedWhenItsWrapperCannotBeCreated()
    {
        // The release itself is covered by AUniqueInstanceWithoutTheInterfaceIsReleased, through the same helper: the
        // object created here has no pointer that the test could count.
        Guid clsid = ComActivator.GetClassId(Dictionary);
        Assert.ThrowsExactly<InvalidOperationException>(() => ComActivator.CreateDispatchInstance<FailingWrapper>(clsid, unique: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ComActivator.CreateDispatchInstance<FailingWrapper>(clsid));
    }

    [TestMethod]
    public void TheOverloadsWithoutTheParameterReturnSharedInstances()
    {
        Guid clsid = ComActivator.GetClassId(Dictionary);
        IDispatch[] shared =
        [
            ComActivator.CreateInstance<IDispatch>(clsid),
            ComActivator.CreateInstance<IDispatch>(clsid, ClassContext.InprocServer),
            ComActivator.CreateInstance<IDispatch>(Dictionary, ClassContext.InprocServer),
            ComActivator.CreateInstance<IDispatch>(clsid, unique: false),
            ComActivator.BindToMoniker<IDispatch>("new:" + Dictionary),
            ComActivator.CreateDispatchInstance<MemoryLeakTests.Wrapper>(clsid).Dispatch,
        ];
        IDispatch[] unique =
        [
            ComActivator.CreateInstance<IDispatch>(clsid, unique: true),
            ComActivator.CreateInstance<IDispatch>(clsid, unique: true, ClassContext.InprocServer),
            ComActivator.CreateDispatchInstance<MemoryLeakTests.Wrapper>(clsid, unique: true).Dispatch,
        ];

        // Only a unique instance is released at once: the reference of the test is the last one then.
        foreach (var (instance, isUnique) in shared.Select(static s => (s, false)).Concat(unique.Select(static u => (u, true))))
        {
            Assert.IsTrue(ComWrappers.TryGetComInstance(instance, out nint unknown));
            ComMarshalling.Release(instance);
            Assert.AreEqual(isUnique, Marshal.Release(unknown) == 0);
        }

        GC.KeepAlive(shared);

        // No instance is running, with or without the parameter, and for a ProgID that is not registered.
        Assert.IsFalse(ComActivator.TryGetActiveInstance<IDispatch>(clsid, out _));
        Assert.IsFalse(ComActivator.TryGetActiveInstance<IDispatch>(clsid, unique: true, out _));
        Assert.IsFalse(ComActivator.TryGetActiveInstance<IDispatch>("CsCom.NotRegistered", unique: true, out _));
        Assert.ThrowsExactly<COMException>(() => ComActivator.GetActiveInstance<IDispatch>(Dictionary, unique: true));
    }

    [TestMethod]
    public void UniqueInstancesAreNotShared()
    {
        var unique = ComActivator.CreateInstance<IDispatch>(Dictionary, unique: true);
        Assert.IsTrue(ComWrappers.TryGetComInstance(unique, out nint unknown));
        try
        {
            var shared = ComInterfaceMarshaller<IDispatch>.ConvertToManaged((void*)unknown);

            Assert.AreNotSame(unique, shared);
            Assert.AreSame(shared, ComInterfaceMarshaller<IDispatch>.ConvertToManaged((void*)unknown));
            Assert.AreNotSame(unique, UniqueComObjectMarshaller<IDispatch>.ConvertToManaged((void*)unknown));
            Assert.AreSame(shared, ComObjectMarshaller<IDispatch>.ConvertToManaged((void*)unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [TestMethod]
    public void MonikersBindToUniqueInstances()
    {
        var first = ComActivator.BindToMoniker<IDispatch>("new:" + Dictionary, unique: true);
        Assert.IsTrue(ComWrappers.TryGetComInstance(first, out nint unknown));

        ((ComObject)(object)first).FinalRelease();

        Assert.AreEqual(0, Marshal.Release(unknown));
    }

    [TestMethod]
    public void DisposingTheEnumeratorReleasesAUniqueEnumerator()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        object shared = DispatchObject.Invoke(dictionary, DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet)!;
        Assert.IsTrue(ComWrappers.TryGetComInstance(shared, out nint unknown));
        try
        {
            var enumerator = ComEnumerator.FromObject(UniqueComObjectMarshaller<object>.ConvertToManaged((void*)unknown), ownsEnumerator: true);
            Assert.IsFalse(enumerator.MoveNext());
            int references = CountReferences(unknown);

            enumerator.Dispose();

            Assert.IsTrue(CountReferences(unknown) < references);

            // Disposing it again releases nothing more.
            references = CountReferences(unknown);
            enumerator.Dispose();
            Assert.AreEqual(references, CountReferences(unknown));

            // The shared enumerator is not released; creating it queries the interface, which adds a reference.
            var sharedEnumerator = ComEnumerator.FromObject(shared, ownsEnumerator: true);
            references = CountReferences(unknown);
            sharedEnumerator.Dispose();
            Assert.AreEqual(references, CountReferences(unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(shared);
    }

    [TestMethod]
    public void DisposingTheEnumeratorKeepsAnEnumeratorOfTheCaller()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        object shared = DispatchObject.Invoke(dictionary, DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet)!;
        Assert.IsTrue(ComWrappers.TryGetComInstance(shared, out nint unknown));
        try
        {
            // The constructor does not take the ownership of the object, which the caller can still use.
            var unique = UniqueComObjectMarshaller<IEnumVARIANT>.ConvertToManaged((void*)unknown)!;
            int references = CountReferences(unknown);
            using (var enumerator = new ComEnumerator(unique))
            {
                Assert.IsFalse(enumerator.MoveNext());
            }

            Assert.AreEqual(references, CountReferences(unknown));
            Assert.AreEqual(0, unique.Reset());

            new ComEnumerator(unique, ownsEnumerator: true).Dispose();
            Assert.IsTrue(CountReferences(unknown) < references);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(shared);
    }

    [TestMethod]
    public void ReleaseReleasesUniqueObjectsAndTheObjectsOfWrappers()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            int references = CountReferences(unknown);

            // Nothing to release.
            ComMarshalling.Release(null);
            ComMarshalling.Release("text");
            ComMarshalling.Release(dictionary);
            ComMarshalling.Release(new MemoryLeakTests.Wrapper(dictionary));
            Assert.AreEqual(references, CountReferences(unknown));

            var unique = UniqueComObjectMarshaller<IDispatch>.ConvertToManaged((void*)unknown)!;
            ComMarshalling.Release(unique);
            Assert.AreEqual(references, CountReferences(unknown));

            // A late-bound wrapper has the object in its Dispatch.
            var wrapper = UniqueDispatchWrapperMarshaller<MemoryLeakTests.Wrapper>.ConvertToManaged((void*)unknown)!;
            Assert.IsTrue(CountReferences(unknown) > references);
            ComMarshalling.Release(wrapper);
            Assert.AreEqual(references, CountReferences(unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void ReleaseReleasesTheObjectThatAnUnknownWrapperStandsFor()
    {
        // An UnknownWrapper stands for its object, also a nested one, as where an object is passed to COM and in
        // ComEnumerator.FromObject: a unique object, or the COM object of a late-bound wrapper, is released.
        IDispatch unique = CreateUniqueObject(new ItemsEnumerator([1]));
        ComMarshalling.Release(new UnknownWrapper(new UnknownWrapper(unique)));
        Assert.IsTrue(IsReleased(unique));

        unique = CreateUniqueObject(new ItemsEnumerator([2]));
        ComMarshalling.Release(new UnknownWrapper(new MemoryLeakTests.Wrapper(unique)));
        Assert.IsTrue(IsReleased(unique));

        // An object that is exposed to COM is left alone, also the object that it provides; an UnknownWrapper of null
        // stands for no object.
        IDispatch provided = CreateUniqueObject(new ItemsEnumerator([3]));
        ComMarshalling.Release(new UnknownWrapper(new ExposedWrapper(provided)));
        ComMarshalling.Release(new UnknownWrapper(null));
        Assert.IsFalse(IsReleased(provided));
        ComMarshalling.Release(provided);

        // A DispatchWrapper is rejected, as everywhere else, rather than ignored.
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentException>(() => ComMarshalling.Release(new DispatchWrapper(null))).ParamName);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentException>(() => ComMarshalling.Release(new UnknownWrapper(new DispatchWrapper(null)))).ParamName);
    }

    [TestMethod]
    public void AReleasedObjectIsNotMarshalled()
    {
        // A released object that is passed to a COM object must fail, rather than pass a pointer that is not valid any more.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary, unique: true);
        ComMarshalling.Release(dictionary);

        Assert.ThrowsExactly<ObjectDisposedException>(() => ComObjectMarshaller<IDispatch>.ConvertToUnmanaged(dictionary));
        Assert.ThrowsExactly<ObjectDisposedException>(() => ComObjectMarshaller<object>.ConvertToUnmanaged(dictionary));
        Assert.ThrowsExactly<ObjectDisposedException>(() => VariantMarshaller.ConvertToUnmanaged(dictionary));

        // Neither is a late-bound wrapper of it.
        var wrapper = new MemoryLeakTests.Wrapper(dictionary);
        Assert.ThrowsExactly<ObjectDisposedException>(() => ComObjectMarshaller<object>.ConvertToUnmanaged(wrapper));
        Assert.ThrowsExactly<ObjectDisposedException>(() => VariantMarshaller.ConvertToUnmanaged(wrapper));
    }

    [TestMethod]
    public void MarshallingAnObjectAsItsInterfaceAddsNoReference()
    {
        // The pointer is that of the interface that the object has already, without asking the COM object again.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            int references = CountReferences(unknown);

            void* pointer = ComObjectMarshaller<IDispatch>.ConvertToUnmanaged(dictionary);
            Assert.AreEqual(references + 1, CountReferences(unknown));
            ComObjectMarshaller<IDispatch>.Free(pointer);
            Assert.AreEqual(references, CountReferences(unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void OnlyTheObjectThatWasCreatedIsReleased()
    {
        // An object of the application that is exposed to COM, which a pointer converts back to, keeps the COM object that
        // it provides, also when an enumerator would own it (a late-bound wrapper is the marshaller's, see
        // TheObjectOfAnOwnedWrapperIsReleased).
        var unique = ComActivator.CreateInstance<IDispatch>(Dictionary, unique: true);
        Assert.ThrowsExactly<InvalidCastException>(() => ComEnumerator.FromObject(new ProviderObject(unique), ownsEnumerator: true));
        Assert.ThrowsExactly<InvalidCastException>(() => ComEnumerator.FromObject(new ExposedWrapper(unique), ownsEnumerator: true));
        Assert.AreEqual("IDictionary", ComDispatch.GetTypeName(unique));

        // An object that the enumerator does not own is neither released when it is no enumerator, nor when it is disposed.
        Assert.ThrowsExactly<InvalidCastException>(() => ComEnumerator.FromObject(unique, ownsEnumerator: false));
        Assert.AreEqual("IDictionary", ComDispatch.GetTypeName(unique));
        // The unique object adds a reference of its own, so the reference of the pointer is released.
        object enumerator;
        void* enumeratorPointer = ComObjectMarshaller<object>.ConvertToUnmanaged(DispatchObject.Invoke(unique, DispIds.NewEnum, DispatchFlags.Method | DispatchFlags.PropertyGet));
        try
        {
            enumerator = UniqueComObjectMarshaller<object>.ConvertToManaged(enumeratorPointer)!;
        }
        finally
        {
            ComObjectMarshaller<object>.Free(enumeratorPointer);
        }

        ComEnumerator.FromObject(enumerator, ownsEnumerator: false).Dispose();
        Assert.AreEqual(0, ((IEnumVARIANT)enumerator).Reset());
        ComMarshalling.Release(enumerator);

        // The pointer of a managed object that wraps a COM object, which does not implement the interface, is converted to a
        // COM object of its own, which calls it through COM and is released: the managed object keeps its COM object.
        var provider = new ProviderObject(unique);
        void* pointer = ComObjectMarshaller<object>.ConvertToUnmanaged(provider);
        try
        {
            Assert.ThrowsExactly<InvalidCastException>(() => UniqueComObjectMarshaller<IConnectionPoint>.ConvertToManaged(pointer));
        }
        finally
        {
            ComObjectMarshaller<object>.Free(pointer);
        }

        Assert.AreEqual("IDictionary", ComDispatch.GetTypeName(unique));

        // Asked for a COM interface that it implements, the managed object is exposed itself, not the object that it wraps.
        void* exposed = ComObjectMarshaller<IProviderObject>.ConvertToUnmanaged(provider);
        try
        {
            Assert.AreSame(provider, ComObjectMarshaller<IProviderObject>.ConvertToManaged(exposed));
        }
        finally
        {
            ComObjectMarshaller<IProviderObject>.Free(exposed);
        }

        ComMarshalling.Release(unique);
    }

    [TestMethod]
    public void OnlyTheObjectsOfSourceGeneratedComClassesAreExposedToCom()
    {
        // Such an object is exposed itself, also when it wraps a COM object; the COM object and a late-bound wrapper are not.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComMarshalling.IsExposedToCom(new ProviderObject(dictionary)));
        Assert.IsFalse(ComMarshalling.IsExposedToCom(dictionary));
        Assert.IsFalse(ComMarshalling.IsExposedToCom(new MemoryLeakTests.Wrapper(dictionary)));
        Assert.IsFalse(ComMarshalling.IsExposedToCom(new object()));
        Assert.IsFalse(ComMarshalling.IsExposedToCom(null));
    }

    [TestMethod]
    public void ThePointerOfAManagedObjectIsConvertedToTheObjectItself()
    {
        // Also without a custom instance, whose flags unwrap the pointer: .NET's UniqueComInterfaceMarshaller does not. The
        // object wraps a unique object, which a release would really release.
        var provider = new ProviderObject(ComActivator.CreateInstance<IDispatch>(Dictionary, unique: true));
        void* pointer = ComObjectMarshaller<IProviderObject>.ConvertToUnmanaged(provider);
        try
        {
            object?[] converted =
            [
                UniqueComObjectMarshaller<IProviderObject>.ConvertToManaged(pointer),
                UniqueComObjectMarshaller<object>.ConvertToManaged(pointer),
                ComObjectMarshaller<IProviderObject>.ConvertToManaged(pointer),
                ComObjectMarshaller<object>.ConvertToManaged(pointer),
            ];

            foreach (object? value in converted)
            {
                Assert.AreSame(provider, value);
            }

            // The object is not the receiver's to release, and neither is the object that it wraps.
            ComMarshalling.Release(converted[0]);
            ((IProviderObject)converted[0]!).Run();
            Assert.AreEqual("IDictionary", ComDispatch.GetTypeName(provider.Dispatch));
        }
        finally
        {
            ComObjectMarshaller<IProviderObject>.Free(pointer);
            ComMarshalling.Release(provider.Dispatch);
        }
    }

    [TestMethod]
    public void AManagedObjectThatIsExposedToComIsPassedItself()
    {
        // The object of the application is passed itself, also when it wraps a COM object and implements IDispatchProvider,
        // as an IUnknown and in a VARIANT, so that its pointer converts back to it. A late-bound wrapper, which is not
        // exposed to COM, is passed as its COM object.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        var provider = new ProviderObject(dictionary);
        void* unknown = ComObjectMarshaller<object>.ConvertToUnmanaged(provider);
        ComVariant variant = VariantMarshaller.ConvertToUnmanaged(provider);
        void* wrapped = ComObjectMarshaller<object>.ConvertToUnmanaged(new PlainWrapper(dictionary));
        try
        {
            Assert.AreSame(provider, ComObjectMarshaller<object>.ConvertToManaged(unknown));
            Assert.AreSame(provider, VariantMarshaller.ConvertToManaged(variant));
            Assert.AreSame(dictionary, ComObjectMarshaller<object>.ConvertToManaged(wrapped));
        }
        finally
        {
            ComObjectMarshaller<object>.Free(unknown);
            variant.Dispose();
            ComObjectMarshaller<object>.Free(wrapped);
        }
    }

    [TestMethod]
    public void ALateBoundWrapperThatIsExposedToComIsPassedItself()
    {
        // A wrapper of the application that is exposed to COM and implements IDispatch is passed itself as a parameter of
        // its type too, like in a VARIANT, and its pointer converts back to it.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        var wrapper = new ExposedWrapper(dictionary);
        void* pointer = DispatchWrapperMarshaller<ExposedWrapper>.ConvertToUnmanaged(wrapper);
        try
        {
            Assert.AreSame(wrapper, ComObjectMarshaller<object>.ConvertToManaged(pointer));
            Assert.AreSame(wrapper, DispatchWrapperMarshaller<ExposedWrapper>.ConvertToManaged(pointer));
            Assert.AreSame(wrapper, UniqueDispatchWrapperMarshaller<ExposedWrapper>.ConvertToManaged(pointer));
        }
        finally
        {
            DispatchWrapperMarshaller<ExposedWrapper>.Free(pointer);
        }
    }

    [TestMethod]
    public void AnExposedObjectIsCalledThroughComForAnotherDeclarationOfItsInterface()
    {
        // The pointer of the object of the application converts back to it for the interface that it implements. Another
        // declaration of the interface, e.g. of another interop assembly, gets a COM object of its own, which calls the
        // object through COM, in a shared conversion and in a unique one alike.
        var provider = new ProviderObject(ComActivator.CreateInstance<IDispatch>(Dictionary));
        void* pointer = ComObjectMarshaller<IProviderObject>.ConvertToUnmanaged(provider);
        try
        {
            Assert.AreSame(provider, ComObjectMarshaller<IProviderObject>.ConvertToManaged(pointer));

            IProviderObjectCopy shared = ComObjectMarshaller<IProviderObjectCopy>.ConvertToManaged(pointer)!;
            Assert.IsInstanceOfType<ComObject>(shared);
            shared.Run();
            Assert.AreEqual(1, provider.Runs);

            IProviderObjectCopy unique = UniqueComObjectMarshaller<IProviderObjectCopy>.ConvertToManaged(pointer)!;
            Assert.IsInstanceOfType<ComObject>(unique);
            Assert.AreNotSame(shared, unique);
            unique.Run();
            Assert.AreEqual(2, provider.Runs);
            ComMarshalling.Release(unique);

            // The COM object of a shared conversion is not shared either: every conversion creates one, which only its
            // receiver has, so that Release releases it, and not the others.
            IProviderObjectCopy again = ComObjectMarshaller<IProviderObjectCopy>.ConvertToManaged(pointer)!;
            Assert.AreNotSame(shared, again);
            ComMarshalling.Release(again);
            Assert.ThrowsExactly<ObjectDisposedException>(() => again.Run());
            shared.Run();
            Assert.AreEqual(3, provider.Runs);
            Assert.AreSame(provider, ComObjectMarshaller<IProviderObject>.ConvertToManaged(pointer));
        }
        finally
        {
            ComObjectMarshaller<IProviderObject>.Free(pointer);
        }
    }

    [TestMethod]
    public void AnExposedObjectWithoutIDispatchIsPassedAsItsDispatchWhereIDispatchIsRequired()
    {
        // An array of the class is a SAFEARRAY of IDispatch, which the object of the application cannot be in, since it
        // does not implement IDispatch: it provides the COM object of its Dispatch instead, like the source of events. In an
        // array of objects, it is passed itself.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        var provider = new ProviderObject(dictionary);
        ComVariant dispatches = VariantMarshaller.ConvertToUnmanaged(new[] { provider });
        ComVariant objects = VariantMarshaller.ConvertToUnmanaged(new object[] { provider });
        try
        {
            Assert.AreEqual(VarEnum.VT_ARRAY | VarEnum.VT_DISPATCH, dispatches.VarType);
            Assert.AreSame(dictionary, ((Array)VariantMarshaller.ConvertToManaged(dispatches)!).GetValue(0));
            Assert.AreSame(provider, ((Array)VariantMarshaller.ConvertToManaged(objects)!).GetValue(0));
        }
        finally
        {
            dispatches.Dispose();
            objects.Dispose();
        }
    }

    [TestMethod]
    public void ALateBoundWrapperConvertsAnExposedObjectThatComesBackItself()
    {
        // The object of the application comes back from a VARIANT itself. A late-bound wrapper converts it to an interface
        // that it implements, and otherwise its COM object is asked for the interface, like when the VARIANT held that COM
        // object; a dispinterface wrapper wraps the IDispatch that it provides, since it does not implement IDispatch. A
        // late-bound wrapper stands for its COM object.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        var provider = new ProviderObject(dictionary);
        var dispatching = new DispatchingProvider(dictionary);
        ComVariant variant = VariantMarshaller.ConvertToUnmanaged(provider);
        try
        {
            object? value = VariantMarshaller.ConvertToManaged(variant);
            Assert.AreSame(provider, value);
            Assert.AreSame(provider, Conversions.ToInterface<IProviderObject>(value));
            Assert.AreSame(dictionary, Conversions.ToInterface<IDispatch>(value));
            Assert.AreSame(dictionary, ((IDispatchProvider)Conversions.ToWrapper<MemoryLeakTests.Wrapper>(value)!).Dispatch);
            Assert.AreSame(dictionary, ((IDispatchProvider)Conversions.ToWrapper<MemoryLeakTests.Wrapper>(new PlainWrapper(dictionary))!).Dispatch);
            Assert.AreSame(dispatching, ((IDispatchProvider)Conversions.ToWrapper<MemoryLeakTests.Wrapper>(dispatching)!).Dispatch);
        }
        finally
        {
            variant.Dispose();
        }
    }

    [TestMethod]
    public void AWrapperWithoutAComObjectStandsForNoObject()
    {
        // Its Dispatch breaks the contract of IDispatchProvider: it is converted to null, like in a VARIANT, rather than
        // wrapped. An object of the application that is exposed to COM and provides no IDispatch does not implement it, as
        // when it is asked through COM.
        var wrapper = new PlainWrapper(null!);
        Assert.IsNull(Conversions.ToWrapper<MemoryLeakTests.Wrapper>(wrapper));
        Assert.IsNull(Conversions.ToInterface<IDispatch>(wrapper));
        CollectionAssert.AreEqual(new IDispatch?[] { null }, Conversions.ToArray<IDispatch>(new object[] { wrapper }));

        var provider = new ProviderObject(null!);
        Assert.AreSame(provider, Conversions.ToInterface<IProviderObject>(provider));
        Assert.ThrowsExactly<InvalidCastException>(() => Conversions.ToWrapper<MemoryLeakTests.Wrapper>(provider));
        Assert.ThrowsExactly<InvalidCastException>(() => Conversions.ToInterface<IDispatch>(provider));
        Assert.ThrowsExactly<InvalidCastException>(() => Conversions.ToArray<IDispatch>(new object[] { provider }));
    }

    [TestMethod]
    public void AWrapperThatAnotherWrapperStandsForConvertsBackToItself()
    {
        // A late-bound wrapper stands for its object, and an object that is exposed to COM without IDispatch for the
        // IDispatch that it provides: a wrapper of the application of the requested type is returned itself then, like by
        // the marshaller of its pointer, rather than wrapped again.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        var exposed = new ExposedWrapper(dictionary);
        Assert.AreSame(exposed, Conversions.ToWrapper<ExposedWrapper>(new PlainWrapper(exposed)));
        Assert.AreSame(exposed, Conversions.ToWrapper<ExposedWrapper>(new ProviderObject(exposed)));

        void* pointer = DispatchWrapperMarshaller<ExposedWrapper>.ConvertToUnmanaged(exposed);
        try
        {
            Assert.AreSame(exposed, DispatchWrapperMarshaller<ExposedWrapper>.ConvertToManaged(pointer));
        }
        finally
        {
            DispatchWrapperMarshaller<ExposedWrapper>.Free(pointer);
        }
    }

    [TestMethod]
    public void AFailedConversionAsksTheComObjectForTheInterfaceOnce()
    {
        // The conversions of the late-bound wrappers and of the event arguments cast a COM object to the interface: a type
        // test before the cast would ask it a second time when it does not implement the interface, since the object keeps
        // no lookup that failed, and the call can go to another process. The managed object behind the COM object, which
        // another ComWrappers exposes, counts the requests.
        var counting = new QueryCountingObject(typeof(IConnectionPoint).GUID);
        nint unknown = new StrategyBasedComWrappers().GetOrCreateComInterfaceForObject(counting, CreateComInterfaceFlags.None);
        try
        {
            var comObject = (ComObject)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Assert.ThrowsExactly<InvalidCastException>(() => Conversions.ToInterface<IConnectionPoint>(comObject));
            Assert.AreEqual(1, counting.Queries);
            comObject.FinalRelease();

            // A ref struct cannot be used in a lambda.
            var variants = stackalloc ComVariant[1];
            variants[0] = ComVariant.CreateRaw(VarEnum.VT_UNKNOWN, unknown);
            var parameters = new DispParams { rgvarg = variants, cArgs = 1 };
            var arguments = new EventArguments(&parameters, null);
            Type? failure = null;
            try
            {
                _ = arguments.Get<IConnectionPoint?>(0);
            }
            catch (Exception e)
            {
                failure = e.GetType();
            }

            Assert.AreEqual(typeof(InvalidCastException), failure);
            Assert.AreEqual(2, counting.Queries);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [TestMethod]
    public void AComObjectIsAskedForIDispatchOnceForAllItsConversions()
    {
        // A COM object passes the IDispatch that it keeps, also as the COM object of a late-bound wrapper, in a VARIANT, in a
        // SAFEARRAY of IDispatch and as a dispinterface wrapper: QueryInterface would ask it on every conversion, through
        // the proxy manager when it is in another apartment. The managed object behind the COM object, which another
        // ComWrappers exposes, counts the requests; the cast of the wrapper's constructor makes the first one.
        var counting = new DispatchCountingObject();
        nint unknown = new StrategyBasedComWrappers().GetOrCreateComInterfaceForObject(counting, CreateComInterfaceFlags.None);
        try
        {
            var comObject = (ComObject)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            var dispatch = (IDispatch)(object)comObject;
            var wrapper = new MemoryLeakTests.Wrapper(dispatch);
            Assert.AreEqual(1, counting.Queries);
            for (int i = 0; i < 3; i++)
            {
                foreach (object value in new object[] { comObject, wrapper, new[] { dispatch } })
                {
                    ComVariant variant = VariantMarshaller.ConvertToUnmanaged(value);
                    Assert.AreEqual(value is Array ? VarEnum.VT_ARRAY | VarEnum.VT_DISPATCH : VarEnum.VT_DISPATCH, variant.VarType);
                    variant.Dispose();
                }

                DispatchWrapperMarshaller<MemoryLeakTests.Wrapper>.Free(DispatchWrapperMarshaller<MemoryLeakTests.Wrapper>.ConvertToUnmanaged(wrapper));
            }

            Assert.AreEqual(1, counting.Queries);
            comObject.FinalRelease();
        }
        finally
        {
            Marshal.Release(unknown);
        }

        // A COM object without IDispatch is asked on every conversion, like before, and only once each time: it is passed
        // as VT_UNKNOWN then.
        var unknownCounting = new QueryCountingObject(new Guid("00020400-0000-0000-C000-000000000046"));
        unknown = new StrategyBasedComWrappers().GetOrCreateComInterfaceForObject(unknownCounting, CreateComInterfaceFlags.None);
        try
        {
            var comObject = (ComObject)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            for (int i = 1; i <= 3; i++)
            {
                ComVariant variant = VariantMarshaller.ConvertToUnmanaged(comObject);
                Assert.AreEqual(VarEnum.VT_UNKNOWN, variant.VarType);
                variant.Dispose();
                Assert.AreEqual(i, unknownCounting.Queries);
            }

            comObject.FinalRelease();
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [TestMethod]
    public void AUniqueObjectIsTheObjectOfAPointerOfTheInstanceOnly()
    {
        // Like a shared object, a unique object is the managed object of a pointer that the instance of the support code has
        // created, e.g. a plain object passed as an IUnknown. The pointer that another ComWrappers has created for a managed
        // object, also one that is exposed to COM, is called through COM.
        var plain = new object();
        nint own = (nint)ComObjectMarshaller<object>.ConvertToUnmanaged(plain);
        nint foreign = new StrategyBasedComWrappers().GetOrCreateComInterfaceForObject(
            new ProviderObject(ComActivator.CreateInstance<IDispatch>(Dictionary)),
            CreateComInterfaceFlags.None);
        try
        {
            Assert.AreSame(plain, UniqueComObjectMarshaller<object>.ConvertToManaged((void*)own));
            Assert.AreSame(plain, ComObjectMarshaller<object>.ConvertToManaged((void*)own));

            object? unique = UniqueComObjectMarshaller<object>.ConvertToManaged((void*)foreign);
            Assert.IsInstanceOfType<ComObject>(unique);
            Assert.IsInstanceOfType<IProviderObject>(unique);
            Assert.IsInstanceOfType<ComObject>(ComObjectMarshaller<object>.ConvertToManaged((void*)foreign));
            ComMarshalling.Release(unique);
        }
        finally
        {
            Marshal.Release(own);
            Marshal.Release(foreign);
        }
    }

    [TestMethod]
    public void AWrapperIsUnwrappedOnce()
    {
        // A wrapper that is not exposed to COM and provides itself is unwrapped once: its object is converted itself,
        // rather than unwrapped again and again.
        var dispatch = new SelfDispatch();
        void* pointer = ComObjectMarshaller<object>.ConvertToUnmanaged(dispatch);
        try
        {
            Assert.AreSame(dispatch, ComObjectMarshaller<object>.ConvertToManaged(pointer));
        }
        finally
        {
            ComObjectMarshaller<object>.Free(pointer);
        }

        // Also where an IDispatch is required: the Dispatch of a wrapper is converted itself, also when it is another
        // wrapper that is not exposed to COM, whose interface has no IDispatch, although that wrapper alone is passed as
        // its COM object there.
        var inner = new DispatchingWrapper(ComActivator.CreateInstance<IDispatch>(Dictionary));
        Assert.ThrowsExactly<InvalidCastException>(() => VariantMarshaller.ConvertToUnmanaged(new PlainWrapper(inner)));
        Assert.ThrowsExactly<InvalidCastException>(
            () => DispatchWrapperMarshaller<MemoryLeakTests.Wrapper>.ConvertToUnmanaged(new MemoryLeakTests.Wrapper(inner)));
        ComVariant variant = VariantMarshaller.ConvertToUnmanaged(inner);
        try
        {
            Assert.AreSame(inner.Dispatch, VariantMarshaller.ConvertToManaged(variant));
        }
        finally
        {
            variant.Dispose();
        }
    }

    [TestMethod]
    public void ALateBoundWrapperIsPassedAsItsComObjectForAnInterfaceThatItImplements()
    {
        // A wrapper of the application that is not exposed to COM stands for the COM object of its Dispatch also in a
        // parameter of a COM interface that it implements itself, e.g. IDispatch by forwarding, whose COM interface would
        // only have IUnknown: the COM object is asked for the interface, in a shared and in a unique conversion alike.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        var wrapper = new DispatchingWrapper(dictionary);
        void* shared = ComObjectMarshaller<IDispatch>.ConvertToUnmanaged(wrapper);
        void* unique = UniqueComObjectMarshaller<IDispatch>.ConvertToUnmanaged(wrapper);
        try
        {
            Assert.AreSame(dictionary, ComObjectMarshaller<IDispatch>.ConvertToManaged(shared));
            Assert.AreSame(dictionary, ComObjectMarshaller<IDispatch>.ConvertToManaged(unique));
        }
        finally
        {
            ComObjectMarshaller<IDispatch>.Free(shared);
            UniqueComObjectMarshaller<IDispatch>.Free(unique);
        }

        // An object of the application that is exposed to COM, which the wrapper wraps, is passed itself, with its rule.
        var exposed = new ExposedWrapper(dictionary);
        void* pointer = ComObjectMarshaller<IDispatch>.ConvertToUnmanaged(new DispatchingWrapper(exposed));
        try
        {
            Assert.AreSame(exposed, ComObjectMarshaller<IDispatch>.ConvertToManaged(pointer));
        }
        finally
        {
            ComObjectMarshaller<IDispatch>.Free(pointer);
        }

        // A wrapper without a COM object, whose Dispatch breaks its contract, stands for no object, and a COM object that
        // does not implement the interface is rejected.
        Assert.IsTrue(ComObjectMarshaller<IDispatch>.ConvertToUnmanaged(new DispatchingWrapper(null!)) is null);
        var exception = Assert.ThrowsExactly<InvalidCastException>(
            () => ComObjectMarshaller<IProviderObject>.ConvertToUnmanaged(new ProviderWrapper(dictionary)));
        StringAssert.Contains(exception.Message, "its Dispatch");
    }

    [TestMethod]
    public void AParameterFallsBackToTheDispatchThatTheObjectOfAWrapperProvides()
    {
        // The Dispatch of a wrapper stands for its object with the rule of QueryInterface, also in a parameter of a COM
        // interface that the wrapper implements: an object of the application that is exposed to COM and does not
        // implement the interface, e.g. a decorator, stands for the IDispatch that it provides, as when a late-bound wrapper
        // converts it to the interface. The inner object is called through a COM object of another ComWrappers.
        var inner = new DispatchingProviderObject();
        var wrappers = new StrategyBasedComWrappers();
        nint unknown = wrappers.GetOrCreateComInterfaceForObject(inner, CreateComInterfaceFlags.None);
        var comObject = (IDispatch)wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        Marshal.Release(unknown);
        var decorator = new DispatchingProvider(comObject);

        void* pointer = ComObjectMarshaller<IProviderObject>.ConvertToUnmanaged(new ProviderWrapper(decorator));
        try
        {
            ComObjectMarshaller<IProviderObject>.ConvertToManaged(pointer)!.Run();
            Assert.AreEqual(1, inner.Runs);
        }
        finally
        {
            ComObjectMarshaller<IProviderObject>.Free(pointer);
        }

        Conversions.ToInterface<IProviderObject>(new PlainWrapper(decorator))!.Run();
        Assert.AreEqual(2, inner.Runs);
        ComMarshalling.Release(comObject);
    }

    [TestMethod]
    public void AConversionCallsAnExposedObjectThroughComForAnotherDeclarationOfItsInterface()
    {
        // The object of the application comes back from a VARIANT or a SAFEARRAY itself. Converted to another declaration
        // of its interface, e.g. of another interop assembly, it is asked through its COM interface, as its pointer is: it
        // gets a COM object of its own, which calls it through COM, in a late-bound wrapper, an array, a SAFEARRAY and an
        // event argument alike, also when a late-bound wrapper wraps it. The interface of its class converts to it.
        var provider = new ProviderObject(ComActivator.CreateInstance<IDispatch>(Dictionary));
        int runs = 0;
        void Run(IProviderObjectCopy? copy)
        {
            Assert.IsInstanceOfType<ComObject>(copy);
            copy.Run();
            Assert.AreEqual(++runs, provider.Runs);
            ComMarshalling.Release(copy);
        }

        Run(Conversions.ToInterface<IProviderObjectCopy>(provider));
        Run(Conversions.ToArray<IProviderObjectCopy>(new object[] { provider })![0]);

        nint psa = SafeArrayMarshaller<IProviderObject>.ConvertToUnmanaged([provider]);
        try
        {
            Run(SafeArrayMarshaller<IProviderObjectCopy>.ConvertToManaged(psa)![0]);
        }
        finally
        {
            SafeArrayMarshaller<IProviderObject>.Free(psa);
        }

        var variants = stackalloc ComVariant[1];
        variants[0] = VariantMarshaller.ConvertToUnmanaged(provider);
        try
        {
            var parameters = new DispParams { rgvarg = variants, cArgs = 1 };
            var arguments = new EventArguments(&parameters, null);
            Run(arguments.Get<IProviderObjectCopy>(0));
            Run(arguments.GetUnique<IProviderObjectCopy>(0));
            Assert.AreSame(provider, arguments.Get<IProviderObject>(0));
        }
        finally
        {
            variants[0].Dispose();
        }

        var dispatching = new DispatchingProviderObject();
        var wrapped = Conversions.ToInterface<IProviderObjectCopy>(new PlainWrapper(dispatching));
        Assert.IsInstanceOfType<ComObject>(wrapped);
        wrapped.Run();
        Assert.AreEqual(1, dispatching.Runs);
        ComMarshalling.Release(wrapped);
    }

    [TestMethod]
    public void AFailedQueryInterfaceThrowsItsError()
    {
        // A COM object that fails QueryInterface with another error than E_NOINTERFACE, e.g. a proxy of a server that has
        // ended, throws the exception of the HRESULT rather than saying that it does not implement the interface: as an
        // enumerator, whose type test does not tell the error, and as the COM object of a late-bound wrapper.
        const int RPC_E_DISCONNECTED = unchecked((int)0x80010108);
        const int E_NOINTERFACE = unchecked((int)0x80004002);

        object disconnected = FailingComObject.Create(RPC_E_DISCONNECTED);
        Assert.AreEqual(RPC_E_DISCONNECTED, Assert.ThrowsExactly<COMException>(() => ComEnumerator.FromObject(disconnected, ownsEnumerator: true)).HResult);
        Assert.ThrowsExactly<InvalidCastException>(() => ComEnumerator.FromObject(FailingComObject.Create(E_NOINTERFACE), ownsEnumerator: true));

        var dispatch = (IDispatch)FailingComObject.Create(RPC_E_DISCONNECTED, dispatch: true);
        Assert.AreEqual(
            RPC_E_DISCONNECTED,
            Assert.ThrowsExactly<COMException>(() => ComObjectMarshaller<IProviderObject>.ConvertToUnmanaged(new ProviderWrapper(dispatch))).HResult);
        ComMarshalling.Release(dispatch);

        dispatch = (IDispatch)FailingComObject.Create(E_NOINTERFACE, dispatch: true);
        var exception = Assert.ThrowsExactly<InvalidCastException>(
            () => ComObjectMarshaller<IProviderObject>.ConvertToUnmanaged(new ProviderWrapper(dispatch)));
        StringAssert.Contains(exception.Message, "its Dispatch");
        ComMarshalling.Release(dispatch);
    }

    [TestMethod]
    public void AnObjectWhoseStrategyDoesNotKnowTheInterfaceIsMarshalled()
    {
        // The object of a ComWrappers with a strategy of its own for the details of interfaces, which knows IDispatch only:
        // the object has no pointer for another interface, also not for the IUnknown of the support code, without failing.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            object selective = new SelectiveComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);

            nint pointer = (nint)ComObjectMarshaller<object>.ConvertToUnmanaged(selective);
            Assert.AreEqual(unknown, pointer);
            Marshal.Release(pointer);

            ComVariant variant = VariantMarshaller.ConvertToUnmanaged(selective);
            Assert.AreEqual(VarEnum.VT_DISPATCH, variant.VarType);
            VariantMarshaller.Free(variant);

            nint dispatch = (nint)ComObjectMarshaller<IDispatch>.ConvertToUnmanaged((IDispatch)selective);
            Assert.AreNotEqual(0, dispatch);
            Marshal.Release(dispatch);

            ComMarshalling.Release(selective);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void AnOwnedEnumeratorIsReleasedWhenItCannotBeAskedForTheInterface()
    {
        // The strategy of the ComWrappers fails when the object is asked for IEnumVARIANT: the object that the enumerator
        // owns is released all the same.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            int references = CountReferences(unknown);
            object owned = new ThrowingComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Assert.IsTrue(CountReferences(unknown) > references);

            Assert.ThrowsExactly<NotSupportedException>(() => ComEnumerator.FromObject(owned, ownsEnumerator: true));
            Assert.AreEqual(references, CountReferences(unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    private sealed class ThrowingComWrappers : StrategyBasedComWrappers
    {
        protected override IIUnknownInterfaceDetailsStrategy GetOrCreateInterfaceDetailsStrategy() => new Strategy();

        private sealed class Strategy : IIUnknownInterfaceDetailsStrategy
        {
            public IComExposedDetails? GetComExposedTypeDetails(RuntimeTypeHandle type) =>
                DefaultIUnknownInterfaceDetailsStrategy.GetComExposedTypeDetails(type);

            public IIUnknownDerivedDetails? GetIUnknownDerivedDetails(RuntimeTypeHandle type) =>
                type.Equals(typeof(IEnumVARIANT).TypeHandle)
                    ? throw new NotSupportedException("The strategy fails.")
                    : DefaultIUnknownInterfaceDetailsStrategy.GetIUnknownDerivedDetails(type);
        }
    }

    // A ComWrappers whose strategy knows IDispatch only, or no interface at all.
    private sealed class SelectiveComWrappers(bool knowsDispatch = true) : StrategyBasedComWrappers
    {
        protected override IIUnknownInterfaceDetailsStrategy GetOrCreateInterfaceDetailsStrategy() => new Strategy(knowsDispatch);

        private sealed class Strategy(bool knowsDispatch) : IIUnknownInterfaceDetailsStrategy
        {
            public IComExposedDetails? GetComExposedTypeDetails(RuntimeTypeHandle type) =>
                DefaultIUnknownInterfaceDetailsStrategy.GetComExposedTypeDetails(type);

            public IIUnknownDerivedDetails? GetIUnknownDerivedDetails(RuntimeTypeHandle type) =>
                knowsDispatch && type.Equals(typeof(IDispatch).TypeHandle) ? DefaultIUnknownInterfaceDetailsStrategy.GetIUnknownDerivedDetails(type) : null;
        }
    }

    [TestMethod]
    public void AnObjectWhoseStrategyDoesNotKnowIDispatchIsAskedForItWhereItIsRequired()
    {
        // The strategy of the ComWrappers of the object knows no interface, so the object is no IDispatch for .NET: where the
        // support code requires an IDispatch, as in a SAFEARRAY of IDispatch, it asks the object itself, before it rejects
        // it. A VARIANT trusts the type test, so that an object without IDispatch is asked once per conversion
        // (AComObjectIsAskedForIDispatchOnceForAllItsConversions): the object is passed as VT_UNKNOWN.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            object blind = new SelectiveComWrappers(knowsDispatch: false).GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            Assert.IsFalse(blind is IDispatch);

            ComVariant variant = VariantMarshaller.ConvertToUnmanaged(blind);
            try
            {
                Assert.AreEqual(VarEnum.VT_UNKNOWN, variant.VarType);
            }
            finally
            {
                VariantMarshaller.Free(variant);
            }

            using (var dispatches = ComSafeArray<object?>.Create(VarEnum.VT_DISPATCH, [1]))
            {
                dispatches[0] = blind;
                Assert.AreSame(dictionary, dispatches[0]);
            }

            ComMarshalling.Release(blind);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void AnEnumeratorWithoutTheInterfaceIsReleased()
    {
        // The object of a _NewEnum member that is no enumerator: nothing could be disposed to release it.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            int references = CountReferences(unknown);
            object unique = UniqueComObjectMarshaller<object>.ConvertToManaged((void*)unknown)!;
            Assert.IsTrue(CountReferences(unknown) > references);

            Assert.ThrowsExactly<InvalidCastException>(() => ComEnumerator.FromObject(unique, ownsEnumerator: true));

            Assert.AreEqual(references, CountReferences(unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void TheObjectOfAnOwnedWrapperIsReleased()
    {
        // A late-bound wrapper stands for its COM object, which an enumerator that owns the wrapper releases like
        // ComMarshalling.Release, also when it is no enumerator: the marshaller created both for a unique _NewEnum.
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            int references = CountReferences(unknown);
            var wrapper = UniqueDispatchWrapperMarshaller<MemoryLeakTests.Wrapper>.ConvertToManaged((void*)unknown)!;
            Assert.IsTrue(CountReferences(unknown) > references);

            var exception = Assert.ThrowsExactly<InvalidCastException>(() => ComEnumerator.FromObject(wrapper, ownsEnumerator: true));
            StringAssert.Contains(exception.Message, typeof(MemoryLeakTests.Wrapper).FullName);
            Assert.AreEqual(references, CountReferences(unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void AnEnumeratorEnumeratesTheObjectThatAWrapperStandsFor()
    {
        // The COM object of a late-bound wrapper, e.g. the one that a unique _NewEnum that returns a dispinterface creates
        // for the wrapper, is enumerated, and released when the enumerator that owns it is disposed.
        IDispatch unique = CreateUniqueObject(new ItemsEnumerator([1, 2, 3]));
        using (var enumerator = ComEnumerator.FromObject(new MemoryLeakTests.Wrapper(unique), ownsEnumerator: true))
        {
            CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, Drain(enumerator));
        }

        Assert.IsTrue(IsReleased(unique));

        // So is the object of an UnknownWrapper, as where an object is passed to COM.
        unique = CreateUniqueObject(new ItemsEnumerator([4]));
        using (var enumerator = ComEnumerator.FromObject(new UnknownWrapper(new MemoryLeakTests.Wrapper(unique)), ownsEnumerator: true))
        {
            CollectionAssert.AreEqual(new object[] { 4 }, Drain(enumerator));
        }

        Assert.IsTrue(IsReleased(unique));

        // Not owned, it is not released.
        unique = CreateUniqueObject(new ItemsEnumerator([5]));
        ComEnumerator.FromObject(new MemoryLeakTests.Wrapper(unique)).Dispose();
        Assert.IsFalse(IsReleased(unique));
        ComMarshalling.Release(unique);
    }

    [TestMethod]
    public unsafe void AnEnumeratorOfTheApplicationIsNotReleased()
    {
        // A collection returns the pointer of a late-bound wrapper of the application that is exposed to COM, which the
        // unique marshaller converts back to the wrapper itself: the enumerator, which owns what the marshaller created,
        // enumerates the COM object that the wrapper provides, and leaves it to the wrapper.
        IDispatch provided = CreateUniqueObject(new ItemsEnumerator([1, 2]));
        var exposed = new ExposedWrapper(provided);
        void* pointer = DispatchWrapperMarshaller<ExposedWrapper>.ConvertToUnmanaged(exposed);
        try
        {
            var returned = UniqueDispatchWrapperMarshaller<ExposedWrapper>.ConvertToManaged(pointer)!;
            Assert.AreSame(exposed, returned);
            using (var enumerator = ComEnumerator.FromObject(returned, ownsEnumerator: true))
            {
                CollectionAssert.AreEqual(new object[] { 1, 2 }, Drain(enumerator));
            }
        }
        finally
        {
            DispatchWrapperMarshaller<ExposedWrapper>.Free(pointer);
        }

        Assert.IsFalse(IsReleased(provided));
        ComMarshalling.Release(provided);

        // An exposed object that is an enumerator itself is enumerated itself.
        var items = new ItemsEnumerator([3]);
        using (var enumerator = ComEnumerator.FromObject(items, ownsEnumerator: true))
        {
            CollectionAssert.AreEqual(new object[] { 3 }, Drain(enumerator));
        }

        Assert.AreEqual(0, items.Reset());
    }

    [TestMethod]
    public void AWrapperOfAnExposedObjectStandsForItWithItsRule()
    {
        // A late-bound wrapper stands for the COM object of its Dispatch, which is the object itself when it is an object
        // of the application that is exposed to COM, e.g. the one that a dispinterface wrapper makes of a decorator that a
        // call returns: where an interface is required that the object does not implement, the IDispatch that it provides
        // is asked for it, as for the object itself, and an enumerator that owns the wrapper leaves that IDispatch alone.
        IDispatch provided = CreateUniqueObject(new ItemsEnumerator([1, 2]));
        var exposed = new ExposedWrapper(provided);
        var wrapper = new PlainWrapper(exposed);
        Assert.AreSame<object>(provided, Conversions.ToInterface<IEnumVARIANT>(exposed));
        Assert.AreSame<object>(provided, Conversions.ToInterface<IEnumVARIANT>(wrapper));
        Assert.AreSame(exposed, Conversions.ToInterface<IDispatch>(wrapper));

        using (var enumerator = ComEnumerator.FromObject(wrapper, ownsEnumerator: true))
        {
            CollectionAssert.AreEqual(new object[] { 1, 2 }, Drain(enumerator));
        }

        Assert.IsFalse(IsReleased(provided));
        ComMarshalling.Release(provided);
    }

    [TestMethod]
    public void AnEnumeratorOfNoObjectIsRejected()
    {
        // A wrapper without a COM object, and an UnknownWrapper of null, stand for no object, like null; an exposed object
        // whose Dispatch is null provides none, and a DispatchWrapper is rejected, as where an object is passed to COM.
        Assert.ThrowsExactly<InvalidOperationException>(() => ComEnumerator.FromObject(new PlainWrapper(null!), ownsEnumerator: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ComEnumerator.FromObject(new UnknownWrapper(null)));
        Assert.ThrowsExactly<InvalidCastException>(() => ComEnumerator.FromObject(new ProviderObject(null!), ownsEnumerator: true));
        Assert.AreEqual("enumerator", Assert.ThrowsExactly<ArgumentException>(() => ComEnumerator.FromObject(new DispatchWrapper(null))).ParamName);
        Assert.AreEqual("enumerator", Assert.ThrowsExactly<ArgumentException>(() => ComEnumerator.FromObject(new UnknownWrapper(new DispatchWrapper(null)))).ParamName);
    }

    // A unique COM object that calls a managed object through COM, from the interface of another ComWrappers, like the
    // COM object of another process.
    private static IDispatch CreateUniqueObject(object managed)
    {
        nint unknown = new StrategyBasedComWrappers().GetOrCreateComInterfaceForObject(managed, CreateComInterfaceFlags.None);
        try
        {
            IDispatch unique = UniqueComObjectMarshaller<IDispatch>.ConvertToManaged((void*)unknown)!;
            Assert.IsInstanceOfType<ComObject>(unique);
            return unique;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static List<object?> Drain(ComEnumerator enumerator)
    {
        var items = new List<object?>();
        while (enumerator.MoveNext())
        {
            items.Add(enumerator.Current);
        }

        return items;
    }

    // A released object cannot be passed to COM any more.
    private static bool IsReleased(object value)
    {
        try
        {
            ComObjectMarshaller<object>.Free(ComObjectMarshaller<object>.ConvertToUnmanaged(value));
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [TestMethod]
    public void AUniqueInstanceWithoutTheInterfaceIsReleased()
    {
        var dictionary = ComActivator.CreateInstance<IDispatch>(Dictionary);
        Assert.IsTrue(ComWrappers.TryGetComInstance(dictionary, out nint unknown));
        try
        {
            int references = CountReferences(unknown);

            Assert.ThrowsExactly<InvalidCastException>(() => UniqueComObjectMarshaller<IConnectionPoint>.ConvertToManaged((void*)unknown));

            // The object that was created for the pointer does not hold it until it is garbage collected.
            Assert.AreEqual(references, CountReferences(unknown));

            // Neither does the object of a late-bound wrapper that cannot be created.
            Assert.ThrowsExactly<InvalidOperationException>(() => UniqueDispatchWrapperMarshaller<FailingWrapper>.ConvertToManaged((void*)unknown));
            Assert.AreEqual(references, CountReferences(unknown));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        GC.KeepAlive(dictionary);
    }

    [TestMethod]
    public void EventConnectionsDoNotKeepAUniqueSourceAlive()
    {
        // The DOM document of MSXML raises events through a connection point.
        var document = ComActivator.CreateInstance<IDispatch>("Msxml2.DOMDocument.6.0", unique: true);
        Assert.IsTrue(ComWrappers.TryGetComInstance(document, out nint unknown));

        var connection = EventConnection.AdviseDispatch(document, new Guid("3EFAA427-272F-11D2-836F-0000F87A7782"), new IgnoringHandler());
        Assert.AreEqual(0, connection.Disconnect());
        ((ComObject)(object)document).FinalRelease();

        // Neither the connection point nor its container holds a reference any more: that of the test is the last one.
        Assert.AreEqual(0, Marshal.Release(unknown));
    }

    private static int CountReferences(nint unknown)
    {
        Marshal.AddRef(unknown);
        return Marshal.Release(unknown);
    }

    private sealed class IgnoringHandler : IDispatchEventHandler
    {
        public bool Invoke(int dispId, EventArguments arguments) => true;
    }

    /// <summary>Exposes the conversions of the generated late-bound wrappers.</summary>
    private abstract class Conversions(IDispatch dispatch) : DispatchObject(dispatch)
    {
        public static new T? ToInterface<T>(object? value) where T : class => DispatchObject.ToInterface<T>(value);

        public static new T? ToWrapper<T>(object? value) where T : class, IDispatchWrapper<T> => DispatchObject.ToWrapper<T>(value);

        public static new T[]? ToArray<T>(object? value) => DispatchObject.ToArray<T>(value);
    }

    [TestMethod]
    public void TheComWrappersCannotBeChangedAfterTheirUse()
    {
        // The tests of the process use the default instance of .NET; a custom instance is tested by the integration tests,
        // which run in processes of their own.
        _ = ComActivator.CreateInstance<IDispatch>(Dictionary);

        Assert.IsNull(ComMarshalling.ComWrappers);
        ComMarshalling.ComWrappers = null;
        Assert.ThrowsExactly<InvalidOperationException>(() => ComMarshalling.ComWrappers = new StrategyBasedComWrappers());
    }
}

/// <summary>A late-bound wrapper of your own, which only implements <see cref="IDispatchWrapper{TSelf}"/>.</summary>
internal sealed class HandWrittenWrapper(IDispatch dispatch) : IDispatchWrapper<HandWrittenWrapper>
{
    public IDispatch Dispatch => dispatch;

    public static HandWrittenWrapper Create(IDispatch dispatch) => new(dispatch);
}

/// <summary>A late-bound wrapper that cannot be created for an object.</summary>
internal sealed class FailingWrapper(IDispatch dispatch) : IDispatchWrapper<FailingWrapper>
{
    public IDispatch Dispatch => dispatch;

    public static FailingWrapper Create(IDispatch dispatch) => throw new InvalidOperationException("Failed.");
}

[GeneratedComInterface]
[Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A10F2")]
internal partial interface IProviderObject
{
    void Run();
}

/// <summary>Another declaration of <see cref="IProviderObject"/>, e.g. of another interop assembly.</summary>
[GeneratedComInterface]
[Guid("6B1F2D2A-4C1E-4B71-9D0B-7B7C2E0A10F2")]
internal partial interface IProviderObjectCopy
{
    void Run();
}

/// <summary>A managed object that is exposed to COM and wraps a COM object.</summary>
[GeneratedComClass]
internal sealed partial class ProviderObject(IDispatch dispatch) : IProviderObject, IDispatchProvider
{
    private int _runs;

    public IDispatch Dispatch => dispatch;

    public int Runs => Volatile.Read(ref _runs);

    public void Run() => Interlocked.Increment(ref _runs);
}

/// <summary>
/// A late-bound wrapper of the application that is exposed to COM and implements <c>IDispatch</c> itself, without type
/// information, e.g. a decorator.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ExposedWrapper(IDispatch dispatch) : IDispatch, IDispatchWrapper<ExposedWrapper>
{
    public IDispatch Dispatch => dispatch;

    public static ExposedWrapper Create(IDispatch dispatch) => new(dispatch);

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

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        unchecked((int)0x80004001);
}

/// <summary>
/// An enumerator of the application that is exposed to COM and implements <c>IDispatch</c>, as the object that a
/// <c>_NewEnum</c> returns as a dispinterface does.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ItemsEnumerator(int[] items) : IDispatch, IEnumVARIANT
{
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private int _index;

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

    public int GetIDsOfNames(in Guid riid, char** rgszNames, uint cNames, uint lcid, int* rgDispId) => E_NOTIMPL;

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        E_NOTIMPL;

    public int Next(uint celt, ComVariant* rgVar, uint* pCeltFetched)
    {
        uint fetched = 0;
        while (fetched < celt && _index < items.Length)
        {
            rgVar[fetched++] = ComVariant.Create(items[_index++]);
        }

        if (pCeltFetched is not null)
        {
            *pCeltFetched = fetched;
        }

        return fetched == celt ? 0 : 1;
    }

    public int Skip(uint celt)
    {
        _index = (int)Math.Min(items.Length, _index + celt);
        return _index == items.Length ? 1 : 0;
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

/// <summary>
/// A managed object that is exposed to COM, and counts the requests for an interface that it does not implement.
/// </summary>
[GeneratedComClass]
internal sealed partial class QueryCountingObject(Guid counted) : IProviderObject, ICustomQueryInterface
{
    private int _queries;

    public int Queries => Volatile.Read(ref _queries);

    public void Run()
    {
    }

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
    {
        if (iid == counted)
        {
            Interlocked.Increment(ref _queries);
        }

        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }
}

/// <summary>
/// A managed object that is exposed to COM, implements <c>IDispatch</c>, and counts the requests for it.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class DispatchCountingObject : IDispatch, ICustomQueryInterface
{
    private static readonly Guid DispatchIid = new("00020400-0000-0000-C000-000000000046");
    private int _queries;

    public int Queries => Volatile.Read(ref _queries);

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

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        unchecked((int)0x80004001);

    public CustomQueryInterfaceResult GetInterface(ref Guid iid, out nint ppv)
    {
        if (iid == DispatchIid)
        {
            Interlocked.Increment(ref _queries);
        }

        ppv = 0;
        return CustomQueryInterfaceResult.NotHandled;
    }
}

/// <summary>A late-bound wrapper, which is not exposed to COM itself.</summary>
internal sealed class PlainWrapper(IDispatch dispatch) : IDispatchProvider
{
    public IDispatch Dispatch => dispatch;
}

/// <summary>
/// A managed <c>IDispatch</c> that is its own <see cref="IDispatchProvider.Dispatch"/>, and is no <see cref="GeneratedComClassAttribute"/>:
/// it is unwrapped as a late-bound wrapper.
/// </summary>
internal sealed unsafe class SelfDispatch : IDispatch, IDispatchProvider
{
    public IDispatch Dispatch => this;

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

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        unchecked((int)0x80004001);
}

/// <summary>
/// A late-bound wrapper of the application that implements <c>IDispatch</c> itself, e.g. by forwarding to its
/// <see cref="IDispatchProvider.Dispatch"/>, and is no <see cref="GeneratedComClassAttribute"/>: it is not exposed to
/// COM.
/// </summary>
internal sealed unsafe class DispatchingWrapper(IDispatch dispatch) : IDispatch, IDispatchProvider
{
    public IDispatch Dispatch => dispatch;

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

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        unchecked((int)0x80004001);
}

/// <summary>
/// A late-bound wrapper of the application that implements a COM interface itself, and is no
/// <see cref="GeneratedComClassAttribute"/>: it is not exposed to COM.
/// </summary>
internal sealed class ProviderWrapper(IDispatch dispatch) : IProviderObject, IDispatchProvider
{
    public IDispatch Dispatch => dispatch;

    public void Run()
    {
    }
}

/// <summary>
/// A managed object that is exposed to COM, implements <c>IDispatch</c> itself, without type information, and wraps a COM
/// object, e.g. a decorator.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class DispatchingProvider(IDispatch dispatch) : IDispatch, IDispatchProvider
{
    public IDispatch Dispatch => dispatch;

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

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        unchecked((int)0x80004001);
}

/// <summary>
/// A managed object that is exposed to COM, implements <c>IDispatch</c> itself, without type information, and
/// <see cref="IProviderObject"/>, and counts its runs.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class DispatchingProviderObject : IDispatch, IProviderObject
{
    private int _runs;

    public int Runs => Volatile.Read(ref _runs);

    public void Run() => Interlocked.Increment(ref _runs);

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

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        unchecked((int)0x80004001);
}

/// <summary>
/// A late-bound wrapper of the application that derives from <see cref="DispatchObject"/>, is exposed to COM and
/// implements <c>IDispatch</c> itself, without type information, e.g. a decorator.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ExposedDispatchObject(IDispatch dispatch) : DispatchObject(dispatch), IDispatch
{
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

    public int Invoke(int dispIdMember, in Guid riid, uint lcid, ushort wFlags, DispParams* pDispParams, ComVariant* pVarResult, ExcepInfo* pExcepInfo, uint* puArgErr) =>
        unchecked((int)0x80004001);
}
