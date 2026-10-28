using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.InteropAssemblies;

public sealed partial class InteropAssemblyReader
{
    private sealed partial class Session
    {
        private void PopulateInterface(Type type, InterfaceDefinition definition)
        {
            var kind = GetInterfaceKind(type)!.Value;
            definition.InterfaceKind = kind;
            var methods = GetDeclaredMethods(type);
            if (kind == InterfaceKind.Dispatch)
            {
                definition.BaseInterface = GetPrimitive(VarEnum.VT_DISPATCH);
                definition.BaseVtableSlots = 7;
                PopulateDispInterface(type, definition, methods);
                return;
            }

            var (baseType, inherited) = FindBaseInterface(type, methods);
            if (baseType is not null)
            {
                definition.BaseInterface = new UserDefinedTypeReference(GetOrCreateDefinition(baseType));
                definition.BaseVtableSlots = GetVtableSize(baseType);
            }
            else if (DerivesFromDispatch(type, kind))
            {
                definition.BaseInterface = GetPrimitive(VarEnum.VT_DISPATCH);
                definition.BaseVtableSlots = 7;
            }
            else
            {
                definition.BaseVtableSlots = 3;
            }

            for (int i = inherited; i < methods.Count; i++)
            {
                definition.Methods.Add(ReadMethod(type, methods, i, definition.BaseVtableSlots + i - inherited, dispatch: false));
            }
        }

        // TlbImp imports the properties of dispinterfaces (VARDESCs) as properties whose accessors, unlike those of the
        // methods (FUNCDESCs), do not preserve the signature.
        private void PopulateDispInterface(Type type, InterfaceDefinition definition, List<MethodInfo> methods)
        {
            var variableAccessors = new HashSet<MethodInfo>();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(static p => p.MetadataToken))
            {
                var getter = property.GetGetMethod(nonPublic: true);
                var setter = property.GetSetMethod(nonPublic: true);
                if ((getter ?? setter) is not { } accessor || IsPreserveSig(accessor) || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                var descriptor = getter is not null ? GetDescriptor(getter.ReturnParameter) : GetDescriptor(setter!.GetParameters()[^1]);
                var varFlags = Find(property.GetCustomAttributesData(), InteropNamespace + "TypeLibVarAttribute") is { ConstructorArguments: [var argument] }
                    ? Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture)
                    : 0;
                definition.Properties.Add(new DispatchPropertyDefinition
                {
                    Name = property.Name,
                    DispId = GetDispId(property.GetCustomAttributesData()) ?? GetDispId(accessor.GetCustomAttributesData()) ?? 0,
                    Type = MapType(property.PropertyType, descriptor, TypePosition.Parameter, $"{type.Name}.{property.Name}"),
                    IsReadOnly = setter is null,
                    Flags = MapVarFlags(varFlags) | (setter is null ? MemberFlags.ReadOnly : MemberFlags.None),
                });

                variableAccessors.Add(accessor);
                if (setter is not null)
                {
                    variableAccessors.Add(setter);
                }
            }

            for (int i = 0; i < methods.Count; i++)
            {
                if (!variableAccessors.Contains(methods[i]))
                {
                    definition.Methods.Add(ReadMethod(type, methods, i, -1, dispatch: true));
                }
            }
        }

        // Interfaces of the built-in COM interop declare the methods of their bases first, so the base is the most derived
        // implemented interface whose methods start the method list.
        private (Type? BaseType, int Inherited) FindBaseInterface(Type type, List<MethodInfo> methods)
        {
            Type? best = null;
            int bestCount = 0;
            foreach (Type candidate in type.GetInterfaces())
            {
                if (!IsComImport(candidate) || GetInterfaceKind(candidate) is null or InterfaceKind.Dispatch)
                {
                    continue;
                }

                var baseMethods = GetDeclaredMethods(candidate);
                if (baseMethods.Count > bestCount && baseMethods.Count <= methods.Count
                    && baseMethods.Select(static m => m.Name).SequenceEqual(methods.Take(baseMethods.Count).Select(static m => m.Name)))
                {
                    best = candidate;
                    bestCount = baseMethods.Count;
                }
            }

            return (best, bestCount);
        }

        private int GetVtableSize(Type type)
        {
            if (!_vtableSizes.TryGetValue(type, out int size))
            {
                var kind = GetInterfaceKind(type);
                var methods = GetDeclaredMethods(type);
                var (baseType, inherited) = FindBaseInterface(type, methods);
                int baseSize = baseType is not null ? GetVtableSize(baseType) : DerivesFromDispatch(type, kind) ? 7 : 3;
                size = kind == InterfaceKind.Dispatch ? 7 : baseSize + methods.Count - inherited;
                _vtableSizes.Add(type, size);
            }

            return size;
        }

