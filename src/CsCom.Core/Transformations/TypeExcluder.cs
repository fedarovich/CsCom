using System.Runtime.InteropServices;
using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Removes the types that match a selector from the generated code; the <c>ExcludeType</c> element of configuration files.
/// </summary>
/// <remarks>
/// <para>
/// The references to the removed types are replaced: interfaces, dispinterfaces and coclasses by <c>IDispatch*</c> if they are
/// dispatchable and by <c>IUnknown*</c> otherwise, enums by <c>long</c>, and pointers to structures by <c>void*</c>.
/// </para>
/// <para>
/// Structures and unions that are used by value cannot be removed, because they make up the layout of other types, and
/// neither can the base interfaces of the interfaces that are kept.
/// </para>
/// </remarks>
public sealed partial class TypeExcluder : IModelTransformer
{
    private static readonly PrimitiveTypeReference Dispatch = new(VarEnum.VT_DISPATCH);
    private static readonly PrimitiveTypeReference Unknown = new(VarEnum.VT_UNKNOWN);
    private static readonly PrimitiveTypeReference Long = new(VarEnum.VT_I4);
    private static readonly PrimitiveTypeReference Void = new(VarEnum.VT_VOID);

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeExcluder"/> class.
    /// </summary>
    /// <param name="type">Matches the types to remove.</param>
    public TypeExcluder(TypeSelector type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
    }

    /// <summary>Gets the selector of the types to remove.</summary>
    public TypeSelector Type { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        var excluded = library.Types.Where(Type.IsMatch).ToHashSet();
        if (excluded.Count == 0)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(Type));
            return;
        }

        foreach (var (location, reference) in GetReferences(library, excluded))
        {
            if (reference is InterfaceBase { Definition: var baseInterface } && excluded.Contains(baseInterface))
            {
                throw new CsComException($"The interface '{baseInterface.Name}' cannot be excluded, because '{location}' derives from it.");
            }

            if (reference is TypeReference type && FindRecordByValue(type, excluded) is { } record)
            {
                throw new CsComException(
                    $"The {(record.IsUnion ? "union" : "structure")} '{record.Name}' cannot be excluded, because '{location}' uses it.");
            }
        }

        foreach (var type in excluded)
        {
            LogExcluding(context.Logger, type.Name);
        }

        library.Types.RemoveAll(excluded.Contains);
        TypeReferenceRewriter.Rewrite(library, type => Replace(type, excluded), deep: false);
    }

    internal static TypeExcluder Create(ConfigurationElement element) => new(element.GetTypeSelector(required: true)!);

    // Replaces the references to the removed types; a pointer to an interface collapses into IUnknown* or IDispatch*.
    private static TypeReference Replace(TypeReference type, HashSet<TypeDefinition> excluded)
    {
        switch (type)
        {
            case PointerTypeReference { ElementType: UserDefinedTypeReference { Definition: InterfaceDefinition or CoClassDefinition } user } when excluded.Contains(user.Definition):
                return ReplaceDefinition(user.Definition);
            case PointerTypeReference { ElementType: UserDefinedTypeReference { Definition: RecordDefinition record } } when excluded.Contains(record):
                return new PointerTypeReference(Void);
            case UserDefinedTypeReference { Definition: var definition } when excluded.Contains(definition):
                return ReplaceDefinition(definition);
            case PointerTypeReference pointer:
                var element = Replace(pointer.ElementType, excluded);
                return ReferenceEquals(element, pointer.ElementType) ? pointer : pointer with { ElementType = element };
            case SafeArrayTypeReference array:
                var arrayElement = Replace(array.ElementType, excluded);
                return ReferenceEquals(arrayElement, array.ElementType) ? array : array with { ElementType = arrayElement };
            case FixedArrayTypeReference array:
                var fixedElement = Replace(array.ElementType, excluded);
                return ReferenceEquals(fixedElement, array.ElementType) ? array : array with { ElementType = fixedElement };
            default:
                return type;
        }
    }

    private static TypeReference ReplaceDefinition(TypeDefinition definition) => definition switch
    {
        EnumDefinition => Long,
        _ => IsDispatchable(definition) ? Dispatch : Unknown,
    };

    private static bool IsDispatchable(TypeDefinition definition) => definition switch
    {
        InterfaceDefinition { InterfaceKind: InterfaceKind.Dual or InterfaceKind.Dispatch } => true,
        InterfaceDefinition { BaseInterface: PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } } => true,
        InterfaceDefinition { BaseInterface: UserDefinedTypeReference { Definition: var baseInterface } } => IsDispatchable(baseInterface),
        CoClassDefinition coClass => coClass.DefaultInterface?.Interface switch
        {
            UserDefinedTypeReference { Definition: var defaultInterface } => IsDispatchable(defaultInterface),
            PrimitiveTypeReference { VarType: VarEnum.VT_DISPATCH } => true,
            _ => false,
        },
        _ => false,
    };

    // A structure or union used by value: not through a pointer, which can be replaced by void*.
    private static RecordDefinition? FindRecordByValue(TypeReference type, HashSet<TypeDefinition> excluded) => type switch
    {
        UserDefinedTypeReference { Definition: RecordDefinition record } when excluded.Contains(record) => record,
        SafeArrayTypeReference array => FindRecordByValue(array.ElementType, excluded),
        FixedArrayTypeReference array => FindRecordByValue(array.ElementType, excluded),
        _ => null,
    };

    // The type references of the types that are kept, with a description of where they are used.
    private static IEnumerable<(string Location, object Reference)> GetReferences(TypeLibrary library, HashSet<TypeDefinition> excluded)
    {
        foreach (var type in library.Types.Where(t => !excluded.Contains(t)))
        {
            switch (type)
            {
                case RecordDefinition record:
                    foreach (var field in record.Fields)
                    {
                        yield return ($"{type.Name}.{field.Name}", field.Type);
                    }

                    break;
                case InterfaceDefinition @interface:
                    if (@interface.BaseInterface is UserDefinedTypeReference { Definition: var baseInterface })
                    {
                        yield return (type.Name, new InterfaceBase(baseInterface));
                    }

                    foreach (var method in @interface.Methods)
                    {
                        foreach (var reference in GetReferences(method))
                        {
                            yield return ($"{type.Name}.{method.Name}", reference);
                        }
                    }

                    foreach (var property in @interface.Properties)
                    {
                        yield return ($"{type.Name}.{property.Name}", property.Type);
                    }

                    break;
                case ModuleDefinition module:
                    foreach (var function in module.Functions)
                    {
                        foreach (var reference in GetReferences(function))
                        {
                            yield return ($"{type.Name}.{function.Name}", reference);
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<TypeReference> GetReferences(MethodDefinition method) => [method.ReturnType, .. method.Parameters.Select(static p => p.Type)];

    [LoggerMessage(Level = LogLevel.Debug, Message = "ExcludeType: excluding '{Name}'")]
    private static partial void LogExcluding(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ExcludeType: no type matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);

    // The base interface of an interface that is kept.
    private sealed record InterfaceBase(TypeDefinition Definition);
}
