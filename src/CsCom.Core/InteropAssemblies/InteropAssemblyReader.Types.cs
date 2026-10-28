using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.InteropAssemblies;

public sealed partial class InteropAssemblyReader
{
    private sealed partial class Session
    {
        // UnmanagedType.Currency, which is obsolete for marshalling but still describes CURRENCY values in interop assemblies.
        private const UnmanagedType UnmanagedCurrency = (UnmanagedType)0x0F;

        // UnmanagedType.AsAny, which is obsolete as well.
        private const UnmanagedType UnmanagedAsAny = (UnmanagedType)0x28;

        /// <summary>
        /// Maps a .NET type and its marshalling information to the type of the model, following the rules of TlbImp and of the
        /// built-in COM interop for the types without marshalling information.
        /// </summary>
        /// <param name="type">The .NET type.</param>
        /// <param name="descriptor">The marshalling information, if any.</param>
        /// <param name="position">Where the type is used, which determines the default marshalling of some types.</param>
        /// <param name="context">The member that uses the type, for warnings.</param>
        /// <param name="charSet">The character set of the structure, for fields: strings and characters are ANSI by default.</param>
        private TypeReference MapType(Type type, MarshalDescriptor? descriptor, TypePosition position, string context, CharSet charSet = CharSet.Ansi)
        {
            if (type.IsByRef || type.IsPointer)
            {
                return new PointerTypeReference(MapType(type.GetElementType()!, type.IsByRef ? descriptor : null, position, context, charSet));
            }

            if (type.IsArray)
            {
                return MapArray(type, descriptor, position, context, charSet);
            }

            UnmanagedType? unmanaged = descriptor?.Type;
            switch (unmanaged)
            {
                case UnmanagedType.LPStruct when type.IsValueType:
                    // A structure passed by pointer, e.g. [MarshalAs(UnmanagedType.LPStruct)] Guid riid for a REFIID.
                    return new PointerTypeReference(MapType(type, null, position, context, charSet));
                case UnmanagedType.CustomMarshaler when descriptor!.Value.MarshalType?.Contains("EnumeratorToEnumVariantMarshaler", StringComparison.Ordinal) == true:
                    return new PointerTypeReference(new WellKnownTypeReference(WellKnownType.IEnumVariant));
                case UnmanagedType.CustomMarshaler:
                    // The custom marshalers of COM interop marshal interface pointers.
                    LogUnsupportedType(context, $"{DisplayName(type)} with the custom marshaler {descriptor!.Value.MarshalType}", "IUnknown*");
                    return GetPrimitive(VarEnum.VT_UNKNOWN);
                case UnmanagedAsAny:
                    LogUnsupportedType(context, $"{DisplayName(type)} marshalled as AsAny", "void*");
                    return new PointerTypeReference(GetPrimitive(VarEnum.VT_VOID));
            }

            if (type.IsEnum)
            {
                // COM enums are 32-bit, so other enums are imported as their underlying types, which keep the layout.
                Type underlying = type.GetEnumUnderlyingType();
                if (underlying.FullName is not ("System.Int32" or "System.UInt32"))
                {
                    LogEnumUnderlyingType(type.Name, underlying.Name);
                    return MapType(underlying, descriptor, position, context);
                }

                return new UserDefinedTypeReference(GetOrCreateDefinition(type));
            }

            switch (type.FullName)
            {
                case "System.Void":
                    return GetPrimitive(VarEnum.VT_VOID);
                case "System.Boolean":
                    return GetPrimitive(unmanaged switch
                    {
                        UnmanagedType.VariantBool => VarEnum.VT_BOOL,
                        UnmanagedType.Bool or UnmanagedType.I4 => VarEnum.VT_I4,
                        UnmanagedType.U4 => VarEnum.VT_UI4,
                        UnmanagedType.U1 => VarEnum.VT_UI1,
                        UnmanagedType.I1 => VarEnum.VT_I1,
                        _ => position == TypePosition.Field ? VarEnum.VT_I4 : VarEnum.VT_BOOL,
                    });
                case "System.SByte":
                    return GetPrimitive(VarEnum.VT_I1);
                case "System.Byte":
                    return GetPrimitive(VarEnum.VT_UI1);
                case "System.Int16":
                    return GetPrimitive(VarEnum.VT_I2);
                case "System.UInt16":
                    return GetPrimitive(VarEnum.VT_UI2);
                case "System.Char":
                    return GetPrimitive(IsAnsiCharacter(unmanaged, position, charSet) ? VarEnum.VT_UI1 : VarEnum.VT_UI2);
                case "System.Int32":
                    return GetPrimitive(unmanaged == UnmanagedType.Error ? VarEnum.VT_ERROR : VarEnum.VT_I4);
                case "System.UInt32":
                    return GetPrimitive(VarEnum.VT_UI4);
                case "System.Int64":
                    return GetPrimitive(VarEnum.VT_I8);
                case "System.UInt64":
                    return GetPrimitive(VarEnum.VT_UI8);
                case "System.Single":
                    return GetPrimitive(VarEnum.VT_R4);
                case "System.Double":
                    return GetPrimitive(VarEnum.VT_R8);
                case "System.Decimal":
                    return GetPrimitive(unmanaged == UnmanagedCurrency ? VarEnum.VT_CY : VarEnum.VT_DECIMAL);
                case "System.DateTime":
                    return GetPrimitive(VarEnum.VT_DATE);
                case "System.String":
                    // Strings are BSTRs in COM methods, and depend on the character set of the structure in fields.
                    return unmanaged switch
                    {
                        UnmanagedType.LPStr => GetPrimitive(VarEnum.VT_LPSTR),
                        UnmanagedType.LPWStr or UnmanagedType.LPTStr => GetPrimitive(VarEnum.VT_LPWSTR),
                        UnmanagedType.BStr => GetPrimitive(VarEnum.VT_BSTR),
                        UnmanagedType.ByValTStr => new FixedArrayTypeReference(
                            GetPrimitive(IsAnsi(charSet) ? VarEnum.VT_UI1 : VarEnum.VT_UI2),
                            [descriptor!.Value.SizeConst ?? 0]),
                        _ when position == TypePosition.Field => GetPrimitive(IsAnsi(charSet) ? VarEnum.VT_LPSTR : VarEnum.VT_LPWSTR),
                        _ => GetPrimitive(VarEnum.VT_BSTR),
                    };
                case "System.Object":
                    return GetPrimitive(unmanaged switch
                    {
                        UnmanagedType.IUnknown or UnmanagedType.Interface => VarEnum.VT_UNKNOWN,
                        UnmanagedType.IDispatch => VarEnum.VT_DISPATCH,
                        _ => VarEnum.VT_VARIANT,
                    });
                case "System.Guid":
                    return new WellKnownTypeReference(WellKnownType.Guid);
                case "System.IntPtr" or "System.UIntPtr":
                    return new PointerTypeReference(GetPrimitive(VarEnum.VT_VOID));
                case "System.Collections.IEnumerator":
                    // IEnumVARIANT, which TlbImp marshals with EnumeratorToEnumVariantMarshaler.
                    return new PointerTypeReference(new WellKnownTypeReference(WellKnownType.IEnumVariant));
                case "System.Array":
                    return new SafeArrayTypeReference(GetPrimitive(descriptor?.SafeArraySubType is { } subType and not VarEnum.VT_EMPTY ? subType : VarEnum.VT_VARIANT));
            }

            if (type.IsValueType && !IsFrameworkType(type))
            {
                return new UserDefinedTypeReference(GetOrCreateDefinition(type));
            }

            if (IsComImport(type))
            {
                if (type.IsInterface)
                {
                    Guid? iid = GetArgument<string>(type.GetCustomAttributesData(), InteropNamespace + "GuidAttribute") is { } text && Guid.TryParse(text, out var parsed) ? parsed : null;
                    if (iid == IID_IUnknown)
                    {
                        return GetPrimitive(VarEnum.VT_UNKNOWN);
                    }

                    if (iid == IID_IDispatch)
                    {
                        return GetPrimitive(VarEnum.VT_DISPATCH);
                    }

                    if (iid == IID_IEnumVARIANT)
                    {
                        return new PointerTypeReference(new WellKnownTypeReference(WellKnownType.IEnumVariant));
                    }

                    // TlbImp replaces the default interface of a coclass with its coclass interface (X : IDefault), when the
                    // interface is the default interface of only that coclass.
                    if (GetArgument<Type>(type.GetCustomAttributesData(), InteropNamespace + "CoClassAttribute") is { } coClass)
                    {
                        Type? defaultInterface = type.GetInterfaces().FirstOrDefault(IsCoClassInterfaceCandidate);
                        return new PointerTypeReference(new UserDefinedTypeReference(GetOrCreateDefinition(defaultInterface ?? coClass)));
                    }
                }

                return new PointerTypeReference(new UserDefinedTypeReference(GetOrCreateDefinition(type)));
            }

            return MapUnsupportedType(type, unmanaged, context);
        }

