using System;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Marshals COM interfaces with one managed object per COM object, like <see cref="ComInterfaceMarshaller{T}"/>, and with
/// the <see cref="StrategyBasedComWrappers"/> of <see cref="ComMarshalling.ComWrappers"/> when it is set. The pointer of a
/// managed object that is exposed to COM is converted to that object itself when it implements the interface, and
/// otherwise to a COM object of its own, which calls it through COM, e.g. when its class implements another declaration
/// of the interface: <see cref="ComInterfaceMarshaller{T}"/> throws an <see cref="InvalidCastException"/> then. Such a
/// COM object is created for every conversion, like a unique object: it is not shared, so two conversions of the
/// pointer give two objects, and <see cref="ComMarshalling.Release"/> releases it.
/// </summary>
/// <typeparam name="T">The interface type, or <see cref="object"/> for <c>IUnknown</c>.</typeparam>
[CustomMarshaller(typeof(CustomMarshallerAttribute.GenericPlaceholder), MarshalMode.Default, typeof(ComObjectMarshaller<>))]
public static unsafe class ComObjectMarshaller<T>
{
    /// <summary>Converts a managed object to an interface pointer.</summary>
    public static void* ConvertToUnmanaged(T? managed) => ComMarshalling.ToUnmanaged(managed);

    /// <summary>Converts an interface pointer to the managed object of the COM object.</summary>
    public static T? ConvertToManaged(void* unmanaged) => ComMarshalling.ToManaged<T>(unmanaged);

    /// <summary>Releases the interface pointer.</summary>
    public static void Free(void* unmanaged) => ComMarshalling.Free(unmanaged);
}

/// <summary>
/// Marshals COM interfaces with a managed object of its own for every interface pointer, like
/// <see cref="UniqueComInterfaceMarshaller{T}"/>, and with the <see cref="StrategyBasedComWrappers"/> of
/// <see cref="ComMarshalling.ComWrappers"/> when it is set. The object belongs to its receiver:
/// <see cref="ComMarshalling.Release"/> releases it at once, and it is released when it is garbage collected otherwise.
/// The pointer of a managed object that is exposed to COM is converted to that object itself when it implements the
/// interface, which has nothing to release, and otherwise to a COM object of its own, which calls it through COM.
/// </summary>
/// <typeparam name="T">The interface type, or <see cref="object"/> for <c>IUnknown</c>.</typeparam>
[CustomMarshaller(typeof(CustomMarshallerAttribute.GenericPlaceholder), MarshalMode.Default, typeof(UniqueComObjectMarshaller<>))]
public static unsafe class UniqueComObjectMarshaller<T>
{
    /// <summary>Converts a managed object to an interface pointer.</summary>
    public static void* ConvertToUnmanaged(T? managed) => ComMarshalling.ToUnmanaged(managed);

    /// <summary>Converts an interface pointer to a new managed object.</summary>
    public static T? ConvertToManaged(void* unmanaged) => ComMarshalling.ToManaged<T>(unmanaged, unique: true);

    /// <summary>Releases the interface pointer.</summary>
    public static void Free(void* unmanaged) => ComMarshalling.Free(unmanaged);
}
