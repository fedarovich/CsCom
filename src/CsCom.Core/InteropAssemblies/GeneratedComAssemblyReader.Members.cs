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

            RenameOverloads(type, read);
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

            // The accessors of a property are one member, which has one identifier.
            var members = definition.Methods
                .Select(static m => (m.MemberId, Name: m.OriginalName, IsProperty: m.InvokeKind != InvokeKind.Method))
                .Distinct()
                .ToList();
            if (members.GroupBy(static m => m.MemberId).FirstOrDefault(static g => g.Count() > 1) is { } duplicates)
            {
                throw new CsComException(
                    $"The members {string.Join(" and ", duplicates.Select(m => $"'{type.Name}.{m.Name}'"))} have the same [DispId] {duplicates.Key}.");
            }
        }

        // The members of an interface of a type library have different names, which are not case-sensitive: the overloads
        // of a method get the names that TlbExp gives them, M_2, M_3 and so on, in the order of their declarations.
        private void RenameOverloads(Type type, List<(MethodInfo Method, string Name, InvokeKind Kind)> read)
        {
            var used = new HashSet<string>(read.Select(static r => r.Name), StringComparer.OrdinalIgnoreCase);
            var first = new Dictionary<string, (string Name, bool IsProperty)>(StringComparer.OrdinalIgnoreCase);
            var accessors = new HashSet<(string Name, InvokeKind Kind)>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < read.Count; i++)
            {
                var (method, name, kind) = read[i];
                bool isProperty = kind != InvokeKind.Method;
                if (isProperty && !accessors.Add((name, kind)))
                {
                    throw new CsComException($"The property '{type.Name}.{name}' has overloaded accessors, which a type library cannot describe.");
                }

                if (!first.TryGetValue(name, out var previous))
                {
                    first.Add(name, (name, isProperty));
                    continue;
                }

                if (isProperty != previous.IsProperty)
                {
                    throw new CsComException($"'{type.Name}.{name}' is both a property and a method, which have the same name in the type library.");
                }

                if (isProperty)
                {
                    // The accessors of one property share its name.
                    if (previous.Name != name)
                    {
                        throw new CsComException(
                            $"The properties '{type.Name}.{previous.Name}' and '{type.Name}.{name}' have the same name in the type library, where the names are not case-sensitive.");
                    }

                    continue;
                }

                int number = counts.GetValueOrDefault(name, 1) + 1;
                counts[name] = number;
                string renamed = $"{name}_{number}";
                if (!used.Add(renamed))
                {
                    throw new CsComException($"The overload of '{type.Name}.{name}' cannot be exported as '{renamed}', which is the name of another member.");
                }

                LogOverload($"{type.Name}.{name}", renamed);
                read[i] = (method, renamed, kind);
            }
        }

        // An interface without [TypeLibType] is marked oleautomation when the type library marshaller can marshal it: its
        // members return HRESULTs and have parameters of the Automation types, and so do those of the interfaces of the
        // library that it derives from. A registered type library makes that marshaller the proxy of the interfaces that
        // are marked, so an interface with other members must not be. Dual interfaces are always marked.
        private void MarkAutomationInterfaces()
        {
            var incompatible = new Dictionary<InterfaceDefinition, string?>();
            foreach (var definition in _unmarkedInterfaces)
            {
                string? member = FindIncompatibleMember(definition);
                if (definition.InterfaceKind == InterfaceKind.Dual)
                {
                    if (member is not null)
                    {
                        LogIncompatibleDualInterface(definition.Name, member);
                    }
                }
                else if (member is null)
                {
                    definition.Flags |= Model.TypeFlags.OleAutomation;
                }
            }

            string? FindIncompatibleMember(InterfaceDefinition definition)
            {
                if (incompatible.TryGetValue(definition, out string? member))
                {
                    return member;
                }

                var method = definition.Methods.FirstOrDefault(static m => !m.ReturnsHResult || !m.Parameters.All(static p => IsAutomationType(p.Type)));
                member = method is not null ? $"{definition.Name}.{method.OriginalName}"
                    : definition.BaseInterface is UserDefinedTypeReference { Definition: InterfaceDefinition { ImportedFrom: null } baseDefinition } ? FindIncompatibleMember(baseDefinition)
                    : null;
                incompatible[definition] = member;
                return member;
            }
        }

        // The types that the type library marshaller marshals: the Automation types, the enumerations and the interface
        // pointers, SAFEARRAYs of them, and pointers to them for the parameters that are passed by reference. Structures
        // and GUIDs only count by reference and in SAFEARRAYs: Automation passes its structures by reference, and whether
        // the marshaller takes them by value is not documented.
        private static bool IsAutomationType(TypeReference type)
        {
            return type switch
            {
                PointerTypeReference { ElementType: PointerTypeReference { ElementType: var pointee } } => IsInterface(pointee),
                PointerTypeReference { ElementType: var pointee } => IsInterface(pointee) || IsValue(pointee) || IsStructure(pointee),
                _ => IsValue(type),
            };

            static bool IsStructure(TypeReference type) =>
                type is UserDefinedTypeReference { Definition: RecordDefinition } or WellKnownTypeReference { Type: WellKnownType.Guid };

            static bool IsInterface(TypeReference type) =>
                type is UserDefinedTypeReference { Definition: InterfaceDefinition or CoClassDefinition } or WellKnownTypeReference { Type: WellKnownType.IEnumVariant };

            static bool IsValue(TypeReference type) => type switch
            {
                PrimitiveTypeReference primitive => primitive.VarType is VarEnum.VT_I1 or VarEnum.VT_UI1 or VarEnum.VT_I2 or VarEnum.VT_UI2 or VarEnum.VT_I4 or VarEnum.VT_UI4
                    or VarEnum.VT_I8 or VarEnum.VT_UI8 or VarEnum.VT_INT or VarEnum.VT_UINT or VarEnum.VT_R4 or VarEnum.VT_R8 or VarEnum.VT_CY or VarEnum.VT_DATE
                    or VarEnum.VT_BSTR or VarEnum.VT_BOOL or VarEnum.VT_DECIMAL or VarEnum.VT_ERROR or VarEnum.VT_VARIANT or VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH,
                SafeArrayTypeReference array => IsStructure(array.ElementType) || IsAutomationType(array.ElementType),
                UserDefinedTypeReference { Definition: EnumDefinition } => true,
                _ => false,
            };
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
