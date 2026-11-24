using System.Reflection;
using System.Runtime.InteropServices;
using CsCom.Model;

using static CsCom.InteropAssemblies.MetadataAttributes;

namespace CsCom.InteropAssemblies;

public sealed partial class GeneratedComAssemblyReader
{
    private sealed partial class Session
    {
        /// <summary>
        /// Maps a .NET type and its marshalling to the native type of the model. Source-generated COM interop marshals a
        /// type as its marshaller tells, so the marshallers of .NET and of the support code of CsCom tell the native types;
        /// the latter are recognized by their names, because the support code can be generated into any namespace.
        /// </summary>
        /// <param name="type">The .NET type, which is not passed by reference.</param>
        /// <param name="info">The marshalling of the value.</param>
        /// <param name="context">The member that uses the type, for warnings.</param>
        private TypeReference MapType(Type type, MarshallingInfo info, string context)
        {
            if (type.IsPointer)
            {
                return new PointerTypeReference(MapType(type.GetElementType()!, default, context));
            }

            if (type.IsFunctionPointer || type.IsByRef)
            {
                LogUnsupportedType(context, DisplayName(type), "void*");
                return new PointerTypeReference(GetPrimitive(VarEnum.VT_VOID));
            }

            UnmanagedType? unmanaged = info.Descriptor?.Type;
            string? marshaller = info.MarshallerName ?? GetNativeMarshaller(type);
            if (type.IsArray)
            {
                return MapArray(type, info, marshaller, context);
            }

            if (type.IsEnum)
            {
                return MapEnum(type, context);
            }

            switch (type.FullName)
            {
                case "System.Void":
                    return GetPrimitive(VarEnum.VT_VOID);
                case "System.SByte":
                    return GetPrimitive(VarEnum.VT_I1);
                case "System.Byte":
                    return GetPrimitive(VarEnum.VT_UI1);
                case "System.Int16":
                    return GetPrimitive(VarEnum.VT_I2);
                case "System.UInt16" or "System.Char":
                    return GetPrimitive(VarEnum.VT_UI2);
                case "System.Int32":
                    return GetPrimitive(unmanaged == UnmanagedType.Error ? VarEnum.VT_ERROR : VarEnum.VT_I4);
                case "System.UInt32":
                    return GetPrimitive(unmanaged == UnmanagedType.Error ? VarEnum.VT_ERROR : VarEnum.VT_UI4);
                case "System.Int64":
                    return GetPrimitive(VarEnum.VT_I8);
                case "System.UInt64":
                    return GetPrimitive(VarEnum.VT_UI8);
                case "System.Single":
                    return GetPrimitive(VarEnum.VT_R4);
                case "System.Double":
                    return GetPrimitive(VarEnum.VT_R8);
                case "System.IntPtr" or "System.UIntPtr":
                    // A value of the size of a pointer, which the interfaces that create type libraries only accept as one.
                    return new PointerTypeReference(GetPrimitive(VarEnum.VT_VOID));
                case "System.Boolean":
                    // Without marshalling information a bool is a byte, as it is when runtime marshalling is disabled.
                    return GetPrimitive(unmanaged switch
                    {
                        UnmanagedType.VariantBool => VarEnum.VT_BOOL,
                        UnmanagedType.Bool or UnmanagedType.I4 or UnmanagedType.U4 => VarEnum.VT_I4,
                        _ => VarEnum.VT_UI1,
                    });
                case "System.String":
                    return GetPrimitive(MapString(unmanaged, marshaller, info.Settings));
                case "System.Decimal":
                    return GetPrimitive(marshaller == "CurrencyMarshaller" || unmanaged == UnmanagedCurrency ? VarEnum.VT_CY : VarEnum.VT_DECIMAL);
                case "System.DateTime":
                    return GetPrimitive(VarEnum.VT_DATE);
                case "System.Guid":
                    return new WellKnownTypeReference(WellKnownType.Guid);
                case "System.Runtime.InteropServices.Marshalling.ComVariant":
                    return GetPrimitive(VarEnum.VT_VARIANT);
                case "System.Runtime.InteropServices.ComTypes.FILETIME":
                    return GetPrimitive(VarEnum.VT_FILETIME);
                case "System.Array" when marshaller == "SafeArrayMarshaller":
                    return new SafeArrayTypeReference(GetPrimitive(VarEnum.VT_VARIANT));
                case "System.Object":
                    return MapObject(unmanaged, marshaller, context);
            }

            if (type.IsInterface)
            {
                return MapInterface(type, context);
            }

            // The value types of the support code of CsCom that have the layouts of the native types.
            if (IsSupportType(type))
            {
                switch (GetSimpleName(type))
                {
                    case "ComBool":
                        return GetPrimitive(VarEnum.VT_BOOL);
                    case "ComDate":
                        return GetPrimitive(VarEnum.VT_DATE);
                    case "ComCurrency":
                        return GetPrimitive(VarEnum.VT_CY);
                    case "ComBstr" or "ComBstrValue":
                        return GetPrimitive(VarEnum.VT_BSTR);
                    case "ComSafeArray" when type.GenericTypeArguments is [var element]:
                        return new SafeArrayTypeReference(MapSafeArrayElement(element, context));
                }
            }

            if (type.IsValueType && !type.IsGenericType && !IsFrameworkType(type) && !IsSupportType(type))
            {
                return MapUserDefined(type, context) ?? throw new CsComException(
                    $"'{context}' uses the structure '{type.FullName}' of the assembly '{type.Assembly.GetName().Name}', which cannot be described in the type library: "
                    + "it is neither a type of a type library that CsCom generated code from, nor found in a type library of the assembly. "
                    + "Export the type library of the assembly and specify the file.");
            }

            if (type.IsValueType)
            {
                throw new CsComException($"'{context}' uses {DisplayName(type)}, which cannot be described in a type library.");
            }

            // The late-bound wrapper of a dispinterface of a type library is a pointer to the dispinterface.
            if (IsDispatchWrapper(type))
            {
                return new PointerTypeReference(new UserDefinedTypeReference(GetOrCreateDefinition(type)));
            }

            LogUnsupportedType(context, DisplayName(type), "IUnknown*");
            return GetPrimitive(VarEnum.VT_UNKNOWN);
        }

