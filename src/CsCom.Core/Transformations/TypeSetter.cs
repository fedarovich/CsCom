using System.Runtime.InteropServices;
using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using CsCom.Output;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Changes the type of the members or the parameters that match selectors to a type of the library, e.g. a <c>long</c>
/// parameter to the enum that type library meant; the <c>SetType</c> element of configuration files.
/// </summary>
/// <remarks>
/// <para>
/// Without a parameter selector, the type of the member itself changes: the return value of methods and functions (their
/// <c>[retval]</c> parameter), the value of property accessors, and the type of dispinterface properties and structure fields.
/// Parameters passed by reference keep their pointer, e.g. <c>long*</c> becomes <c>Color*</c>.
/// </para>
/// <para>
/// Vtable and dual interfaces, module functions and structures must keep their binary layout, so only compatible types can be
/// changed: 4-byte integers and enums to enums, interface pointers to interfaces, dispinterfaces and coclasses, and structures
/// to structures of the same size. The members of dispinterfaces are passed in <c>VARIANT</c>s, so their types can change freely.
/// </para>
/// </remarks>
public sealed partial class TypeSetter : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TypeSetter"/> class.
    /// </summary>
    /// <param name="type">Matches the types; all types when <see langword="null"/>.</param>
    /// <param name="member">Matches the names of the members.</param>
    /// <param name="parameter">Matches the names of the parameters; the types of the members change when <see langword="null"/>.</param>
    /// <param name="to">The name of the new type: an enum, a structure, an interface, a dispinterface or a coclass of the library.</param>
    public TypeSetter(TypeSelector? type, NameSelector member, NameSelector? parameter, string to)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentException.ThrowIfNullOrEmpty(to);
        Type = type;
        Member = member;
        Parameter = parameter;
        To = to;
    }

    private enum Layout
    {
        Other,
        Int32,
        InterfacePointer,
        Record,
    }

    /// <summary>Gets the selector of the types; all types when <see langword="null"/>.</summary>
    public TypeSelector? Type { get; }

    /// <summary>Gets the selector of the members.</summary>
    public NameSelector Member { get; }

    /// <summary>Gets the selector of the parameters, if any.</summary>
    public NameSelector? Parameter { get; }

    /// <summary>Gets the name of the new type.</summary>
    public string To { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        var target = FindTarget(library);
        TypeReference value = target is InterfaceDefinition or CoClassDefinition
            ? new PointerTypeReference(new UserDefinedTypeReference(target))
            : new UserDefinedTypeReference(target);

        bool matched = false;
        foreach (var type in library.Types.Where(t => ModelSelectors.IsMatch(Type, t)))
        {
            foreach (var (location, get, set, isParameter) in GetTargets(type).ToList())
            {
                matched = true;
                set(Change(get(), value, type, location, isParameter));
                LogChanging(context.Logger, location, To);
            }
        }

        if (!matched)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(Type, ("Member", Member), ("Parameter", Parameter)));
        }
    }

    internal static TypeSetter Create(ConfigurationElement element) => new(
        element.GetTypeSelector(),
        element.GetRequiredSelector("Member"),
        element.GetSelector("Parameter"),
        element.GetRequiredAttribute("To"));

    private TypeDefinition FindTarget(TypeLibrary library)
    {
        var targets = library.Types
            .Where(t => t.Name == To && t is EnumDefinition or RecordDefinition or InterfaceDefinition or CoClassDefinition)
            .ToList();
        return targets switch
        {
            [var target] => target,
            [] => throw new CsComException($"SetType: the type '{To}' was not found."),
            _ => throw new CsComException($"SetType: the name '{To}' is ambiguous; it is used by {string.Join(", ", targets.Select(static t => t.ImportedFrom?.Name ?? "the library"))}."),
        };
    }

    // The types to change, with a description of their location and accessors; parameters can be passed by reference.
    private IEnumerable<(string Location, Func<TypeReference> Get, Action<TypeReference> Set, bool IsParameter)> GetTargets(TypeDefinition type)
    {
        foreach (var method in ModelSelectors.GetMethods(type).Where(m => Member.IsMatch(m.Name)))
        {
            if (Parameter is not null)
            {
                foreach (var parameter in method.Parameters.Where(p => Parameter.IsMatch(p.Name)))
                {
                    yield return ($"{type.Name}.{method.Name}({parameter.Name})", () => parameter.Type, t => parameter.Type = t, true);
                }

                continue;
            }

            // The value of a property setter is its last parameter; otherwise the return value.
            if (method.InvokeKind is InvokeKind.PropertyPut or InvokeKind.PropertyPutRef && method.Parameters.Count > 0)
            {
                var value = method.Parameters[^1];
                yield return ($"{type.Name}.{method.Name}({value.Name})", () => value.Type, t => value.Type = t, true);
            }
            else if (method.RetvalParameter is { } retval && method.ReturnsHResult)
            {
                yield return ($"{type.Name}.{method.Name}", () => retval.Type, t => retval.Type = t, true);
            }
            else if (!method.ReturnsHResult && method.ReturnType is not PrimitiveTypeReference { VarType: VarEnum.VT_VOID })
            {
                yield return ($"{type.Name}.{method.Name}", () => method.ReturnType, t => method.ReturnType = t, false);
            }
        }

        if (Parameter is not null)
        {
            yield break;
        }

        switch (type)
        {
            case InterfaceDefinition @interface:
                foreach (var property in @interface.Properties.Where(p => Member.IsMatch(p.Name)))
                {
                    yield return ($"{type.Name}.{property.Name}", () => property.Type, t => property.Type = t, false);
                }

                break;
            case RecordDefinition record:
                foreach (var field in record.Fields.Where(f => Member.IsMatch(f.Name)))
                {
                    yield return ($"{type.Name}.{field.Name}", () => field.Type, t => field.Type = t, false);
                }

                break;
        }
    }

    // Replaces the value type, keeping the pointer of a parameter passed by reference.
    private TypeReference Change(TypeReference type, TypeReference value, TypeDefinition owner, string location, bool isParameter)
    {
        TypeReference old = type;
        bool byRef = false;
        if (isParameter && TypeMapper.TryGetByRefElement(type, out var element))
        {
            old = element;
            byRef = true;
        }

        if (owner is not InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } && !IsCompatible(old, value))
        {
            string where = owner switch
            {
                InterfaceDefinition { InterfaceKind: InterfaceKind.Dual } => "the vtable of a dual interface",
                InterfaceDefinition => "a vtable interface",
                ModuleDefinition => "a module function",
                _ => "a structure",
            };
            throw new CsComException(
                $"SetType: '{location}' is {ModelPrinter.FormatType(old)}, which cannot be changed to '{To}' in {where}, because their layouts differ.");
        }

        return byRef ? new PointerTypeReference(value) : value;
    }

    private static bool IsCompatible(TypeReference old, TypeReference value)
    {
        var layout = GetLayout(old);
        return layout != Layout.Other && layout == GetLayout(value) && (layout != Layout.Record || GetRecordSize(old) == GetRecordSize(value));
    }

    private static Layout GetLayout(TypeReference type) => type switch
    {
        PrimitiveTypeReference { VarType: VarEnum.VT_I4 or VarEnum.VT_UI4 or VarEnum.VT_INT or VarEnum.VT_UINT } => Layout.Int32,
        UserDefinedTypeReference { Definition: EnumDefinition } => Layout.Int32,
        PrimitiveTypeReference { VarType: VarEnum.VT_UNKNOWN or VarEnum.VT_DISPATCH } => Layout.InterfacePointer,
        PointerTypeReference { ElementType: var element } when TypeMapper.IsInterfaceLike(element) => Layout.InterfacePointer,
        UserDefinedTypeReference { Definition: RecordDefinition } => Layout.Record,
        _ => Layout.Other,
    };

    private static int GetRecordSize(TypeReference type) => ((RecordDefinition)((UserDefinedTypeReference)type).Definition).Size;

    [LoggerMessage(Level = LogLevel.Debug, Message = "SetType: changing the type of '{Location}' to '{To}'")]
    private static partial void LogChanging(ILogger logger, string location, string to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SetType: nothing matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