        // Reference types are marshalled as interface pointers by the built-in COM interop (managed objects as CCWs); other
        // value types have no COM equivalent.
        private TypeReference MapUnsupportedType(Type type, UnmanagedType? unmanaged, string context)
        {
            string name = DisplayName(type);
            if (type.IsValueType)
            {
                LogUnsupportedType(context, name, "void*");
                return new PointerTypeReference(GetPrimitive(VarEnum.VT_VOID));
            }

            if (type.BaseType?.FullName == "System.MulticastDelegate" && unmanaged == UnmanagedType.FunctionPtr)
            {
                LogUnsupportedType(context, name, "a function pointer (void*)");
                return new PointerTypeReference(GetPrimitive(VarEnum.VT_VOID));
            }

            if (name == "System.Text.StringBuilder")
            {
                // A buffer that the callee fills: a pointer to its first character.
                bool ansi = unmanaged == UnmanagedType.LPStr;
                LogUnsupportedType(context, name, ansi ? "a character buffer (byte*)" : "a character buffer (ushort*)");
                return new PointerTypeReference(GetPrimitive(ansi ? VarEnum.VT_UI1 : VarEnum.VT_UI2));
            }

            LogUnsupportedType(context, name, "IUnknown*");
            return GetPrimitive(VarEnum.VT_UNKNOWN);
        }

