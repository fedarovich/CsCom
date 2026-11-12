using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// Creates and updates the project files of the generated projects.
/// </summary>
internal static partial class ProjectFile
{
    public const string RuntimePackageId = "CsCom.Runtime";

    public const string DynamicPackageId = "CsCom.Runtime.Dynamic";

    /// <summary>
    /// Creates the text of a project file.
    /// </summary>
    /// <param name="manifest">The settings of the project.</param>
    /// <param name="rootNamespace">The root namespace: the namespace of the first source.</param>
    /// <param name="runtimePackageVersion">The version of the CsCom.Runtime package, or <see langword="null"/> when the version is managed centrally.</param>
    /// <param name="extraProperties">Additional MSBuild properties.</param>
    public static string Create(CsComManifest manifest, string rootNamespace, string? runtimePackageVersion, IReadOnlyDictionary<string, string>? extraProperties = null)
    {
        bool usePackage = manifest.SupportCode == SupportCodeMode.Package;
        var builder = new StringBuilder();
        builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        builder.AppendLine();
        builder.AppendLine("  <PropertyGroup>");
        if (manifest.TargetFrameworks.Count == 1)
        {
            AppendProperty(builder, "TargetFramework", manifest.TargetFrameworks[0]);
        }
        else
        {
            AppendProperty(builder, "TargetFrameworks", string.Join(';', manifest.TargetFrameworks));
        }

        if (manifest.RuntimeIdentifiers.Count > 0)
        {
            AppendProperty(builder, "RuntimeIdentifiers", string.Join(';', manifest.RuntimeIdentifiers));
        }

        AppendProperty(builder, "LangVersion", manifest.LanguageVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendProperty(builder, "Nullable", "enable");
        AppendProperty(builder, "AllowUnsafeBlocks", "true");
        AppendProperty(builder, "IsAotCompatible", "true");
        AppendProperty(builder, "GenerateDocumentationFile", "true");
        AppendProperty(builder, "AssemblyName", manifest.Name);
        AppendProperty(builder, "RootNamespace", rootNamespace);
        if (usePackage)
        {
            builder.AppendLine("    <!-- CS1591: not all members of the type library are documented. -->");
            builder.AppendLine("    <!-- SYSLIB1230, CA2256: the interfaces derive from CsCom.Runtime.IDispatch, which is defined in another assembly. -->");
            AppendProperty(builder, "NoWarn", "$(NoWarn);CS1591;SYSLIB1230;CA2256");
        }
        else
        {
            builder.AppendLine("    <!-- CS1591: not all members of the type library are documented. -->");
            AppendProperty(builder, "NoWarn", "$(NoWarn);CS1591");
        }

        foreach (var (name, value) in extraProperties ?? new Dictionary<string, string>())
        {
            AppendProperty(builder, name, value);
        }

        builder.AppendLine("  </PropertyGroup>");
        builder.AppendLine();
        builder.AppendLine("  <ItemGroup>");
        builder.AppendLine("    <!-- Source-generated COM interop marshals VARIANT and DECIMAL values only when runtime marshalling is disabled. -->");
        builder.AppendLine("    <AssemblyAttribute Include=\"System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute\" />");
        builder.AppendLine("  </ItemGroup>");

        if (usePackage)
        {
            builder.AppendLine();
            builder.AppendLine("  <ItemGroup>");
            AppendPackageReference(builder, RuntimePackageId, runtimePackageVersion);
            if (manifest.IsDynamic)
            {
                AppendPackageReference(builder, DynamicPackageId, runtimePackageVersion);
            }

            builder.AppendLine("  </ItemGroup>");
        }

        builder.AppendLine();
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    /// <summary>
    /// Updates the C# version and the versions of the CsCom.Runtime packages in an existing project file, and adds or removes
    /// the reference to CsCom.Runtime.Dynamic. Projects without a reference to CsCom.Runtime, whose support code is emitted
    /// into them, get no package references.
    /// </summary>
    /// <param name="path">The path to the project file.</param>
    /// <param name="languageVersion">
    /// The C# version to set, or <see langword="null"/> to keep the <c>LangVersion</c> of the project, which its owner can
    /// have changed: it is only set when the update asks for a version.
    /// </param>
    /// <param name="runtimePackageVersion">The version of the CsCom.Runtime packages.</param>
    /// <param name="dynamic">Whether the project references CsCom.Runtime.Dynamic.</param>
    /// <returns>Whether the file was changed.</returns>
    public static bool Update(string path, int? languageVersion, string runtimePackageVersion, bool dynamic)
    {
        var document = Load(path);
        bool changed = false;
        if (languageVersion is { } requested)
        {
            string version = requested.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var element in document.Descendants().Where(static e => e.Name.LocalName == "LangVersion"))
            {
                if (element.Value != version)
                {
                    element.Value = version;
                    changed = true;
                }
            }
        }

        var runtimeReferences = GetPackageReferences(document, RuntimePackageId);
        var dynamicReferences = GetPackageReferences(document, DynamicPackageId);
        foreach (var reference in runtimeReferences.Concat(dynamicReferences))
        {
            if (reference.Attribute("Version") is { } attribute && attribute.Value != runtimePackageVersion)
            {
                attribute.Value = runtimePackageVersion;
                changed = true;
            }
        }

        if (dynamic && dynamicReferences.Count == 0 && runtimeReferences.FirstOrDefault() is { } runtimeReference)
        {
            // A copy of the reference to CsCom.Runtime, with its version or without it for central package management.
            var dynamicReference = new XElement(runtimeReference);
            dynamicReference.SetAttributeValue("Include", DynamicPackageId);
            string indentation = runtimeReference.PreviousNode is XText { Value: var whitespace } && string.IsNullOrWhiteSpace(whitespace) ? whitespace : "\n    ";
            runtimeReference.AddAfterSelf(new XText(indentation), dynamicReference);
            changed = true;
        }
        else if (!dynamic && dynamicReferences.Count > 0)
        {
            foreach (var reference in dynamicReferences)
            {
                if (reference.PreviousNode is XText { Value: var whitespace } text && string.IsNullOrWhiteSpace(whitespace))
                {
                    text.Remove();
                }

                reference.Remove();
            }

            changed = true;
        }

        if (changed)
        {
            Save(document, path);
        }

        return changed;
    }

    /// <summary>
    /// Loads a project file with its whitespace, so that <see cref="Save"/> changes only the edited elements.
    /// </summary>
    /// <exception cref="CsComException">The file is not a valid MSBuild project.</exception>
    public static XDocument Load(string path)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            return document.Root?.Name.LocalName == "Project"
                ? document
                : throw new CsComException($"'{path}' is not an MSBuild project: its root element is not <Project>.");
        }
        catch (XmlException e)
        {
            throw new CsComException($"'{path}' is not a valid project file: {e.Message}", e);
        }
    }

    /// <summary>
    /// Saves a project file loaded with <see cref="Load"/>, keeping the encoding, the byte order mark and the line endings
    /// of the existing file, and the text of the tags and of the content that were not edited: the writer would put the
    /// attributes of every tag on one line and change their quotes.
    /// </summary>
    public static void Save(XDocument document, string path)
    {
        byte[] existing = File.ReadAllBytes(path);
        Encoding encoding = GetEncoding(existing, document.Declaration?.Encoding);
        using var reader = new StreamReader(new MemoryStream(existing), encoding, detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();

        // The parser turns the line endings into \n, which the writer turns into those of the file. The whitespace around the
        // root element is part of the document, so only the declaration is written separately.
        string newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string edited = Write(document, newLine);
        string unedited = Write(XDocument.Parse(text, LoadOptions.PreserveWhitespace), newLine);

        // The file is replaced as a whole, so that a failure does not leave a part of it.
        string temporary = path + ".cscom.tmp";
        try
        {
            File.WriteAllText(temporary, Merge(text, unedited, edited), encoding);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string Write(XDocument document, string newLine)
    {
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, NewLineChars = newLine, NewLineHandling = NewLineHandling.Replace };
        var builder = new StringBuilder();
        builder.Append(document.Declaration?.ToString());
        using (var writer = XmlWriter.Create(builder, settings))
        {
            document.Save(writer);
        }

        return builder.ToString();
    }

    // The encoding of the file: that of its byte order mark, otherwise the one that its XML declaration names, otherwise UTF-8.
    private static Encoding GetEncoding(byte[] file, string? declared)
    {
        switch (file)
        {
            case [0xEF, 0xBB, 0xBF, ..]:
                return new UTF8Encoding(true);
            case [0xFF, 0xFE, ..]:
                return Encoding.Unicode;
            case [0xFE, 0xFF, ..]:
                return Encoding.BigEndianUnicode;
        }

        if (!string.IsNullOrEmpty(declared) && !declared.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Encoding.GetEncoding(declared);
            }
            catch (ArgumentException)
            {
                // The parser has read the file, so this is not expected; UTF-8 is the encoding of XML without a declaration.
            }
        }

        return new UTF8Encoding(false);
    }

    /// <summary>
    /// Applies the changes between two texts written by the XML writer to the original text of the document, which differs
    /// from the first one only inside its tags and in the form of its characters: the tags and the content that are the same
    /// in both keep their original text.
    /// </summary>
    /// <param name="original">The text of the file.</param>
    /// <param name="unedited">The document of the file as the writer writes it.</param>
    /// <param name="edited">The edited document as the writer writes it.</param>
    internal static string Merge(string original, string unedited, string edited)
    {
        var originalTokens = Tokenize(original);
        var uneditedTokens = Tokenize(unedited);
        var editedTokens = Tokenize(edited);
        if (originalTokens.Count != uneditedTokens.Count)
        {
            return edited;
        }

        // The common start and end, then the longest common subsequence of the tokens between them.
        int start = 0;
        while (start < uneditedTokens.Count && start < editedTokens.Count && uneditedTokens[start] == editedTokens[start])
        {
            start++;
        }

        int end = 0;
        while (end < uneditedTokens.Count - start && end < editedTokens.Count - start && uneditedTokens[^(end + 1)] == editedTokens[^(end + 1)])
        {
            end++;
        }

        int rows = uneditedTokens.Count - start - end;
        int columns = editedTokens.Count - start - end;
        var lengths = new int[rows + 1, columns + 1];
        for (int i = rows - 1; i >= 0; i--)
        {
            for (int j = columns - 1; j >= 0; j--)
            {
                lengths[i, j] = uneditedTokens[start + i] == editedTokens[start + j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var builder = new StringBuilder(edited.Length);
        builder.AppendJoin(null, originalTokens.Take(start));
        for (int i = 0, j = 0; j < columns;)
        {
            if (i < rows && uneditedTokens[start + i] == editedTokens[start + j])
            {
                builder.Append(originalTokens[start + i]);
                i++;
                j++;
            }
            else if (i < rows && lengths[i + 1, j] >= lengths[i, j + 1])
            {
                i++;
            }
            else
            {
                builder.Append(editedTokens[start + j]);
                j++;
            }
        }

        builder.AppendJoin(null, originalTokens.Skip(originalTokens.Count - end));
        return builder.ToString();
    }

    // Splits the text of an XML document into its markup (tags, comments, CDATA sections, processing instructions and the
    // document type) and the text between them.
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int position = 0;
        while (position < text.Length)
        {
            int end;
            if (text[position] != '<')
            {
                end = text.IndexOf('<', position);
                end = end < 0 ? text.Length : end;
            }
            else if (text.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                end = EndOf("-->", position + 4);
            }
            else if (text.AsSpan(position).StartsWith("<![CDATA[", StringComparison.Ordinal))
            {
                end = EndOf("]]>", position + 9);
            }
            else if (text.AsSpan(position).StartsWith("<?", StringComparison.Ordinal))
            {
                end = EndOf("?>", position + 2);
            }
            else
            {
                // A tag ends at the first > outside of its attribute values; a document type can have brackets.
                char quote = '\0';
                int depth = 0;
                for (end = position + 1; end < text.Length; end++)
                {
                    char c = text[end];
                    if (quote != '\0')
                    {
                        quote = c == quote ? '\0' : quote;
                    }
                    else if (c is '"' or '\'')
                    {
                        quote = c;
                    }
                    else if (c == '[')
                    {
                        depth++;
                    }
                    else if (c == ']')
                    {
                        depth--;
                    }
                    else if (c == '>' && depth <= 0)
                    {
                        end++;
                        break;
                    }
                }
            }

            tokens.Add(text[position..end]);
            position = end;
        }

        return tokens;

        int EndOf(string terminator, int from)
        {
            int index = text.IndexOf(terminator, from, StringComparison.Ordinal);
            return index < 0 ? text.Length : index + terminator.Length;
        }
    }

    /// <summary>
    /// Finds the <c>Directory.Packages.props</c> that enables central package management for the directory, if any.
    /// </summary>
    public static string? FindCentralPackageManagement(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "Directory.Packages.props");
            if (File.Exists(path))
            {
                return CentralPackageManagementPattern().IsMatch(File.ReadAllText(path)) ? path : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates the text of a <c>nuget.config</c> file that adds a package source and optionally changes the global packages folder.
    /// </summary>
    public static string CreateNuGetConfig(string? packageSource, string? globalPackagesFolder)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.AppendLine("<configuration>");
        if (globalPackagesFolder is not null)
        {
            builder.AppendLine("  <config>");
            builder.AppendLine($"    <add key=\"globalPackagesFolder\" value=\"{SecurityElement.Escape(globalPackagesFolder)}\" />");
            builder.AppendLine("  </config>");
        }

        if (packageSource is not null)
        {
            builder.AppendLine("  <packageSources>");
            builder.AppendLine($"    <add key=\"CsCom\" value=\"{SecurityElement.Escape(packageSource)}\" />");
            builder.AppendLine("  </packageSources>");
        }

        builder.AppendLine("</configuration>");
        return builder.ToString();
    }

    private static List<XElement> GetPackageReferences(XDocument document, string id) =>
        [.. document.Descendants().Where(e => e.Name.LocalName == "PackageReference" && string.Equals((string?)e.Attribute("Include"), id, StringComparison.OrdinalIgnoreCase))];

    private static void AppendPackageReference(StringBuilder builder, string id, string? version) =>
        builder.AppendLine(version is null
            ? $"    <PackageReference Include=\"{id}\" />"
            : $"    <PackageReference Include=\"{id}\" Version=\"{SecurityElement.Escape(version)}\" />");

    private static void AppendProperty(StringBuilder builder, string name, string value) =>
        builder.Append("    <").Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).AppendLine(">");

    [GeneratedRegex(@"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CentralPackageManagementPattern();
}
