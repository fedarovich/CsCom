using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Converts the values returned by <c>IDispatch::Invoke</c> or received in <c>VARIANT</c>s to the types of generated signatures.
/// </summary>
internal static class ComConvert
{
    /// <summary>Converts a value to a late-bound wrapper.</summary>
    public static T? ToWrapper<T>(object? value) where T : class, IDispatchWrapper<T>
    {
        // The rule of IDispatchProvider where an IDispatch is required: a late-bound wrapper stands for its COM object, or
        // for no object without one, and a managed object that is exposed to COM, which a pointer converts back to, is
        // wrapped itself when it implements IDispatch, and otherwise through the IDispatch that it provides, e.g. a
        // decorator of a COM object; one that provides none does not implement IDispatch. The wrapper calls the managed
        // IDispatch, so its type is tested, rather than the COM interface of the object. An IDispatch that is a T itself,
        // e.g. a wrapper of the application that is exposed to COM, is returned like by the marshaller of its pointer.
        return value switch
        {
            null => null,
            T wrapper => wrapper,
            _ when ComMarshalling.IsLateBoundWrapper(value, out IDispatch? wrapped) => wrapped is null ? null : DispatchWrapperMarshaller<T>.Wrap(wrapped),
            IDispatch dispatch => T.Create(dispatch),
            _ when ComMarshalling.TryGetProvidedDispatch(value, out IDispatch? provided) => DispatchWrapperMarshaller<T>.Wrap(provided),
            _ => throw new InvalidCastException($"The value of type {value.GetType()} does not implement IDispatch."),
        };
    }

    /// <summary>Converts a value to a COM interface.</summary>
    public static T? ToInterface<T>(object? value) where T : class => To<T?>(value);

    /// <summary>
    /// Converts a value to <typeparamref name="T"/>: <see langword="null"/> to the default value, numbers to enums and to other
    /// numeric types, and <see cref="DateTime"/>, <see cref="decimal"/> and <see cref="bool"/> to the COM value wrappers.
    /// </summary>
    public static T To<T>(object? value)
    {
        switch (value)
        {
            case null:
                return default!;

            // A COM object is asked for the interface once, by the cast: the type test below would ask it, and the cast at
            // the end again when it does not implement the interface, which can be a call to another process. It is no
            // IDispatchProvider either, which its strategy would look up on every call.
            case ComObject when typeof(T).IsInterface:
                return (T)value;
            case T typed:
                return typed;
        }

        // The rule of IDispatchProvider for an interface (ToInterface is this method too): a value that implements it is
        // itself, also a managed object that is exposed to COM, which a pointer converts back to. Otherwise a late-bound
        // wrapper stands for the COM object of its Dispatch, or for no object without one, with the rule of what that is.
        // An object that is exposed to COM, also in a late-bound wrapper, is asked through its COM interface then, as its
        // pointer would be, e.g. for another declaration of the interface, of another interop assembly, which gives a COM
        // object of its own, and else through the IDispatch that it provides, whose COM object is asked for the interface:
        // the order of ComMarshalling.QueryInterface. One that provides none does not implement the interface, which the
        // cast at the end throws for.
        if (typeof(T).IsInterface)
        {
            if (ComMarshalling.IsLateBoundWrapper(value, out IDispatch? wrapped))
            {
                if (wrapped is null)
                {
                    return default!;
                }

                // A COM object is asked for the interface once, by the cast, as above.
                value = wrapped;
                if (value is ComObject or T)
                {
                    return (T)value;
                }
            }

            if (ComMarshalling.TryConvertExposedObject(value, out T? converted))
            {
                return converted;
            }

            if (ComMarshalling.TryGetProvidedDispatch(value, out IDispatch? provided))
            {
                return (T)(object)provided;
            }
        }

        // T is checked to be exactly the converted type, so the value is reinterpreted instead of being boxed and unboxed.
        if (typeof(T) == typeof(ComDate))
        {
            return Reinterpret<ComDate, T>((ComDate)Convert.ToDateTime(value, CultureInfo.InvariantCulture));
        }

        if (typeof(T) == typeof(ComCurrency))
        {
            return Reinterpret<ComCurrency, T>((ComCurrency)Convert.ToDecimal(value, CultureInfo.InvariantCulture));
        }

        if (typeof(T) == typeof(ComBool))
        {
            return Reinterpret<ComBool, T>((ComBool)Convert.ToBoolean(value, CultureInfo.InvariantCulture));
        }

        if (typeof(T) == typeof(nint))
        {
            return Reinterpret<nint, T>((nint)Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }

        if (typeof(T) == typeof(nuint))
        {
            return Reinterpret<nuint, T>((nuint)Convert.ToUInt64(value, CultureInfo.InvariantCulture));
        }

        Type type = typeof(T);
        if (type.IsEnum)
        {
            return (T)Enum.ToObject(type, value);
        }

        if (value is IConvertible && Type.GetTypeCode(type) != TypeCode.Object)
        {
            return (T)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        }

        return (T)value;
    }

    // The arrays are only returned as is when their type is exactly the requested one: the CLR also considers an int[] to be a
    // Color[] (an enum of int) and a string[] to be an object[], whose elements could then not be set to other objects. The
    // arrays of the underlying type of an enum, e.g. the int[] read from a SAFEARRAY(Color), are copied as a block.

    /// <summary>Converts a one-dimensional array to an array of <typeparamref name="T"/>, converting the elements with <see cref="To{T}"/>.</summary>
    public static T[]? ToArray<T>(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case T[] typed when typed.GetType() == typeof(T[]):
                return typed;
            case Array { Rank: 1 } array:
            {
                var result = new T[array.Length];
                if (TryCopyEnumArray<T>(array, result))
                {
                    return result;
                }

                int lowerBound = array.GetLowerBound(0);
                for (int i = 0; i < result.Length; i++)
                {
                    result[i] = To<T>(array.GetValue(lowerBound + i));
                }

                return result;
            }

            default:
                throw new InvalidCastException($"The value of type {value.GetType()} is not a one-dimensional array.");
        }
    }

