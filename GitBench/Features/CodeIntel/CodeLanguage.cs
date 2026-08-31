namespace GitBench.Features.CodeIntel;

internal enum CodeLanguage
{
    CSharp,
}

internal static class CodeLanguages
{
    public static IReadOnlyList<CodeLanguage> All { get; } = [CodeLanguage.CSharp];

    private static readonly string[] CSharpLeadingDecorations = ["attribute_list"];

    public static CodeLanguage? Detect(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var extension = Path.GetExtension(path.AsSpan());
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ? CodeLanguage.CSharp : null;
    }

    public static string GrammarName(this CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => "c_sharp",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };

    public static string QueryResourceName(this CodeLanguage language) => $"{language.GrammarName()}.scm";

    public static IReadOnlyList<string> LeadingDecorationNodeTypes(this CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => CSharpLeadingDecorations,
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
    };
}