        // The marshaller that a type names itself, with [NativeMarshalling].
        private static string? GetNativeMarshaller(Type type) =>
            !type.IsArray && !type.IsPointer && GetArgument<Type>(type.GetCustomAttributesData(), MarshallingNamespace + "NativeMarshallingAttribute") is { } marshaller
                ? GetSimpleName(marshaller)
                : null;

        private static VarEnum MapString(UnmanagedType? unmanaged, string? marshaller, InterfaceSettings settings)
        {
            switch (unmanaged)
            {
                case UnmanagedType.BStr:
                    return VarEnum.VT_BSTR;
                case UnmanagedType.LPWStr:
                    return VarEnum.VT_LPWSTR;
                case UnmanagedType.LPStr or UnmanagedType.LPUTF8Str:
                    return VarEnum.VT_LPSTR;
            }

            // StringMarshalling.Custom is 0, Utf8 is 1 and Utf16 is 2.
            marshaller ??= settings.StringMarshalling switch
            {
                1 => "Utf8StringMarshaller",
                2 => "Utf16StringMarshaller",
                _ => settings.StringMarshaller is { } custom ? GetSimpleName(custom) : null,
            };
            return marshaller switch
            {
                "Utf16StringMarshaller" => VarEnum.VT_LPWSTR,
                "Utf8StringMarshaller" or "AnsiStringMarshaller" => VarEnum.VT_LPSTR,
                _ => VarEnum.VT_BSTR,
            };
        }

        private TypeReference MapObject(UnmanagedType? unmanaged, string? marshaller, string context)
        {
            switch (marshaller)
            {
                case "VariantMarshaller" or "OptionalVariantMarshaller" or "ComVariantMarshaller":
                    return GetPrimitive(VarEnum.VT_VARIANT);
                case "ComObjectMarshaller" or "UniqueComObjectMarshaller" or "ComInterfaceMarshaller" or "UniqueComInterfaceMarshaller":
                    return GetPrimitive(VarEnum.VT_UNKNOWN);
            }

            switch (unmanaged)
            {
                case UnmanagedType.Struct:
                    return GetPrimitive(VarEnum.VT_VARIANT);
                case UnmanagedType.IUnknown or UnmanagedType.Interface:
                    return GetPrimitive(VarEnum.VT_UNKNOWN);
                case UnmanagedType.IDispatch:
                    return GetPrimitive(VarEnum.VT_DISPATCH);
            }

            LogUnsupportedType(context, "an object without a marshaller", "IUnknown*");
            return GetPrimitive(VarEnum.VT_UNKNOWN);
        }

        private TypeReference MapInterface(Type type, string context)
        {
            if (GetWellKnownInterface(type) is { } wellKnown)
            {
                return wellKnown == IID_IDispatch ? GetPrimitive(VarEnum.VT_DISPATCH)
                    : wellKnown == IID_IEnumVARIANT ? new PointerTypeReference(new WellKnownTypeReference(WellKnownType.IEnumVariant))
                    : GetPrimitive(VarEnum.VT_UNKNOWN);
            }

            // An interface of the support code is in no type library, like one that is not a COM interface.
            bool isUserDefined = IsGeneratedComInterface(type) && !type.IsGenericType && !IsSupportType(type);
            if (isUserDefined && MapUserDefined(type, context) is { } reference)
            {
                return new PointerTypeReference(reference);
            }

            if (!isUserDefined)
            {
                LogUnsupportedType(context, DisplayName(type), IsDispatchInterface(type) ? "IDispatch*" : "IUnknown*");
            }

            return GetPrimitive(IsDispatchInterface(type) ? VarEnum.VT_DISPATCH : VarEnum.VT_UNKNOWN);
        }