    /// <summary>Converts a two-dimensional array to a zero-based array of <typeparamref name="T"/>, converting the elements with <see cref="To{T}"/>.</summary>
    public static T[,]? ToArray2D<T>(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case T[,] typed when typed.GetType() == typeof(T[,]) && typed.GetLowerBound(0) == 0 && typed.GetLowerBound(1) == 0:
                return typed;
            case Array { Rank: 2 } array:
            {
                var result = new T[array.GetLength(0), array.GetLength(1)];
                if (TryCopyEnumArray<T>(array, result))
                {
                    return result;
                }

                int lower0 = array.GetLowerBound(0);
                int lower1 = array.GetLowerBound(1);
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    for (int j = 0; j < result.GetLength(1); j++)
                    {
                        result[i, j] = To<T>(array.GetValue(lower0 + i, lower1 + j));
                    }
                }

                return result;
            }

            default:
                throw new InvalidCastException($"The value of type {value.GetType()} is not a two-dimensional array.");
        }
    }

    /// <summary>Converts a three-dimensional array to a zero-based array of <typeparamref name="T"/>, converting the elements with <see cref="To{T}"/>.</summary>
    public static T[,,]? ToArray3D<T>(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case T[,,] typed when typed.GetType() == typeof(T[,,]) && typed.GetLowerBound(0) == 0 && typed.GetLowerBound(1) == 0 && typed.GetLowerBound(2) == 0:
                return typed;
            case Array { Rank: 3 } array:
            {
                var result = new T[array.GetLength(0), array.GetLength(1), array.GetLength(2)];
                if (TryCopyEnumArray<T>(array, result))
                {
                    return result;
                }

                int lower0 = array.GetLowerBound(0);
                int lower1 = array.GetLowerBound(1);
                int lower2 = array.GetLowerBound(2);
                for (int i = 0; i < result.GetLength(0); i++)
                {
                    for (int j = 0; j < result.GetLength(1); j++)
                    {
                        for (int k = 0; k < result.GetLength(2); k++)
                        {
                            result[i, j, k] = To<T>(array.GetValue(lower0 + i, lower1 + j, lower2 + k));
                        }
                    }
                }

                return result;
            }

            default:
                throw new InvalidCastException($"The value of type {value.GetType()} is not a three-dimensional array.");
        }
    }

    // Copies an array of the underlying type of the enum T into the zero-based result of the same shape, without boxing the
    // elements. Array.Copy treats the arrays as one-dimensional, from their first elements, whatever their lower bounds.
    private static bool TryCopyEnumArray<T>(Array array, Array result)
    {
        if (!typeof(T).IsEnum || array.GetType().GetElementType() != Enum.GetUnderlyingType(typeof(T)))
        {
            return false;
        }

        Array.Copy(array, result, array.Length);
        return true;
    }

    // Only valid when TFrom and TTo are the same type.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TTo Reinterpret<TFrom, TTo>(TFrom value) => Unsafe.As<TFrom, TTo>(ref value);
}
