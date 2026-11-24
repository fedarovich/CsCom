using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace CsCom.InteropAssemblies;

/// <summary>
/// Reads the custom attributes of the metadata of assemblies loaded with <see cref="MetadataLoadContext"/>, whose attribute
/// types are found by their names.
/// </summary>
internal static class MetadataAttributes
{
    public const string InteropNamespace = "System.Runtime.InteropServices.";

    public static CustomAttributeData? Find(IList<CustomAttributeData> attributes, string fullName) =>
        attributes.FirstOrDefault(a => a.AttributeType.FullName == fullName);

    public static T? GetArgument<T>(IList<CustomAttributeData> attributes, string fullName) =>
        Find(attributes, fullName) is { ConstructorArguments: [var argument, ..] } && argument.Value is T value ? value : default;

    public static object? GetNamedArgument(CustomAttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(a => a.MemberName == name).TypedValue.Value;

    public static Guid? GetGuid(IList<CustomAttributeData> attributes) =>
        GetArgument<string>(attributes, InteropNamespace + "GuidAttribute") is { } text && Guid.TryParse(text, out var guid) ? guid : null;

    // Default values are metadata constants, or attributes for the types that have no constants.
    public static bool TryGetDefaultValue(ParameterInfo parameter, out object? value)
    {
        if ((parameter.Attributes & ParameterAttributes.HasDefault) != 0)
        {
            value = parameter.RawDefaultValue;
            return true;
        }

        foreach (var attribute in parameter.GetCustomAttributesData())
        {
            switch (attribute.AttributeType.FullName)
            {
                case "System.Runtime.CompilerServices.DateTimeConstantAttribute":
                    value = new DateTime((long)attribute.ConstructorArguments[0].Value!);
                    return true;
                case "System.Runtime.CompilerServices.DecimalConstantAttribute":
                    var arguments = attribute.ConstructorArguments;
                    value = new decimal(
                        ToBits(arguments[4].Value),
                        ToBits(arguments[3].Value),
                        ToBits(arguments[2].Value),
                        Convert.ToByte(arguments[1].Value, CultureInfo.InvariantCulture) != 0,
                        Convert.ToByte(arguments[0].Value, CultureInfo.InvariantCulture));
                    return true;
                case InteropNamespace + "DefaultParameterValueAttribute":
                    object? argument = attribute.ConstructorArguments[0].Value;
                    value = argument is CustomAttributeTypedArgument typed ? typed.Value : argument;
                    return true;
            }
        }

        value = null;
        return false;

        // DecimalConstantAttribute takes the 32-bit parts of the value as uint (as C# emits it) or as int.
        static int ToBits(object? part) => part is uint bits ? unchecked((int)bits) : Convert.ToInt32(part, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Reads the marshalling information (<see cref="MarshalDescriptor"/>) and the offsets of fields from the files of the
/// assemblies, because <see cref="MetadataLoadContext"/> resolves the types that the marshalling information names.
/// </summary>
internal sealed class MarshallingMetadata : IDisposable
{
    private readonly Dictionary<Assembly, PEReader> _readers = [];

    public MarshalDescriptor? GetDescriptor(ParameterInfo parameter)
    {
        if ((parameter.Attributes & ParameterAttributes.HasFieldMarshal) == 0)
        {
            return null;
        }

        var reader = GetReader(parameter.Member.Module.Assembly);
        return MarshalDescriptor.Read(reader, reader.GetParameter(MetadataTokens.ParameterHandle(parameter.MetadataToken)).GetMarshallingDescriptor());
    }

    public MarshalDescriptor? GetDescriptor(FieldInfo field)
    {
        if ((field.Attributes & FieldAttributes.HasFieldMarshal) == 0)
        {
            return null;
        }

        var reader = GetReader(field.Module.Assembly);
        return MarshalDescriptor.Read(reader, reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(field.MetadataToken)).GetMarshallingDescriptor());
    }

    public int GetFieldOffset(FieldInfo field)
    {
        var reader = GetReader(field.Module.Assembly);
        return reader.GetFieldDefinition(MetadataTokens.FieldDefinitionHandle(field.MetadataToken)).GetOffset();
    }

    public void Dispose()
    {
        foreach (var reader in _readers.Values)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }

    private MetadataReader GetReader(Assembly assembly)
    {
        if (!_readers.TryGetValue(assembly, out var reader))
        {
            reader = new PEReader(File.OpenRead(assembly.Location));
            _readers.Add(assembly, reader);
        }

        return reader.GetMetadataReader();
    }
}
