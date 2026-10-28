using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CsCom.Transformations;

namespace CsCom.Configuration;

/// <summary>
/// An element of the configuration file, passed to the factories of the transformers. It reads the attributes and the child
/// elements and reports errors with their location in the file.
/// </summary>
/// <remarks>
/// The attributes, child elements and text that the factory does not read are reported as errors, so that typos are not
/// ignored. Attributes with a namespace, e.g. <c>xsi:noNamespaceSchemaLocation</c>, are allowed everywhere.
/// </remarks>
public sealed class ConfigurationElement
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly HashSet<XName> _readAttributes = [];
    private readonly Dictionary<XElement, ConfigurationElement> _children = [];
    private bool _valueRead;

    internal ConfigurationElement(XElement element, string? filePath)
    {
        Element = element;
        FilePath = filePath;
    }

    /// <summary>Gets the XML element.</summary>
    public XElement Element { get; }

    /// <summary>Gets the local name of the element.</summary>
    public string Name => Element.Name.LocalName;

    /// <summary>Gets the path of the configuration file, or <see langword="null"/> if it was not loaded from a file.</summary>
    public string? FilePath { get; }

    /// <summary>Gets the text of the element.</summary>
    public string Value
    {
        get
        {
            _valueRead = true;
            return Element.Value;
        }
    }

    /// <summary>Gets the value of an attribute.</summary>
    /// <returns>The value, or <see langword="null"/> if the attribute is missing.</returns>
    public string? GetAttribute(string name)
    {
        _readAttributes.Add(name);
        return Element.Attribute(name)?.Value;
    }

    /// <summary>Gets the value of an attribute that must be specified.</summary>
    /// <exception cref="CsComException">The attribute is missing.</exception>
    public string GetRequiredAttribute(string name) =>
        GetAttribute(name) ?? throw CreateError($"The attribute '{name}' is required by <{Name}>.");

    /// <summary>Gets the value of a Boolean attribute: <c>true</c> or <c>false</c>, in any case.</summary>
    /// <exception cref="CsComException">The value is not valid.</exception>
    public bool GetBoolean(string name, bool defaultValue)
    {
        string? value = GetAttribute(name);
        if (value is null)
        {
            return defaultValue;
        }

        return bool.TryParse(value, out bool result)
            ? result
            : throw CreateError(Element.Attribute(name)!, FilePath, $"'{value}' is not a valid value of the attribute '{name}': true or false is expected.");
    }

    /// <summary>Gets the value of an enum attribute, given by the name of an enum member in any case.</summary>
    /// <exception cref="CsComException">The value is not valid.</exception>
    public TEnum GetEnum<TEnum>(string name, TEnum defaultValue)
        where TEnum : struct, Enum => GetEnum<TEnum>(name) ?? defaultValue;

    /// <summary>Gets the value of an enum attribute, given by the name of an enum member in any case.</summary>
    /// <returns>The value, or <see langword="null"/> if the attribute is missing.</returns>
    /// <exception cref="CsComException">The value is not valid.</exception>
    public TEnum? GetEnum<TEnum>(string name)
        where TEnum : struct, Enum
    {
        string? value = GetAttribute(name);
        if (value is null)
        {
            return null;
        }

        // Enum.TryParse also accepts numbers and lists of flags, which are not member names.
        if (value.Length > 0 && char.IsLetter(value[0]) && !value.Contains(',') && Enum.TryParse(value, ignoreCase: true, out TEnum result) && Enum.IsDefined(result))
        {
            return result;
        }

        throw CreateError(
            Element.Attribute(name)!,
            FilePath,
            $"'{value}' is not a valid value of the attribute '{name}': one of {string.Join(", ", Enum.GetNames<TEnum>())} is expected.");
    }

    /// <summary>
    /// Gets a regular expression given by an attribute. It is case-sensitive, culture-invariant and has a timeout of a second.
    /// </summary>
    /// <returns>The regular expression, or <see langword="null"/> if the attribute is missing.</returns>
    /// <exception cref="CsComException">The value is not a valid regular expression.</exception>
    public Regex? GetRegex(string name)
    {
        string? value = GetAttribute(name);
        if (value is null)
        {
            return null;
        }

        try
        {
            return new Regex(value, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException e)
        {
            throw CreateError(Element.Attribute(name)!, FilePath, $"'{value}' is not a valid regular expression in the attribute '{name}': {e.Message}", e);
        }
    }

    /// <summary>Gets a regular expression given by an attribute that must be specified, see <see cref="GetRegex"/>.</summary>
    /// <exception cref="CsComException">The attribute is missing or its value is not a valid regular expression.</exception>
    public Regex GetRequiredRegex(string name) =>
        GetRegex(name) ?? throw CreateError($"The attribute '{name}' is required by <{Name}>.");

    /// <summary>Gets the value of an integer attribute that must be specified, within a range.</summary>
    /// <exception cref="CsComException">The attribute is missing, or its value is not an integer within the range.</exception>
    public int GetRequiredInt32(string name, int minimum, int maximum)
    {
        string value = GetRequiredAttribute(name);
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int result) && result >= minimum && result <= maximum
            ? result
            : throw CreateError(Element.Attribute(name)!, FilePath, $"'{value}' is not a valid value of the attribute '{name}': an integer from {minimum} to {maximum} is expected.");
    }

    /// <summary>
    /// Gets the value of a flags enum attribute: names of enum members in any case, separated by <c>,</c> or <c>|</c>.
    /// </summary>
    /// <returns>The combined value, or <see langword="null"/> if the attribute is missing.</returns>
    /// <exception cref="CsComException">A name is not valid.</exception>
    public TEnum? GetFlags<TEnum>(string name)
        where TEnum : struct, Enum
    {
        string? value = GetAttribute(name);
        if (value is null)
        {
            return null;
        }

        ulong result = 0;
        foreach (string part in value.Split([',', '|'], StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0 || !char.IsLetter(part[0]) || !Enum.TryParse(part, ignoreCase: true, out TEnum flag) || !Enum.IsDefined(flag) || ToUInt64(flag) == 0)
            {
                var names = Enum.GetValues<TEnum>().Where(static v => ToUInt64(v) != 0).Select(static v => v.ToString().ToLowerInvariant());
                throw CreateError(
                    Element.Attribute(name)!,
                    FilePath,
                    $"'{part}' is not a valid value of the attribute '{name}': a list of {string.Join(", ", names)} is expected.");
            }

            result |= ToUInt64(flag);
        }

        return (TEnum)Enum.ToObject(typeof(TEnum), result);

        static ulong ToUInt64(TEnum value) => Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Gets a selector of names given by an attribute: a regular expression (see <see cref="GetRegex"/>), or an exact name in the
    /// attribute with the <c>Name</c> suffix, e.g. <c>Type</c> or <c>TypeName</c>.
    /// </summary>
    /// <returns>The selector, or <see langword="null"/> if both attributes are missing.</returns>
    /// <exception cref="CsComException">Both attributes are specified, or the regular expression is not valid.</exception>
    public NameSelector? GetSelector(string name)
    {
        string exactName = name + "Name";
        var pattern = GetRegex(name);
        string? exact = GetAttribute(exactName);
        if (pattern is not null && exact is not null)
        {
            throw CreateError($"The attributes '{name}' and '{exactName}' cannot be combined in <{Name}>.");
        }

        return pattern is not null ? NameSelector.FromPattern(pattern) : exact is not null ? NameSelector.FromName(exact) : null;
    }

    /// <summary>Gets a selector of names that must be specified, see <see cref="GetSelector"/>.</summary>
    /// <exception cref="CsComException">The selector is missing or not valid.</exception>
    public NameSelector GetRequiredSelector(string name) =>
        GetSelector(name) ?? throw CreateError($"The attribute '{name}' or '{name}Name' is required by <{Name}>.");

    /// <summary>
    /// Gets a selector of types given by the <c>Type</c> or <c>TypeName</c> attribute and the <c>Kind</c> attribute, a list of
    /// <see cref="TypeKinds"/> separated by <c>,</c> or <c>|</c>.
    /// </summary>
    /// <param name="required">Whether a name or a kind must be specified.</param>
    /// <returns>The selector, or <see langword="null"/> if none of the attributes is specified.</returns>
    /// <exception cref="CsComException">The attributes are not valid, or missing although required.</exception>
    public TypeSelector? GetTypeSelector(bool required = false)
    {
        var name = GetSelector("Type");
        var kinds = GetFlags<TypeKinds>("Kind");
        if (name is null && kinds is null)
        {
            return required ? throw CreateError($"The attribute 'Type', 'TypeName' or 'Kind' is required by <{Name}>.") : null;
        }

        return new TypeSelector(name, kinds ?? TypeKinds.All);
    }

    /// <summary>Gets the child elements.</summary>
    /// <param name="name">The name of the elements, or <see langword="null"/> for all child elements.</param>
    public IEnumerable<ConfigurationElement> Elements(string? name = null)
    {
        foreach (XElement child in name is null ? Element.Elements() : Element.Elements(name))
        {
            if (!_children.TryGetValue(child, out var element))
            {
                element = new ConfigurationElement(child, FilePath);
                _children.Add(child, element);
            }

            yield return element;
        }
    }

    /// <summary>Creates an exception that reports an error at the location of the element.</summary>
    public CsComException CreateError(string message) => CreateError(Element, FilePath, message);

    internal CsComException CreateError(string message, Exception innerException) => CreateError(Element, FilePath, message, innerException);

    internal static CsComException CreateError(XObject source, string? filePath, string message, Exception? innerException = null)
    {
        var lineInfo = (IXmlLineInfo)source;
        string location = (lineInfo.HasLineInfo(), filePath) switch
        {
            (true, null) => $"Line {lineInfo.LineNumber}, position {lineInfo.LinePosition}: ",
            (true, _) => $"{filePath}({lineInfo.LineNumber},{lineInfo.LinePosition}): ",
            (false, null) => string.Empty,
            (false, _) => $"{filePath}: ",
        };
        return new CsComException(location + message, innerException);
    }

    // Reports the first attribute, child element or text that was not read, including those of the child elements.
    internal void ThrowIfUnused()
    {
        foreach (XAttribute attribute in Element.Attributes())
        {
            if (!attribute.IsNamespaceDeclaration && attribute.Name.Namespace == XNamespace.None && !_readAttributes.Contains(attribute.Name))
            {
                throw CreateError(attribute, FilePath, $"The attribute '{attribute.Name.LocalName}' is not supported by <{Name}>.");
            }
        }

        foreach (XNode node in Element.Nodes())
        {
            switch (node)
            {
                case XElement child when _children.TryGetValue(child, out var element):
                    element.ThrowIfUnused();
                    break;
                case XElement child:
                    throw CreateError(child, FilePath, $"The element <{child.Name.LocalName}> is not supported by <{Name}>.");
                case XText text when !_valueRead && !string.IsNullOrWhiteSpace(text.Value):
                    throw CreateError(text, FilePath, $"<{Name}> does not support text.");
            }
        }
    }
}
