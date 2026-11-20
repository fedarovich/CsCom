using CsCom.CodeGeneration;
using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Changes the marshalling options for the members or the parameters whose names match regular expressions; the
/// <c>SetMarshalling</c> element of configuration files.
/// </summary>
/// <remarks>
/// <para>
/// Without a parameter expression, the options change for all parameters and the return values of the matching methods and
/// functions, and for the matching properties of dispinterfaces and fields of structures. With it, they change only for the
/// matching parameters, including <c>[retval]</c> parameters.
/// </para>
/// <para>
/// The changes are combined with the changes made by earlier transformers. Only the representation of <c>DATE</c>,
/// <c>CURRENCY</c>, <c>VARIANT_BOOL</c> and <c>BSTR</c> values applies to the fields of structures, and only the
/// representation of <c>SAFEARRAY</c>s to the late-bound wrappers of dispinterfaces; all changes apply to the parameters
/// and return values of vtable and dual interfaces and of module functions. A type that none of the changes applies to
/// does not match. For an element that only changes the marshaller of interface pointers, neither does a parameter or a
/// return value that is no interface pointer, or an interface pointer that is passed in and out, which is always a
/// shared object.
/// </para>
/// <para>
/// Like every transformer, it sees the model as the transformers before it left it. The other changes apply to the
/// matching parameters whatever their types, but an element that only changes the marshaller of interface pointers applies
/// to the interface pointers at its point: a <c>SetType</c> that makes a parameter an interface pointer goes before it.
/// </para>
/// </remarks>
public sealed partial class MarshallingSetter : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MarshallingSetter"/> class.
    /// </summary>
    /// <param name="type">Matches the types; all types when <see langword="null"/>.</param>
    /// <param name="member">Matches the names of the members; all members when <see langword="null"/>.</param>
    /// <param name="parameter">Matches the names of the parameters; the whole members when <see langword="null"/>.</param>
    /// <param name="marshalling">The changes to the marshalling options.</param>
    public MarshallingSetter(TypeSelector? type, NameSelector? member, NameSelector? parameter, MarshallingOverrides marshalling)
    {
        Type = type;
        Member = member;
        Parameter = parameter;
        Marshalling = marshalling;
    }

    /// <summary>Gets the selector of the types; all types when <see langword="null"/>.</summary>
    public TypeSelector? Type { get; }

    /// <summary>Gets the selector of the members; all members when <see langword="null"/>.</summary>
    public NameSelector? Member { get; }

    /// <summary>Gets the selector of the parameters; the whole members when <see langword="null"/>.</summary>
    public NameSelector? Parameter { get; }

    /// <summary>Gets the changes to the marshalling options.</summary>
    public MarshallingOverrides Marshalling { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        // A type that none of the changes applies to does not match, so that the warning tells that the element has no
        // effect. Neither does a parameter or a return value when only the marshaller of interface pointers would change
        // and it is no interface pointer, or one that is passed in and out, which is always a shared object.
        bool interfacesOnly = Marshalling.Options == new MarshallingOptionOverrides();
        bool matched = false;
        foreach (var type in library.Types.Where(t => ModelSelectors.IsMatch(Type, t) && AppliesTo(t)))
        {
            foreach (var method in ModelSelectors.GetMethods(type).Where(m => ModelSelectors.IsMatch(Member, m.Name)))
            {
                foreach (var parameter in method.Parameters.Where(p => ModelSelectors.IsMatch(Parameter, p.Name)))
                {
                    if (interfacesOnly && !SignatureBuilder.HasInterfaceMarshaller(type, method, parameter))
                    {
                        continue;
                    }

                    matched = true;
                    parameter.Marshalling = MarshallingOverrides.Combine(parameter.Marshalling, Marshalling);
                    LogChanging(context.Logger, $"{type.Name}.{method.Name}({parameter.Name})");
                }

                if (Parameter is null && (!interfacesOnly || TypeMapper.IsInterfacePointer(method.ReturnType)))
                {
                    matched = true;
                    method.ReturnMarshalling = MarshallingOverrides.Combine(method.ReturnMarshalling, Marshalling);
                    LogChanging(context.Logger, $"{type.Name}.{method.Name}");
                }
            }

            if (Parameter is not null)
            {
                continue;
            }

            switch (type)
            {
                case InterfaceDefinition @interface:
                    foreach (var property in @interface.Properties.Where(p => ModelSelectors.IsMatch(Member, p.Name)))
                    {
                        matched = true;
                        property.Marshalling = MarshallingOverrides.Combine(property.Marshalling, Marshalling);
                        LogChanging(context.Logger, $"{type.Name}.{property.Name}");
                    }

                    break;
                case RecordDefinition record:
                    foreach (var field in record.Fields.Where(f => ModelSelectors.IsMatch(Member, f.Name)))
                    {
                        matched = true;
                        field.Marshalling = MarshallingOverrides.Combine(field.Marshalling, Marshalling);
                        LogChanging(context.Logger, $"{type.Name}.{field.Name}");
                    }

                    break;
            }
        }

        if (!matched)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(Type, ("Member", Member), ("Parameter", Parameter)));
        }
    }

    // Whether one of the changes has an effect on the members of the type. All of them apply to the parameters and return
    // values of vtable and dual interfaces and of the functions of modules; TypeMapper tells which of them it reads for
    // the late-bound wrappers of dispinterfaces and for the fields of structures.
    private bool AppliesTo(TypeDefinition type) => type switch
    {
        InterfaceDefinition { InterfaceKind: InterfaceKind.Dispatch } => TypeMapper.AppliesToLateBound(Marshalling),
        RecordDefinition => TypeMapper.AppliesToFields(Marshalling),
        _ => true,
    };

    internal static MarshallingSetter Create(ConfigurationElement element)
    {
        var marshalling = new MarshallingOverrides
        {
            Options = new()
            {
                Dates = element.GetEnum<ValueMarshalling>("Dates"),
                Currencies = element.GetEnum<ValueMarshalling>("Currencies"),
                Booleans = element.GetEnum<ValueMarshalling>("Booleans"),
                Bstrs = element.GetEnum<BstrMarshalling>("Bstrs"),
                SafeArrays = element.GetEnum<SafeArrayMarshalling>("SafeArrays"),
            },
            Interfaces = element.GetEnum<InterfaceMarshalling>("Interfaces"),
        };

        if (marshalling == new MarshallingOverrides())
        {
            throw element.CreateError($"At least one of the attributes Dates, Currencies, Booleans, Bstrs, SafeArrays or Interfaces is required by <{element.Name}>.");
        }

        return new MarshallingSetter(element.GetTypeSelector(), element.GetSelector("Member"), element.GetSelector("Parameter"), marshalling);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "SetMarshalling: changing the marshalling of '{Name}'")]
    private static partial void LogChanging(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SetMarshalling: nothing matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
