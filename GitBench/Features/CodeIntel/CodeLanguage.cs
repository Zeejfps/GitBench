namespace GitBench.Features.CodeIntel;

internal enum CodeLanguage
{
    CSharp,
    TypeScript,
    Tsx,
}

internal static class CodeLanguages
{
    public static IReadOnlyList<CodeLanguage> All { get; } =
        [CodeLanguage.CSharp, CodeLanguage.TypeScript, CodeLanguage.Tsx];

    private static readonly string[] CSharpLeadingDecorations = ["attribute_list"];
    private static readonly string[] TypeScriptLeadingDecorations = ["decorator"];

    /// <summary>
    /// The grammar a file's name says it is written in, or null where we have none.
    /// </summary>
    /// <remarks>
    /// TSX is a separate grammar rather than a flag on TypeScript's, because JSX syntax is
    /// ambiguous with type assertions — the same bytes parse two ways and only the extension says
    /// which. So <c>.tsx</c> may not be parsed as TypeScript, and <c>.ts</c> may not be parsed as
    /// TSX.
    /// </remarks>
    public static CodeLanguage? Detect(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var extension = Path.GetExtension(path.AsSpan());
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) return CodeLanguage.CSharp;
        if (extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase)) return CodeLanguage.Tsx;
        if (extension.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mts", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".cts", StringComparison.OrdinalIgnoreCase))
            return CodeLanguage.TypeScript;
        return null;
    }

    public static string GrammarName(this CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => "c_sharp",
        CodeLanguage.TypeScript => "typescript",
        CodeLanguage.Tsx => "tsx",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static string QueryResourceName(this CodeLanguage language) => $"{language.GrammarName()}.scm";

    public static IReadOnlyList<string> LeadingDecorationNodeTypes(this CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => CSharpLeadingDecorations,
        CodeLanguage.TypeScript or CodeLanguage.Tsx => TypeScriptLeadingDecorations,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}
