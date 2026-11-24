using System.Reflection;
using System.Runtime.InteropServices;
using CsCom.Model;

using static CsCom.InteropAssemblies.MetadataAttributes;

namespace CsCom.InteropAssemblies;

public sealed partial class GeneratedComAssemblyReader
{
    private sealed partial class Session
    {
        // UnmanagedType.Currency, which is obsolete.
        private const UnmanagedType UnmanagedCurrency = (UnmanagedType)0x0F;

        private void PopulateInterface(Type type, InterfaceDefinition definition)
        {
            Type? baseInterface = GetBaseInterface(type);
            definition.BaseInterface = baseInterface is null ? null : GetWellKnownInterface(baseInterface) switch
            {
                { } guid when guid == IID_IDispatch => GetPrimitive(VarEnum.VT_DISPATCH),
                { } guid when guid == IID_IEnumVARIANT => new WellKnownTypeReference(WellKnownType.IEnumVariant),
                { } => null,
                null => new UserDefinedTypeReference(GetOrCreateDefinition(baseInterface)),
            };

            // The COM generator adds the members of the base interfaces of the same assembly to an interface, as members with
            // bodies: the members of the interface itself are the abstract ones, in the order of their declarations.
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(static m => m.IsAbstract)
                .OrderBy(static m => m.MetadataToken)
                .ToList();
            var settings = InterfaceSettings.Read(type);
            int firstId = FirstMemberId | (GetDepth(type) << 16);
            var propertyIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var read = new List<(MethodInfo Method, string Name, InvokeKind Kind)>();
            foreach (var method in methods)
            {
                var (name, kind) = GetNameAndKind(method, type);
                read.Add((method, name, kind));

                // The accessors of a property have the identifier that any of them declares.
                if (kind != InvokeKind.Method && GetDispId(method) is { } declared && !propertyIds.TryAdd(name, declared) && propertyIds[name] != declared)
                {
                    throw new CsComException($"The accessors of the property '{type.Name}.{name}' have different [DispId] values.");
                }
            }

            for (int i = 0; i < read.Count; i++)
            {
                var (method, name, kind) = read[i];
                int defaultId = kind == InvokeKind.PropertyGet && name == "_NewEnum" ? DispIdNewEnum : firstId + i;
                int memberId;
                if (kind == InvokeKind.Method)
                {
                    memberId = GetDispId(method) ?? defaultId;
                }
                else
                {
                    if (!propertyIds.TryGetValue(name, out memberId))
                    {
                        propertyIds.Add(name, memberId = defaultId);
                    }
                }

                definition.Methods.Add(ReadMethod(type, method, name, kind, memberId, settings));
            }

            if (definition.Methods.Where(static m => m.InvokeKind == InvokeKind.Method).GroupBy(static m => m.MemberId).FirstOrDefault(static g => g.Count() > 1) is { } duplicates)
            {
                throw new CsComException(
                    $"The members {string.Join(" and ", duplicates.Select(m => $"'{type.Name}.{m.OriginalName}'"))} have the same [DispId] {duplicates.Key}.");
            }
        }

        private static int? GetDispId(MethodInfo method) =>
            Find(method.GetCustomAttributesData(), InteropNamespace + "DispIdAttribute") is { ConstructorArguments: [var argument] } ? (int)argument.Value! : null;

        // Source-generated COM interfaces have no properties: the accessors of a property are the methods named get_X,
        // set_X (or put_X, let_X) and putref_X, as in the code that CsCom generates.
        private (string Name, InvokeKind Kind) GetNameAndKind(MethodInfo method, Type type)
        {
            (string prefix, InvokeKind kind)[] prefixes =
            [
                ("get_", InvokeKind.PropertyGet), ("set_", InvokeKind.PropertyPut), ("put_", InvokeKind.PropertyPut),
                ("let_", InvokeKind.PropertyPut), ("putref_", InvokeKind.PropertyPutRef),
            ];
            foreach (var (prefix, kind) in prefixes)
            {
                if (!method.Name.StartsWith(prefix, StringComparison.Ordinal) || method.Name.Length == prefix.Length)
                {
                    continue;
                }

                bool returnsValue = method.ReturnType.FullName != "System.Void";
                bool isValid = !IsPreserveSig(method) && (kind == InvokeKind.PropertyGet ? returnsValue : !returnsValue && method.GetParameters().Length > 0);
                if (isValid)
                {
                    return (method.Name[prefix.Length..], kind);
                }

                LogInvalidAccessor($"{type.Name}.{method.Name}");
                break;
            }

            return (method.Name, InvokeKind.Method);
        }

        private static bool IsPreserveSig(MethodInfo method) => (method.MethodImplementationFlags & MethodImplAttributes.PreserveSig) != 0;

        private MethodDefinition ReadMethod(Type type, MethodInfo method, string name, InvokeKind kind, int memberId, InterfaceSettings settings)
        {
            string context = $"{type.Name}.{method.Name}";
            var attributes = method.GetCustomAttributesData();
            bool preserveSig = IsPreserveSig(method);
            var definition = new MethodDefinition
            {
                Name = name,
                OriginalName = name,
                MemberId = memberId,
                InvokeKind = kind,
                ReturnType = preserveSig ? MapReturn(method, context, settings) : GetPrimitive(VarEnum.VT_HRESULT),
                Flags = Find(attributes, InteropNamespace + "TypeLibFuncAttribute") is { ConstructorArguments: [var flags] }
                    ? (MemberFlags)(Convert.ToInt32(flags.Value, System.Globalization.CultureInfo.InvariantCulture) & 0x7FF)
                    : memberId == DispIdNewEnum ? MemberFlags.Restricted : MemberFlags.None,
                Documentation = GetDescription(attributes),
            };

            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                definition.Parameters.Add(ReadParameter(parameters[i], i, context, settings));
            }

