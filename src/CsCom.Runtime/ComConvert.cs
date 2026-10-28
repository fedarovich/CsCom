using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace CsCom.Runtime;

/// <summary>
/// Converts the values returned by <c>IDispatch::Invoke</c> or received in <c>VARIANT</c>s to the types of generated signatures.
/// </summary>
internal static class ComConvert
{
    /// <summary>Converts a value to a late-bound wrapper.</summary>
    public static T? ToWrapper<T>(object? value) where T : class, IDispatchWrapper<T>
    {
        return value switch
        {
            null => null,
            T wrapper => wrapper,
            IDispatch dispatch => T.Create(dispatch),
            _ => throw new InvalidCastException($"The value of type {value.GetType()} does not implement IDispatch."),
        };
    }

    /// <summary>Converts a value to a COM interface.</summary>
    public static T? ToInterface<T>(object? value) where T : class
    {
        return value switch
        {
            null => null,
            IDispatchProvider provider => (T)(object)provider.Dispatch,
            _ => (T)value,
        };
    }

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
            case T typed:
                return typed;
            case IDispatchProvider provider when typeof(T).IsInterface:
                return (T)(object)provider.Dispatch;
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

    /// <summary>Converts a one-dimensional array to an array of <typeparamref name="T"/>, converting the elements with <see cref="To{T}"/>.</summary>
    public static T[]? ToArray<T>(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case T[] typed:
                return typed;
            case Array { Rank: 1 } array:
            {
                var result = new T[array.Length];
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

    // Only valid when TFrom and TTo are the same type.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TTo Reinterpret<TFrom, TTo>(TFrom value) => Unsafe.As<TFrom, TTo>(ref value);
}
