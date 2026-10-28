using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Replaces the references to type aliases with the aliased types and removes the aliases from the library.
/// </summary>
/// <remarks>
/// MIDL gives anonymous enums, structs and unions names like <c>__MIDL___MIDL_itf_scrrun_0000_0000_0001</c>
/// and declares a typedef for them. Such types are renamed after their first typedef, as TlbImp does.
/// </remarks>
public sealed partial class AliasResolver : IModelTransformer
{
    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        var aliases = library.Types.OfType<AliasDefinition>().ToList();
        if (aliases.Count == 0)
        {
            return;
        }

        var takenNames = new HashSet<(TypeLibraryReference?, string)>(library.Types.Select(static t => (t.ImportedFrom, t.Name)));
        foreach (var alias in aliases)
        {
            if (alias.Target is UserDefinedTypeReference { Definition: { } target } && IsAnonymous(target) && target.Name == target.OriginalName)
            {
                takenNames.Remove((alias.ImportedFrom, alias.Name));
                if (takenNames.Add((target.ImportedFrom, alias.Name)))
                {
                    LogRenaming(context.Logger, target.Name, alias.Name);
                    target.Name = alias.Name;
                    target.Documentation ??= alias.Documentation;
                }
            }
        }

        var resolved = new Dictionary<AliasDefinition, TypeReference>();
        TypeReferenceRewriter.Rewrite(library, type => type is UserDefinedTypeReference { Definition: AliasDefinition alias } ? Resolve(alias, resolved, []) : type);
        library.Types.RemoveAll(static t => t is AliasDefinition);
    }

    private static TypeReference Resolve(AliasDefinition alias, Dictionary<AliasDefinition, TypeReference> resolved, HashSet<AliasDefinition> visiting)
    {
        if (resolved.TryGetValue(alias, out var target))
        {
            return target;
        }

        if (!visiting.Add(alias))
        {
            throw new InvalidOperationException($"The type alias '{alias.Name}' is recursive.");
        }

        target = alias.Target is UserDefinedTypeReference { Definition: AliasDefinition inner } ? Resolve(inner, resolved, visiting) : alias.Target;
        resolved[alias] = target;
        return target;
    }

    private static bool IsAnonymous(TypeDefinition definition) =>
        definition is EnumDefinition or RecordDefinition && definition.OriginalName.StartsWith("__MIDL", StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Renaming '{Name}' to '{NewName}'")]
    private static partial void LogRenaming(ILogger logger, string name, string newName);
}
