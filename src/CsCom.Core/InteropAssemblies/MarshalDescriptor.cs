using System.Reflection.Metadata;
using System.Runtime.InteropServices;

namespace CsCom.InteropAssemblies;

/// <summary>
/// The marshalling information of a parameter, return value or field (<see cref="MarshalAsAttribute"/>), decoded from its
/// metadata blob without resolving the types it names, e.g. custom marshalers of .NET Framework that .NET does not have.
/// </summary>
/// <param name="Type">The unmanaged type.</param>
/// <param name="SafeArraySubType">The element type of a <c>SAFEARRAY</c>, or <c>VT_EMPTY</c> if unspecified.</param>
/// <param name="SafeArrayUserDefinedSubType">The name of the user-defined element type of a <c>SAFEARRAY</c>.</param>
/// <param name="ArraySubType">The element type of a C-style array or fixed-size array.</param>
/// <param name="SizeConst">The number of elements of a fixed-size array or string, or of a C-style array.</param>
/// <param name="SizeParamIndex">The index of the parameter that contains the number of elements of a C-style array.</param>
/// <param name="MarshalType">The name of the type of a custom marshaler.</param>
internal readonly record struct MarshalDescriptor(
    UnmanagedType Type,
    VarEnum SafeArraySubType = VarEnum.VT_EMPTY,
    string? SafeArrayUserDefinedSubType = null,
    UnmanagedType? ArraySubType = null,
    int? SizeConst = null,
    int? SizeParamIndex = null,
    string? MarshalType = null)
{
    // NATIVE_TYPE_MAX: an unspecified element type.
    private const byte NativeTypeMax = 0x50;

    /// <summary>
    /// Reads the descriptor of a parameter or field, or returns <see langword="null"/> if it has none.
    /// </summary>
    public static MarshalDescriptor? Read(MetadataReader reader, BlobHandle handle) =>
        handle.IsNil ? null : Read(reader.GetBlobReader(handle));

    /// <summary>
    /// Decodes a <c>MarshalSpec</c> blob (ECMA-335 II.23.4).
    /// </summary>
    public static MarshalDescriptor Read(BlobReader blob)
    {
        var type = (UnmanagedType)blob.ReadByte();
        switch (type)
        {
            case UnmanagedType.SafeArray:
            {
                var subType = blob.RemainingBytes > 0 ? (VarEnum)blob.ReadCompressedInteger() : VarEnum.VT_EMPTY;
                string? userDefined = blob.RemainingBytes > 0 ? blob.ReadSerializedString() : null;
                return new MarshalDescriptor(type, subType, SafeArrayUserDefinedSubType: userDefined);
            }

            case UnmanagedType.ByValArray:
            {
                int size = blob.ReadCompressedInteger();
                UnmanagedType? elementType = blob.RemainingBytes > 0 ? ReadElementType(ref blob) : null;
                return new MarshalDescriptor(type, ArraySubType: elementType, SizeConst: size);
            }

            case UnmanagedType.ByValTStr:
                return new MarshalDescriptor(type, SizeConst: blob.ReadCompressedInteger());

            case UnmanagedType.LPArray:
            {
                UnmanagedType? elementType = blob.RemainingBytes > 0 ? ReadElementType(ref blob) : null;
                int? parameter = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : null;
                int? size = blob.RemainingBytes > 0 ? blob.ReadCompressedInteger() : null;
                return new MarshalDescriptor(type, ArraySubType: elementType, SizeConst: size, SizeParamIndex: parameter);
            }

            case UnmanagedType.CustomMarshaler:
            {
                blob.ReadSerializedString(); // The GUID of the type library.
                blob.ReadSerializedString(); // The native type name.
                string? marshalType = blob.ReadSerializedString();
                return new MarshalDescriptor(type, MarshalType: marshalType);
            }

            default:
                return new MarshalDescriptor(type);
        }
    }

    private static UnmanagedType? ReadElementType(ref BlobReader blob)
    {
        byte value = blob.ReadByte();
        return value == NativeTypeMax ? null : (UnmanagedType)value;
    }
}
