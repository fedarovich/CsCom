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

    private readonly string? _supportNamespace;

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

    /// <summary>How <c>DATE</c>, <c>CURRENCY</c> and <c>VARIANT_BOOL</c> values are represented.</summary>
    public MarshallingOptions Marshalling { get; init; } = new();

    /// <summary>The code generated for the event (source) interfaces.</summary>
    public EventStyle EventStyle { get; init; }

    /// <summary>Whether the members of the generated structures are public fields or properties.</summary>
    public StructProperties StructProperties { get; init; }

    /// <summary>
    /// Whether overloads are generated for the methods whose optional parameters have default values that C# cannot express,
    /// e.g. <c>ComBool.True</c> or a <c>DateTime</c>, so that these parameters can be omitted.
    /// </summary>
    public bool OverloadsForDefaultValues { get; init; }

    /// <summary>The version of the tool written into the file headers.</summary>
    public string ToolVersion { get; init; } = ToolInfo.Version;

    /// <summary>
    /// The namespace of the support code: <c>CsCom.Runtime</c> with <see cref="SupportCodeMode.Package"/>, otherwise the
    /// specified namespace, or <c>&lt;Namespace&gt;.ComSupport</c> by default.
    /// </summary>
    public string SupportNamespace
    {
        get => SupportCode == SupportCodeMode.Package ? "CsCom.Runtime" : _supportNamespace ?? $"{Namespace}.ComSupport";
        init => _supportNamespace = value;
    }

    /// <summary>
    /// The warnings disabled in the generated files with <c>#pragma warning disable</c>, e.g. <c>CS1591</c> when the files
    /// are compiled into a project that generates documentation.
    /// </summary>
    public IReadOnlyList<string> SuppressedWarnings { get; init; } = [];

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