        private static bool DerivesFromDispatch(Type type, InterfaceKind? kind) =>
            kind == InterfaceKind.Dual
            || (Find(type.GetCustomAttributesData(), InteropNamespace + "TypeLibTypeAttribute") is { ConstructorArguments: [var argument] }
                && (Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture) & TypeLibTypeDispatchable) != 0);

        private MethodDefinition ReadMethod(Type owner, List<MethodInfo> methods, int index, int slot, bool dispatch)
        {
            var method = methods[index];
            var attributes = method.GetCustomAttributesData();
            int? dispId = GetDispId(attributes);
            var (name, invokeKind) = GetNameAndKind(method, methods, dispId);
            string context = $"{owner.Name}.{name}";
            bool preserveSig = IsPreserveSig(method);

            var parameters = method.GetParameters();
            var definitions = new List<ParameterDefinition>(parameters.Length + 2);
            for (int i = 0; i < parameters.Length; i++)
            {
                definitions.Add(ReadParameter(parameters[i], i, parameters.Length, invokeKind, context));
            }

            if (Find(attributes, InteropNamespace + "LCIDConversionAttribute") is { ConstructorArguments: [var lcidArgument] })
            {
                int position = Math.Clamp((int)lcidArgument.Value!, 0, definitions.Count);
                definitions.Insert(position, new ParameterDefinition { Name = "lcid", Type = GetPrimitive(VarEnum.VT_I4), Flags = ParameterFlags.In | ParameterFlags.Lcid });
            }

            // TlbImp returns the enumerator of _NewEnum (IUnknown*) through a custom marshaler as an IEnumerator.
            TypeReference returnType = name == "_NewEnum"
                ? GetPrimitive(VarEnum.VT_UNKNOWN)
                : MapType(method.ReturnType, GetDescriptor(method.ReturnParameter), TypePosition.Return, context);

            var definition = new MethodDefinition
            {
                Name = name,
                OriginalName = name,
                MemberId = dispId ?? FirstMemberId + index,
                InvokeKind = invokeKind,
                VtableSlot = slot,
                ReturnType = dispatch || preserveSig ? returnType : GetPrimitive(VarEnum.VT_HRESULT),
                Flags = MapFuncFlags(attributes),
            };

            definition.Parameters.AddRange(definitions);
            if (!dispatch && !preserveSig && method.ReturnType.FullName != "System.Void")
            {
                definition.Parameters.Add(new ParameterDefinition
                {
                    Name = "pRetVal",
                    Type = new PointerTypeReference(returnType),
                    Flags = ParameterFlags.Out | ParameterFlags.Retval,
                });
            }

            return definition;
        }

        // TlbImp names the accessors get_X, set_X and let_X; set_X is the putref accessor when let_X (put) exists. A setter
        // alone is a put accessor, except for interfaces, which are assigned by reference like VB's Set.
        private (string Name, InvokeKind Kind) GetNameAndKind(MethodInfo method, List<MethodInfo> methods, int? dispId)
        {
            string name = method.Name;
            if (method.IsSpecialName && name.Length > 4 && name[3] == '_')
            {
                string property = name[4..];
                switch (name[..4])
                {
                    case "get_":
                        return (property, InvokeKind.PropertyGet);
                    case "let_":
                        return (property, InvokeKind.PropertyPut);
                    case "set_":
                        bool byReference = methods.Any(m => m.Name == "let_" + property) || IsAssignedByReference(method);
                        return (property, byReference ? InvokeKind.PropertyPutRef : InvokeKind.PropertyPut);
                }
            }

            return dispId == DispIdNewEnum && name == "GetEnumerator" ? ("_NewEnum", InvokeKind.PropertyGet) : (name, InvokeKind.Method);
        }

        private bool IsAssignedByReference(MethodInfo setter)
        {
            var parameters = setter.GetParameters();
            if (parameters.Length == 0)
            {
                return false;
            }

            var value = parameters[^1];
            return (value.ParameterType.IsInterface && IsComImport(value.ParameterType))
                || (value.ParameterType.FullName == "System.Object" && GetDescriptor(value)?.Type is UnmanagedType.IUnknown or UnmanagedType.IDispatch or UnmanagedType.Interface);
        }

        private ParameterDefinition ReadParameter(ParameterInfo parameter, int index, int count, InvokeKind kind, string context)
        {
            string name = !string.IsNullOrEmpty(parameter.Name)
                ? parameter.Name
                : index == count - 1 && kind is InvokeKind.PropertyPut or InvokeKind.PropertyPutRef ? "value" : $"param{index}";

            var flags = ParameterFlags.None;
            if ((parameter.Attributes & ParameterAttributes.In) != 0)
            {
                flags |= ParameterFlags.In;
            }

            if ((parameter.Attributes & ParameterAttributes.Out) != 0)
            {
                flags |= ParameterFlags.Out;
            }

            if (flags == ParameterFlags.None)
            {
                flags = parameter.ParameterType.IsByRef ? ParameterFlags.In | ParameterFlags.Out : ParameterFlags.In;
            }

            if ((parameter.Attributes & ParameterAttributes.Optional) != 0)
            {
                flags |= ParameterFlags.Optional;
            }

            object? defaultValue = null;
            if (TryGetDefaultValue(parameter, out object? value))
            {
                flags |= ParameterFlags.HasDefault;
                defaultValue = value;
            }

            return new ParameterDefinition
            {
                Name = name,
                Type = MapType(parameter.ParameterType, GetDescriptor(parameter), TypePosition.Parameter, $"{context}({name})"),
                Flags = flags,
                DefaultValue = defaultValue,
            };
        }

        // Default values are metadata constants, or attributes for the types that have no constants.
        private static bool TryGetDefaultValue(ParameterInfo parameter, out object? value)
        {
            if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0)
            {
                value = parameter.RawDefaultValue;
                return true;
            }

            foreach (var attribute in parameter.GetCustomAttributesData())
            {
                switch (attribute.AttributeType.FullName)
                {
                    case "System.Runtime.CompilerServices.DateTimeConstantAttribute":
                        value = new DateTime((long)attribute.ConstructorArguments[0].Value!);
                        return true;
                    case "System.Runtime.CompilerServices.DecimalConstantAttribute":
                        var arguments = attribute.ConstructorArguments;
                        value = new decimal(
                            Convert.ToInt32(arguments[4].Value, CultureInfo.InvariantCulture),
                            Convert.ToInt32(arguments[3].Value, CultureInfo.InvariantCulture),
                            Convert.ToInt32(arguments[2].Value, CultureInfo.InvariantCulture),
                            Convert.ToByte(arguments[1].Value, CultureInfo.InvariantCulture) != 0,
                            Convert.ToByte(arguments[0].Value, CultureInfo.InvariantCulture));
                        return true;
                    case InteropNamespace + "DefaultParameterValueAttribute":
                        object? argument = attribute.ConstructorArguments[0].Value;
                        value = argument is CustomAttributeTypedArgument typed ? typed.Value : argument;
                        return true;
                }
            }

            value = null;
            return false;
        }

        private static bool IsPreserveSig(MethodInfo method) => (method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0;

        private static int? GetDispId(IList<CustomAttributeData> attributes) =>
            Find(attributes, InteropNamespace + "DispIdAttribute") is { ConstructorArguments: [var argument] } ? (int)argument.Value! : null;

        private static MemberFlags MapFuncFlags(IList<CustomAttributeData> attributes)
        {
            const int mask = 0x1 | 0x2 | 0x4 | 0x40 | 0x100 | 0x200 | 0x400;
            return Find(attributes, InteropNamespace + "TypeLibFuncAttribute") is { ConstructorArguments: [var argument] }
                ? (MemberFlags)(Convert.ToInt32(argument.Value, CultureInfo.InvariantCulture) & mask)
                : MemberFlags.None;
        }

        // The flags of TypeLibVarAttribute are those of VARDESCs (VARFLAGS).
        private static MemberFlags MapVarFlags(int flags)
        {
            var result = MemberFlags.None;
            (int VarFlag, MemberFlags Flag)[] map =
            [
                (0x80, MemberFlags.Restricted), (0x2, MemberFlags.Source), (0x4, MemberFlags.Bindable), (0x40, MemberFlags.Hidden),
                (0x100, MemberFlags.DefaultCollectionElement), (0x200, MemberFlags.UiDefault), (0x400, MemberFlags.NonBrowsable), (0x1, MemberFlags.ReadOnly),
            ];
            foreach (var (varFlag, flag) in map)
            {
                if ((flags & varFlag) != 0)
                {
                    result |= flag;
                }
            }

            return result;
        }
    }
}