        private TypeReference MapEnum(Type type, string context)
        {
            // The enumerations of type libraries are 32-bit, so the others are exported as their underlying types.
            Type underlying = type.GetEnumUnderlyingType();
            if (underlying.FullName is "System.Int32" or "System.UInt32" && !IsFrameworkType(type) && MapUserDefined(type, context) is { } reference)
            {
                return reference;
            }

            if (!IsFrameworkType(type) && underlying.FullName is not ("System.Int32" or "System.UInt32"))
            {
                LogUnsupportedType(context, $"the enumeration {type.Name} based on {underlying.Name}", underlying.Name);
            }

            return MapType(underlying, default, context);
        }

        // A type of the library, or a type of another library that the library refers to. A type of another assembly that
        // does not tell its type library cannot be referred to.
        private UserDefinedTypeReference? MapUserDefined(Type type, string context)
        {
            if (type.Assembly != _assembly && GetImportedLibrary(type) is null)
            {
                LogUnknownLibrary(type.FullName ?? type.Name, type.Assembly.GetName().Name ?? string.Empty);
                if (!type.IsValueType || type.IsEnum)
                {
                    LogUnsupportedType(context, DisplayName(type), type.IsEnum ? "long" : "IUnknown*");
                }

                return null;
            }

            return new UserDefinedTypeReference(GetOrCreateDefinition(type));
        }

        private TypeReference MapArray(Type type, MarshallingInfo info, string? marshaller, string context)
        {
            Type element = type.GetElementType()!;
            switch (marshaller)
            {
                case "SafeArrayMarshaller":
                    return new SafeArrayTypeReference(MapSafeArrayElement(info.MarshallerArgument ?? element, context), type.GetArrayRank() > 1 ? type.GetArrayRank() : null);
                case "UnknownSafeArrayMarshaller":
                    return new SafeArrayTypeReference(GetPrimitive(VarEnum.VT_UNKNOWN));
            }

            if (info.Descriptor is { Type: UnmanagedType.SafeArray } descriptor)
            {
                return new SafeArrayTypeReference(descriptor.SafeArraySubType is VarEnum.VT_EMPTY or VarEnum.VT_RECORD or VarEnum.VT_USERDEFINED
                    ? MapSafeArrayElement(element, context)
                    : GetPrimitive(descriptor.SafeArraySubType));
            }

            // A generated marshaller of the SAFEARRAYs of several dimensions, which CsCom names by the element and the rank.
            if (type.GetArrayRank() > 1)
            {
                return new SafeArrayTypeReference(MapSafeArrayElement(element, context), type.GetArrayRank());
            }

            LogUnsupportedType(context, $"the array {DisplayName(type)} that is not marshalled as a SAFEARRAY", "a pointer to its first element");
            return new PointerTypeReference(MapType(element, new MarshallingInfo(null, null, info.Settings), context));
        }

        // The elements of a SAFEARRAY have the types of the values of VARIANTs.
        private TypeReference MapSafeArrayElement(Type element, string context) => element.FullName switch
        {
            "System.String" => GetPrimitive(VarEnum.VT_BSTR),
            "System.Object" => GetPrimitive(VarEnum.VT_VARIANT),
            "System.Boolean" => GetPrimitive(VarEnum.VT_BOOL),
            _ => MapType(element, default, context),
        };

        // The fields of structures are blittable for source-generated COM interop, so they have their native types.
        private TypeReference MapField(FieldInfo field, string context)
        {
            Type type = field.FieldType;
            var descriptor = _marshalling.GetDescriptor(field);

            // A fixed buffer, whose field has the type of its elements and its length in an attribute.
            if (Find(field.GetCustomAttributesData(), "System.Runtime.CompilerServices.FixedBufferAttribute") is { ConstructorArguments: [{ Value: Type element }, { Value: int length }] })
            {
                return new FixedArrayTypeReference(MapType(element, default, context), [length]);
            }

            // An inline array: a structure with one field for its first element.
            if (type.IsValueType && Find(type.GetCustomAttributesData(), "System.Runtime.CompilerServices.InlineArrayAttribute") is { ConstructorArguments: [{ Value: int count }] }
                && GetInstanceFields(type) is [var first])
            {
                return new FixedArrayTypeReference(MapField(first, context), [count]);
            }

            if (type.FullName == "System.String" && descriptor is null)
            {
                LogUnsupportedType(context, "a string without [MarshalAs]", "BSTR");
            }

            return MapType(type, new MarshallingInfo(descriptor, null, default), context);
        }

        private static string DisplayName(Type type) => type.FullName ?? type.Name;
    }
}