            // A method that does not preserve its signature returns an HRESULT, and its return value in a last parameter.
            if (!preserveSig && method.ReturnType.FullName != "System.Void")
            {
                definition.Parameters.Add(new ParameterDefinition
                {
                    Name = "pRetVal",
                    Type = new PointerTypeReference(MapReturn(method, context, settings)),
                    Flags = ParameterFlags.Out | ParameterFlags.Retval,
                });
            }

            return definition;
        }

        private TypeReference MapReturn(MethodInfo method, string context, InterfaceSettings settings)
        {
            var parameter = method.ReturnParameter;
            var descriptor = _marshalling.GetDescriptor(parameter);
            if (descriptor?.Type == UnmanagedType.Error && method.ReturnType.FullName is "System.Int32" or "System.UInt32")
            {
                return GetPrimitive(VarEnum.VT_HRESULT);
            }

            return MapType(method.ReturnType, new MarshallingInfo(descriptor, GetMarshaller(parameter.GetCustomAttributesData()), settings), context);
        }

        private ParameterDefinition ReadParameter(ParameterInfo parameter, int index, string context, InterfaceSettings settings)
        {
            string name = string.IsNullOrEmpty(parameter.Name) ? $"param{index}" : parameter.Name;
            Type type = parameter.ParameterType;
            var flags = ParameterFlags.None;
            if (type.IsByRef)
            {
                // C# marks the in and out parameters; a ref parameter is passed both ways.
                bool isIn = (parameter.Attributes & ParameterAttributes.In) != 0;
                bool isOut = (parameter.Attributes & ParameterAttributes.Out) != 0;
                flags = isOut && !isIn ? ParameterFlags.Out : isIn && !isOut ? ParameterFlags.In : ParameterFlags.In | ParameterFlags.Out;
            }
            else
            {
                flags = ParameterFlags.In;
            }

            var info = new MarshallingInfo(_marshalling.GetDescriptor(parameter), GetMarshaller(parameter.GetCustomAttributesData()), settings);
            TypeReference mapped = type.IsByRef
                ? new PointerTypeReference(MapType(type.GetElementType()!, info, $"{context}({name})"))
                : MapType(type, info, $"{context}({name})");

            object? defaultValue = null;
            bool hasDefault = TryGetDefaultValue(parameter, out object? value);
            if (hasDefault && value is not (null or DBNull or Missing))
            {
                flags |= ParameterFlags.Optional | ParameterFlags.HasDefault;
                defaultValue = value;
            }
            else if (hasDefault || (parameter.Attributes & ParameterAttributes.Optional) != 0)
            {
                // A parameter that can be omitted without a value to pass for it, e.g. an optional VARIANT.
                flags |= ParameterFlags.Optional;
            }

            return new ParameterDefinition { Name = name, Type = mapped, Flags = flags, DefaultValue = defaultValue };
        }

        // The marshaller of [MarshalUsing(typeof(X))] of the value itself, rather than of the elements of a collection.
        private static Type? GetMarshaller(IList<CustomAttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeType.FullName == MarshallingNamespace + "MarshalUsingAttribute"
                    && attribute.ConstructorArguments is [{ Value: Type marshaller }]
                    && GetNamedArgument(attribute, "ElementIndirectionDepth") is null or 0)
                {
                    return marshaller;
                }
            }

            return null;
        }

        // The marshalling of the strings that have none of their own, which the interface declares.
        private readonly record struct InterfaceSettings(int? StringMarshalling, Type? StringMarshaller)
        {
            public static InterfaceSettings Read(Type type)
            {
                var attribute = Find(type.GetCustomAttributesData(), MarshallingNamespace + "GeneratedComInterfaceAttribute");
                return attribute is null ? default : new InterfaceSettings(
                    GetNamedArgument(attribute, "StringMarshalling") is { } value ? Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) : null,
                    GetNamedArgument(attribute, "StringMarshallingCustomType") as Type);
            }
        }

        // What tells the native type of a value: [MarshalAs], the marshaller of [MarshalUsing], and the interface.
        private readonly record struct MarshallingInfo(MarshalDescriptor? Descriptor, Type? Marshaller, InterfaceSettings Settings)
        {
            // The name of the marshaller without its type arguments, by which the marshallers of the support code are
            // recognized wherever it was generated.
            public string? MarshallerName => Marshaller is null ? null : GetSimpleName(Marshaller);

            public Type? MarshallerArgument => Marshaller is { IsConstructedGenericType: true, GenericTypeArguments: [var argument] } ? argument : null;
        }

        private static string GetSimpleName(Type type)
        {
            int arity = type.Name.IndexOf('`', StringComparison.Ordinal);
            return arity < 0 ? type.Name : type.Name[..arity];
        }
    }
}
