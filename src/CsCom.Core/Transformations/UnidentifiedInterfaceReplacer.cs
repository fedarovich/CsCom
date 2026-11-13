using System.Runtime.InteropServices;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Replaces the references to the vtable and dual interfaces that have no GUID, which cannot be generated as COM
/// interfaces: an interface declared in a library block without the <c>uuid</c> attribute.
/// </summary>
/// <remarks>
/// The interfaces stay in the model, also those of the other libraries that are generated together, and are skipped when
/// the code is generated. Pointers to them become <c>IDispatch*</c> (if they are dispatchable) or <c>IUnknown*</c>, like those
/// to the types that <see cref="TypeExcluder"/> removes. An interface that derives from one derives from its nearest base
/// interface that has a GUID; the generated interface has placeholders for the vtable slots of the skipped ones.
/// </remarks>
public sealed partial class UnidentifiedInterfaceReplacer : IModelTransformer
{
    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(context);

        var unidentified = new HashSet<TypeDefinition>(ReferenceEqualityComparer.Instance);
        TypeReferenceRewriter.Rewrite(library, reference =>
        {
            if (reference is UserDefinedTypeReference { Definition: InterfaceDefinition { Guid: null, InterfaceKind: not InterfaceKind.Dispatch } definition })
            {
                unidentified.Add(definition);
            }

            return reference;
        });

        if (unidentified.Count == 0)
        {
            return;
        }

        foreach (var @interface in library.Types.OfType<InterfaceDefinition>())
        {
            if (@interface.BaseInterface is UserDefinedTypeReference { Definition: InterfaceDefinition @base } && unidentified.Contains(@base))
            {
                @interface.BaseInterface = FindIdentifiedBase(@base);
            }
        }

        TypeReferenceRewriter.Rewrite(library, type => TypeExcluder.Replace(type, unidentified), deep: false);
        foreach (var definition in unidentified)
        {
            LogReplacing(context.Logger, definition.Name, library.Name, TypeExcluder.IsDispatchable(definition) ? "IDispatch" : "IUnknown");
        }
    }

    // The nearest base interface that is generated: one with a GUID, IDispatch, or none (IUnknown).
    private static TypeReference? FindIdentifiedBase(InterfaceDefinition definition)
    {
        var visited = new HashSet<InterfaceDefinition>(ReferenceEqualityComparer.Instance);
        for (var current = definition; visited.Add(current);)
        {
            switch (current.BaseInterface)
            {
                case UserDefinedTypeReference { Definition: InterfaceDefinition { Guid: null, InterfaceKind: not InterfaceKind.Dispatch } @base }:
                    current = @base;
                    break;
                case PrimitiveTypeReference { VarType: VarEnum.VT_UNKNOWN }:
                    return null;
                default:
                    return current.BaseInterface;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The references of '{Library}' to the interface '{Name}', which has no GUID, are replaced by {Replacement}")]
    private static partial void LogReplacing(ILogger logger, string name, string library, string replacement);
}
