using GitBench.Features.CodeIntel;

namespace GitBench.Features.Diff;

/// <summary>
/// What a file is written in, decided once per file: a language we bundle a tree-sitter grammar
/// for (colors and outline both come from the parser), one only TextMate knows, or nothing.
/// </summary>
internal abstract record FileLanguage
{
    public sealed record TreeSitter(CodeLanguage Language) : FileLanguage;

    public sealed record TextMate(string LanguageId) : FileLanguage;

    public sealed record None : FileLanguage
    {
        public static readonly None Instance = new();
    }

    /// <summary>The parser wins over TextMate wherever both know the extension, except for the
    /// file names TextMate singles out (tsconfig.json is jsonc, whose comments the JSON grammar
    /// would parse as errors).</summary>
    public static FileLanguage Detect(string path)
    {
        if (LanguageRegistry.DetectByFileName(path) is { } named) return new TextMate(named);
        if (CodeLanguages.Detect(path) is { } language) return new TreeSitter(language);
        if (LanguageRegistry.DetectByExtension(path) is { } id) return new TextMate(id);
        return None.Instance;
    }

    /// <summary>A language named rather than inferred from a path — a fenced code block's info
    /// string — where the parser's own aliases apply before TextMate's ids.</summary>
    public static FileLanguage Named(string name) =>
        CodeLanguages.FromInjectionName(name) is { } language ? new TreeSitter(language) : new TextMate(name);
}
