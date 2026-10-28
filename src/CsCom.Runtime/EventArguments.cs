using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CsCom.Runtime;

/// <summary>
/// Handles the events that a COM object raises through <c>IDispatch::Invoke</c>.
/// </summary>
public interface IDispatchEventHandler
{
    /// <summary>
    /// Handles an event.
    /// </summary>
    /// <param name="dispId">The dispatch identifier of the event.</param>
    /// <param name="arguments">The arguments and the result of the event.</param>
    /// <returns><see langword="false"/> if the event is unknown; the invocation then fails with <c>DISP_E_MEMBERNOTFOUND</c>.</returns>
    bool Invoke(int dispId, EventArguments arguments);
}

/// <summary>
/// The arguments of an invocation of <c>IDispatch::Invoke</c> (<c>DISPPARAMS</c>) and its result.
/// </summary>
/// <remarks>
/// The arguments are identified by their positions in the declaration of the member: the positional arguments, which
/// <c>DISPPARAMS</c> stores in reverse order, and the named arguments, whose dispatch identifiers are their positions.
/// Missing and omitted arguments read as <see langword="null"/> or the default value.
/// </remarks>
public readonly unsafe ref struct EventArguments
{
    private readonly DispParams* _parameters;
    private readonly ComVariant* _result;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventArguments"/> structure.
    /// </summary>
    /// <param name="parameters">The arguments; can be <see langword="null"/>.</param>
    /// <param name="result">The variable that receives the result; <see langword="null"/> if the caller expects none.</param>
    public EventArguments(DispParams* parameters, ComVariant* result)
    {
        _parameters = parameters;
        _result = result;
    }

    /// <summary>Gets the number of arguments.</summary>
    public int Count => _parameters is null ? 0 : (int)_parameters->cArgs;

    /// <summary>Gets an argument converted to a managed object, as <see cref="VariantMarshaller"/> converts it.</summary>
    /// <param name="index">The position of the argument in the declaration of the member.</param>
    public object? GetValue(int index)
    {
        ComVariant* variant = Find(index);
        if (variant is null || IsOmitted(variant))
        {
            return null;
        }

        if ((variant->VarType & VarEnum.VT_BYREF) == 0)
        {
            return VariantMarshaller.ConvertToManaged(*variant);
        }

        ComVariant value = default;
        Marshal.ThrowExceptionForHR(OleNativeMethods.VariantCopyInd(&value, variant));
        try
        {
            return VariantMarshaller.ConvertToManaged(value);
        }
        finally
        {
            value.Dispose();
        }
    }

    /// <summary>Gets an argument converted to <typeparamref name="T"/>.</summary>
    /// <param name="index">The position of the argument in the declaration of the member.</param>
    public T Get<T>(int index) => ComConvert.To<T>(GetValue(index));

    /// <summary>Gets an argument converted to a late-bound wrapper.</summary>
    /// <param name="index">The position of the argument in the declaration of the member.</param>
    public T? GetWrapper<T>(int index) where T : class, IDispatchWrapper<T> => ComConvert.ToWrapper<T>(GetValue(index));

    /// <summary>Gets an array argument converted to an array of <typeparamref name="T"/>.</summary>
    /// <param name="index">The position of the argument in the declaration of the member.</param>
    public T[]? GetArray<T>(int index) => ComConvert.ToArray<T>(GetValue(index));

    /// <summary>Gets a two-dimensional array argument converted to a zero-based array of <typeparamref name="T"/>.</summary>
    /// <param name="index">The position of the argument in the declaration of the member.</param>
    public T[,]? GetArray2D<T>(int index) => ComConvert.ToArray2D<T>(GetValue(index));

    /// <summary>Gets a three-dimensional array argument converted to a zero-based array of <typeparamref name="T"/>.</summary>
    /// <param name="index">The position of the argument in the declaration of the member.</param>
    public T[,,]? GetArray3D<T>(int index) => ComConvert.ToArray3D<T>(GetValue(index));

    /// <summary>
    /// Returns a new value through an argument passed by reference, converted to the type of the argument. Nothing happens
    /// if the argument is missing or was passed by value.
    /// </summary>
    /// <param name="index">The position of the argument in the declaration of the member.</param>
    /// <param name="value">The new value.</param>
    public void Set<T>(int index, T value)
    {
        ComVariant* variant = Find(index);
        if (variant is null || (variant->VarType & VarEnum.VT_BYREF) == 0)
        {
            return;
        }

        VarEnum type = variant->VarType & ~VarEnum.VT_BYREF;
        void* target = (void*)variant->GetRawDataRef<nint>();
        if (target is null)
        {
            return;
        }

        ComVariant converted = VariantMarshaller.ConvertToUnmanaged(value);
        if (type == VarEnum.VT_VARIANT)
        {
            Marshal.ThrowExceptionForHR(OleNativeMethods.VariantClear((ComVariant*)target));
            *(ComVariant*)target = converted;
            return;
        }

        try
        {
            if (converted.VarType != type)
            {
                Marshal.ThrowExceptionForHR(OleNativeMethods.VariantChangeType(&converted, &converted, 0, (ushort)type));
            }

            Store(target, type, ref converted);
        }
        finally
        {
            converted.Dispose();
        }
    }

    /// <summary>Sets the result of the invocation, if the caller expects one.</summary>
    /// <param name="value">The result.</param>
    public void SetResult<T>(T value)
    {
        if (_result is not null)
        {
            Marshal.ThrowExceptionForHR(OleNativeMethods.VariantClear(_result));
            *_result = VariantMarshaller.ConvertToUnmanaged(value);
        }
    }

    private ComVariant* Find(int index)
    {
        if (_parameters is null || index < 0)
        {
            return null;
        }

        uint count = _parameters->cArgs;
        uint named = _parameters->cNamedArgs;
        for (uint i = 0; i < named; i++)
        {
            if (_parameters->rgdispidNamedArgs[i] == index)
            {
                return _parameters->rgvarg + i;
            }
        }

        uint positional = count - named;
        return (uint)index < positional ? _parameters->rgvarg + (count - 1 - (uint)index) : null;
    }

    private static bool IsOmitted(ComVariant* variant) =>
        variant->VarType == VarEnum.VT_ERROR && variant->GetRawDataRef<int>() == OleNativeMethods.DISP_E_PARAMNOTFOUND;

    // Moves the value into the storage of a by-reference argument and releases the previous value.
    private static void Store(void* target, VarEnum type, ref ComVariant value)
    {
        if ((type & VarEnum.VT_ARRAY) != 0)
        {
            SafeArrayConverter.Destroy(*(nint*)target);
            *(nint*)target = value.GetRawDataRef<nint>();
            value = default;
            return;
        }

        switch (type)
        {
            case VarEnum.VT_BSTR:
                Marshal.FreeBSTR(*(nint*)target);
                *(nint*)target = value.GetRawDataRef<nint>();
                value = default;
                return;
            case VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH:
                if (*(nint*)target != 0)
                {
                    Marshal.Release(*(nint*)target);
                }

                *(nint*)target = value.GetRawDataRef<nint>();
                value = default;
                return;
            case VarEnum.VT_DECIMAL:
                *(decimal*)target = value.As<decimal>();
                return;
        }

        int size = type switch
        {
            VarEnum.VT_I1 or VarEnum.VT_UI1 => 1,
            VarEnum.VT_I2 or VarEnum.VT_UI2 or VarEnum.VT_BOOL => 2,
            VarEnum.VT_I4 or VarEnum.VT_UI4 or VarEnum.VT_INT or VarEnum.VT_UINT or VarEnum.VT_R4 or VarEnum.VT_ERROR => 4,
            VarEnum.VT_I8 or VarEnum.VT_UI8 or VarEnum.VT_R8 or VarEnum.VT_CY or VarEnum.VT_DATE => 8,
            _ => throw new NotSupportedException($"By-reference arguments of type {type} are not supported."),
        };

        fixed (byte* source = &value.GetRawDataRef<byte>())
        {
            Buffer.MemoryCopy(source, target, size, size);
        }
    }
}