        // E.g. System.Collections.Generic.List<System.Int32> rather than the assembly-qualified arguments of FullName.
        private static string DisplayName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.FullName ?? type.Name;
            }

            string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
            return $"{type.Namespace}.{name}<{string.Join(", ", type.GetGenericArguments().Select(DisplayName))}>";
        }

        private static bool IsAnsi(CharSet charSet) => charSet is CharSet.Ansi or CharSet.None;

        // Characters are UTF-16 in COM methods and with the Unicode character set, and ANSI in the fields of other structures.
        private static bool IsAnsiCharacter(UnmanagedType? unmanaged, TypePosition position, CharSet charSet) =>
            unmanaged is UnmanagedType.U1 or UnmanagedType.I1 || (unmanaged is null && position == TypePosition.Field && IsAnsi(charSet));

        private TypeReference MapArray(Type type, MarshalDescriptor? descriptor, TypePosition position, string context, CharSet charSet)
        {
            Type element = type.GetElementType()!;
            MarshalDescriptor? elementDescriptor = descriptor?.ArraySubType is { } subType ? new MarshalDescriptor(subType) : null;
            switch (descriptor?.Type)
            {
                case UnmanagedType.ByValArray:
                    return new FixedArrayTypeReference(MapType(element, elementDescriptor, TypePosition.Field, context, charSet), [descriptor.Value.SizeConst ?? 0]);
                case UnmanagedType.LPArray:
                    // The model has no size_is, so C-style arrays are pointers to their first elements, as in type libraries.
                    LogCArray(context);
                    return new PointerTypeReference(MapType(element, elementDescriptor, position, context, charSet));
            }

            int rank = type.GetArrayRank();
            return new SafeArrayTypeReference(MapSafeArrayElement(element, descriptor?.SafeArraySubType ?? VarEnum.VT_EMPTY, context), rank > 1 ? rank : null);
        }

        // The element type of a SAFEARRAY is given by SafeArraySubType, except for the user-defined types, which are more
        // precise: TlbImp records VT_I4 for enums and VT_DISPATCH or VT_UNKNOWN for interfaces.
        private TypeReference MapSafeArrayElement(Type element, VarEnum subType, string context)
        {
            bool userDefined = element.IsEnum || IsComImport(element) || (element.IsValueType && !IsFrameworkType(element));
            return !userDefined && subType is not (VarEnum.VT_EMPTY or VarEnum.VT_RECORD)
                ? GetPrimitive(subType)
                : MapType(element, null, TypePosition.ArrayElement, context);
        }

        // Computes the layout of a structure for 64-bit processes: the offsets of the fields, the size and the alignment, which
        // is the packing of TlbImp, where type libraries record their alignment.
        private RecordLayout GetRecordLayout(Type type)
        {
            if (_layouts.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var fields = GetInstanceFields(type);
            var attribute = type.StructLayoutAttribute;
            bool isExplicit = attribute?.Value == LayoutKind.Explicit;
            int pack = attribute is { Pack: > 0 } ? attribute.Pack : 8;
            var charSet = attribute?.CharSet ?? CharSet.Ansi;
            var offsets = new int[fields.Count];
            bool sequential = true;
            int end = 0;
            int size = 0;
            int alignment = 1;
            for (int i = 0; i < fields.Count; i++)
            {
                var (fieldSize, fieldAlignment) = GetFieldLayout(fields[i].FieldType, GetDescriptor(fields[i]), charSet);
                int effective = Math.Min(fieldAlignment, pack);
                offsets[i] = isExplicit ? GetFieldOffset(fields[i]) : Align(end, effective);
                sequential &= offsets[i] == Align(end, effective);
                end = offsets[i] + fieldSize;
                size = Math.Max(size, end);
                alignment = Math.Max(alignment, effective);
            }

            // The generator writes sequential structures and unions, so other explicit layouts are imported as sequential ones.
            bool isUnion = isExplicit && fields.Count > 0 && offsets.All(static o => o == 0);
            if (isExplicit && !isUnion && !sequential)
            {
                LogExplicitLayout(type.Name);
            }

            size = Math.Max(Align(size, alignment), attribute?.Size ?? 0);
            var layout = new RecordLayout(size, attribute is { Pack: > 0 } ? attribute.Pack : alignment, offsets, isUnion)
            {
                FieldAlignment = alignment,
            };
            _layouts.Add(type, layout);
            return layout;
        }

        private (int Size, int Alignment) GetFieldLayout(Type type, MarshalDescriptor? descriptor, CharSet charSet)
        {
            int pointer = IntPtr.Size;
            if (type.IsByRef || type.IsPointer)
            {
                return (pointer, pointer);
            }

            if (type.IsEnum)
            {
                return GetFieldLayout(type.GetEnumUnderlyingType(), null, charSet);
            }

            if (type.IsArray)
            {
                if (descriptor is { Type: UnmanagedType.ByValArray } array)
                {
                    var (elementSize, elementAlignment) = GetFieldLayout(type.GetElementType()!, array.ArraySubType is { } subType ? new MarshalDescriptor(subType) : null, charSet);
                    return (elementSize * (array.SizeConst ?? 0), elementAlignment);
                }

                return (pointer, pointer);
            }

            UnmanagedType? unmanaged = descriptor?.Type;
            switch (type.FullName)
            {
                case "System.Boolean":
                    return unmanaged switch
                    {
                        UnmanagedType.VariantBool => (2, 2),
                        UnmanagedType.U1 or UnmanagedType.I1 => (1, 1),
                        _ => (4, 4),
                    };
                case "System.SByte" or "System.Byte":
                    return (1, 1);
                case "System.Int16" or "System.UInt16":
                    return (2, 2);
                case "System.Char":
                    return IsAnsiCharacter(unmanaged, TypePosition.Field, charSet) ? (1, 1) : (2, 2);
                case "System.IntPtr" or "System.UIntPtr":
                    return (pointer, pointer);
                case "System.Int32" or "System.UInt32" or "System.Single":
                    return (4, 4);
                case "System.Int64" or "System.UInt64" or "System.Double" or "System.DateTime":
                    return (8, 8);
                case "System.Decimal":
                    return unmanaged == UnmanagedCurrency ? (8, 8) : (16, 8);
                case "System.Guid":
                    return (16, 4);
                case "System.String":
                    int characterSize = IsAnsi(charSet) ? 1 : 2;
                    return unmanaged == UnmanagedType.ByValTStr ? ((descriptor!.Value.SizeConst ?? 0) * characterSize, characterSize) : (pointer, pointer);
                case "System.Object":
                    // A VARIANT has the size of four pointers on 64-bit and of 16 bytes on 32-bit Windows.
                    return unmanaged is UnmanagedType.IUnknown or UnmanagedType.IDispatch or UnmanagedType.Interface
                        ? (pointer, pointer)
                        : (pointer == 8 ? 24 : 16, 8);
            }

            if (type.IsValueType && !IsFrameworkType(type))
            {
                var layout = GetRecordLayout(type);
                return (layout.Size, layout.FieldAlignment);
            }

            return (pointer, pointer);
        }

        private static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

        // The assemblies of the .NET runtime, which resolve the references to the assemblies of .NET Framework, define no COM
        // types; their value types other than the mapped ones are not supported.
        private static bool IsFrameworkType(Type type) =>
            type.Assembly.Location.StartsWith(RuntimeEnvironment.GetRuntimeDirectory(), StringComparison.OrdinalIgnoreCase);

        [LoggerMessage(Level = LogLevel.Warning, Message = "'{Member}' uses {Type}, which is not supported; it is imported as {Native}")]
        private partial void LogUnsupportedType(string member, string type, string native);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The structure '{Name}' has an explicit layout, which is imported as a sequential layout")]
        private partial void LogExplicitLayout(string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "'{Member}' is a C-style array, which is imported as a pointer to its first element")]
        private partial void LogCArray(string member);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The enum '{Name}' is based on {Type} rather than Int32, so it is imported as {Type}")]
        private partial void LogEnumUnderlyingType(string name, string type);
    }
}
