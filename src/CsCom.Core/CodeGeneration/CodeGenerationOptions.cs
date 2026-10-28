using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsCom.CodeGeneration;

/// <summary>
/// Where the support code used by the generated code (<c>IDispatch</c>, activation, late binding) comes from.
/// </summary>
public enum SupportCodeMode
{
    /// <summary>The generated project references the <c>CsCom.Runtime</c> package.</summary>
    Package,

    /// <summary>The support code is emitted into the generated project as public types.</summary>
    Public,

    /// <summary>
    /// The support code is emitted into the generated project as internal types.
    /// All generated types are internal as well, because public interfaces cannot derive from an internal <c>IDispatch</c>.
    /// </summary>
    Internal,
}

/// <summary>
/// The options of the C# code generation.
/// </summary>
public sealed class CodeGenerationOptions
{
    /// <summary>The minimum supported C# version.</summary>
    public const int MinimumLanguageVersion = 14;

    /// <summary>The namespace of the generated types.</summary>
    public required string Namespace { get; init; }

    /// <summary>The C# language version to generate the code for.</summary>
    public int LanguageVersion { get; init; } = MinimumLanguageVersion;

    /// <summary>Whether all types are generated into a single file.</summary>
    public bool SingleFile { get; init; }

    /// <summary>The name of the file generated when <see cref="SingleFile"/> is set.</summary>
    public string SingleFileName { get; init; } = "Interop.cs";

    /// <summary>Where the support code comes from.</summary>
    public SupportCodeMode SupportCode { get; init; } = SupportCodeMode.Package;

    /// <summary>The version of the tool written into the file headers.</summary>
    public string ToolVersion { get; init; } = ToolInfo.Version;

    /// <summary>The namespace of the support code.</summary>
    public string SupportNamespace => SupportCode == SupportCodeMode.Package ? "CsCom.Runtime" : $"{Namespace}.ComSupport";

    /// <summary>Whether extension indexers (C# 15) can be generated.</summary>
    public bool SupportsExtensionIndexers => LanguageVersion >= 15;
}

/// <summary>
/// A generated C# source file.
/// </summary>
/// <param name="RelativePath">The path of the file relative to the folder that receives the generated code.</param>
/// <param name="Root">The syntax tree of the file.</param>
public sealed record GeneratedDocument(string RelativePath, CompilationUnitSyntax Root)
{
    /// <summary>Gets the text of the file.</summary>
    public string GetText() => Root.ToFullString();
}
