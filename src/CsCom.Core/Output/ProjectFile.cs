using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CsCom.CodeGeneration;

namespace CsCom.Output;

/// <summary>
/// Creates and updates the project files of the generated projects.
/// </summary>
internal static partial class ProjectFile
{
    public const string RuntimePackageId = "CsCom.Runtime";

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
            builder.AppendLine(runtimePackageVersion is null
                ? $"    <PackageReference Include=\"{RuntimePackageId}\" />"
                : $"    <PackageReference Include=\"{RuntimePackageId}\" Version=\"{SecurityElement.Escape(runtimePackageVersion)}\" />");
            builder.AppendLine("  </ItemGroup>");
        }

        builder.AppendLine();
        builder.AppendLine("</Project>");
        return builder.ToString();
    }

    /// <summary>
    /// Updates the C# version and the version of the CsCom.Runtime package in an existing project file.
    /// </summary>
    /// <returns>Whether the file was changed.</returns>
    public static bool Update(string path, int languageVersion, string runtimePackageVersion)
    {
        var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
        bool changed = false;
        string version = languageVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var element in document.Descendants().Where(static e => e.Name.LocalName == "LangVersion"))
        {
            if (element.Value != version)
            {
                element.Value = version;
                changed = true;
            }
        }

        foreach (var reference in document.Descendants().Where(static e => e.Name.LocalName == "PackageReference" && (string?)e.Attribute("Include") == RuntimePackageId))
        {
            if (reference.Attribute("Version") is { } attribute && attribute.Value != runtimePackageVersion)
            {
                attribute.Value = runtimePackageVersion;
                changed = true;
            }
        }

        if (changed)
        {
            var settings = new System.Xml.XmlWriterSettings { OmitXmlDeclaration = document.Declaration is null, Encoding = new UTF8Encoding(false) };
            using var writer = System.Xml.XmlWriter.Create(path, settings);
            document.Save(writer);
        }

        return changed;
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

    private static void AppendProperty(StringBuilder builder, string name, string value) =>
        builder.Append("    <").Append(name).Append('>').Append(SecurityElement.Escape(value)).Append("</").Append(name).AppendLine(">");

    [GeneratedRegex(@"<ManagePackageVersionsCentrally>\s*true\s*</ManagePackageVersionsCentrally>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CentralPackageManagementPattern();
}
