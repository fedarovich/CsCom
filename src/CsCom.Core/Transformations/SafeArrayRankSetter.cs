using CsCom.Configuration;
using CsCom.Model;
using Microsoft.Extensions.Logging;

namespace CsCom.Transformations;

/// <summary>
/// Sets the number of dimensions of the <c>SAFEARRAY</c>s of the members or the parameters that match selectors, which type
/// libraries do not record; the <c>SetSafeArrayRank</c> element of configuration files.
/// </summary>
/// <remarks>
/// With the default <c>array</c> representation of <c>SAFEARRAY</c>s, a rank of 2 or 3 gives <c>T[,]</c> or <c>T[,,]</c>
/// instead of <c>T[]</c>, and a <c>SAFEARRAY</c> with another rank throws a <see cref="System.Runtime.InteropServices.SafeArrayRankMismatchException"/>.
/// The other representations already support any rank. Without a parameter selector, the rank is set for all parameters and
/// the return values of the matching methods, and for the matching dispinterface properties.
/// </remarks>
public sealed partial class SafeArrayRankSetter : IModelTransformer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SafeArrayRankSetter"/> class.
    /// </summary>
    /// <param name="type">Matches the types; all types when <see langword="null"/>.</param>
    /// <param name="member">Matches the names of the members; all members when <see langword="null"/>.</param>
    /// <param name="parameter">Matches the names of the parameters; the whole members when <see langword="null"/>.</param>
    /// <param name="rank">The number of dimensions, from 1 to 3.</param>
    public SafeArrayRankSetter(TypeSelector? type, NameSelector? member, NameSelector? parameter, int rank)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rank, 3);
        Type = type;
        Member = member;
        Parameter = parameter;
        Rank = rank;
    }

    /// <summary>Gets the selector of the types; all types when <see langword="null"/>.</summary>
    public TypeSelector? Type { get; }

    /// <summary>Gets the selector of the members; all members when <see langword="null"/>.</summary>
    public NameSelector? Member { get; }

    /// <summary>Gets the selector of the parameters; the whole members when <see langword="null"/>.</summary>
    public NameSelector? Parameter { get; }

    /// <summary>Gets the number of dimensions.</summary>
    public int Rank { get; }

    /// <inheritdoc/>
    public void Transform(TypeLibrary library, TransformationContext context)
    {
        int count = 0;
        foreach (var type in library.Types.Where(t => ModelSelectors.IsMatch(Type, t)))
        {
            foreach (var method in ModelSelectors.GetMethods(type).Where(m => ModelSelectors.IsMatch(Member, m.Name)))
            {
                foreach (var parameter in method.Parameters.Where(p => ModelSelectors.IsMatch(Parameter, p.Name)))
                {
                    parameter.Type = SetRank(parameter.Type, ref count);
                }

                if (Parameter is null)
                {
                    method.ReturnType = SetRank(method.ReturnType, ref count);
                }
            }

            if (Parameter is null && type is InterfaceDefinition @interface)
            {
                foreach (var property in @interface.Properties.Where(p => ModelSelectors.IsMatch(Member, p.Name)))
                {
                    property.Type = SetRank(property.Type, ref count);
                }
            }
        }

        if (count == 0)
        {
            LogNoMatch(context.Logger, ModelSelectors.Describe(Type, ("Member", Member), ("Parameter", Parameter)));
        }
        else
        {
            LogSetting(context.Logger, Rank, count);
        }
    }

    internal static SafeArrayRankSetter Create(ConfigurationElement element) => new(
        element.GetTypeSelector(),
        element.GetSelector("Member"),
        element.GetSelector("Parameter"),
        element.GetRequiredInt32("Rank", 1, 3));

    // Sets the rank of a SAFEARRAY, also when it is passed by reference.
    private TypeReference SetRank(TypeReference type, ref int count)
    {
        switch (type)
        {
            case SafeArrayTypeReference array:
                count++;
                return array with { Rank = Rank };
            case PointerTypeReference pointer:
                var element = SetRank(pointer.ElementType, ref count);
                return ReferenceEquals(element, pointer.ElementType) ? pointer : pointer with { ElementType = element };
            default:
                return type;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "SetSafeArrayRank: setting the rank {Rank} of {Count} SAFEARRAYs")]
    private static partial void LogSetting(ILogger logger, int rank, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SetSafeArrayRank: no SAFEARRAY matches {Selectors}")]
    private static partial void LogNoMatch(ILogger logger, string selectors);
}
