using CsCom.Model;

namespace CsCom.CodeGeneration;

/// <summary>
/// The libraries that are generated together into one project, each into its own namespace: the namespaces of the types of
/// all the libraries and the event interfaces of all their coclasses.
/// </summary>
internal sealed class EmitScope
{
    /// <summary>The namespaces of the types of all the libraries.</summary>
    public required IReadOnlyDictionary<TypeDefinition, string> Namespaces { get; init; }

    /// <summary>The event (source) interfaces of the coclasses of all the libraries.</summary>
    public required IReadOnlySet<InterfaceDefinition> EventSources { get; init; }

    /// <summary>Whether the support code is emitted with this library; it is emitted once for all the libraries.</summary>
    public bool EmitSupportCode { get; init; }
}
