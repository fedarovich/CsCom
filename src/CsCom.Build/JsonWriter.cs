using System.Globalization;
using System.Text;

namespace CsCom.Build;

/// <summary>
/// Writes the JSON of a request of the cscom tool: an object of string properties and arrays of strings or of such objects.
/// The tasks do not use System.Text.Json, which the MSBuild of Visual Studio may load in another version.
/// </summary>
internal sealed class JsonWriter
{
    private readonly StringBuilder _builder = new("{");
    private bool _first = true;

    public void Property(string name, string value)
    {
        Separate();
        WriteString(name);
        _builder.Append(':');
        WriteString(value);
    }

    /// <summary>Writes a string as an element of an array.</summary>
    public void Value(string value)
    {
        Separate();
        WriteString(value);
    }

    public void StartArray(string name)
    {
        Separate();
        WriteString(name);
        _builder.Append(":[");
        _first = true;
    }

    public void EndArray()
    {
        _builder.Append(']');
        _first = false;
    }

    public void StartObject()
    {
        Separate();
        _builder.Append('{');
        _first = true;
    }

    public void EndObject()
    {
        _builder.Append('}');
        _first = false;
    }

    /// <summary>Gets the JSON of the root object.</summary>
    public override string ToString() => _builder + "}";

    private void Separate()
    {
        if (!_first)
        {
            _builder.Append(',');
        }

        _first = false;
    }

    private void WriteString(string value)
    {
        _builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    _builder.Append("\\\"");
                    break;
                case '\\':
                    _builder.Append("\\\\");
                    break;
                case < ' ':
                    _builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    _builder.Append(c);
                    break;
            }
        }

        _builder.Append('"');
    }
}
