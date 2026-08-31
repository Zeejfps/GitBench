namespace GitBench.Features.CodeIntel;

internal enum CodeLanguage
{
    CSharp,
    TypeScript,
    Tsx,
    JavaScript,
    Json,
    Css,
    Html,
    Markdown,
    Yaml,
    Python,
    Go,
    Rust,
    Java,
    Bash,
    C,
}

/// <summary>
/// The languages we bundle a grammar and a query for, and how a file's name maps onto one.
/// </summary>
/// <remarks>
/// <para>
/// One table rather than three parallel switches: a language is a grammar name, a set of
/// extensions and the node types that decorate a declaration without starting it, and keeping
/// those together is what stops a new language being added to two of the three.
/// </para>
/// <para>
/// Deliberately not <c>LanguageRegistry</c>'s id, which names a TextMate grammar and has some
/// sixty members. One type per vocabulary.
/// </para>
/// </remarks>
internal static class CodeLanguages
{
    /// <summary>
    /// Attributes, decorators and annotations: children of the declaration node that precede its
    /// signature. A declaration's start line skips them, so a fold chevron sits beside the thing it
    /// folds rather than several rows above it.
    /// </summary>
    private static readonly string[] None = [];
    private static readonly string[] Attributes = ["attribute_list"];
    private static readonly string[] Decorators = ["decorator"];
    private static readonly string[] Annotations = ["modifiers", "annotation", "marker_annotation"];
    private static readonly string[] AttributeItems = ["attribute_item"];

    private static readonly Entry[] Table =
    [
        new(CodeLanguage.CSharp, "c_sharp", [".cs"], Attributes),
        // TSX is its own grammar, not a flag on TypeScript's: JSX syntax is ambiguous with type
        // assertions, so the same bytes parse two ways and only the extension says which.
        new(CodeLanguage.TypeScript, "typescript", [".ts", ".mts", ".cts"], Decorators),
        new(CodeLanguage.Tsx, "tsx", [".tsx"], Decorators),
        // JSX needs no grammar of its own — tree-sitter-javascript parses it inline.
        new(CodeLanguage.JavaScript, "javascript", [".js", ".mjs", ".cjs", ".jsx"], Decorators),
        new(CodeLanguage.Json, "json", [".json"], None),
        new(CodeLanguage.Css, "css", [".css"], None),
        new(CodeLanguage.Html, "html", [".html", ".htm"], None),
        new(CodeLanguage.Markdown, "markdown", [".md", ".markdown"], None),
        new(CodeLanguage.Yaml, "yaml", [".yaml", ".yml"], None),
        new(CodeLanguage.Python, "python", [".py", ".pyi"], Decorators),
        new(CodeLanguage.Go, "go", [".go"], None),
        new(CodeLanguage.Rust, "rust", [".rs"], AttributeItems),
        new(CodeLanguage.Java, "java", [".java"], Annotations),
        new(CodeLanguage.Bash, "bash", [".sh", ".bash"], None),
        // Only C ships, so a .h is unambiguous here in a way it is not in general.
        new(CodeLanguage.C, "c", [".c", ".h"], None),
    ];

    public static IReadOnlyList<CodeLanguage> All { get; } = [.. Table.Select(e => e.Language)];

    /// <summary>The grammar a file's name says it is written in, or null where we bundle none.</summary>
    public static CodeLanguage? Detect(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var extension = Path.GetExtension(path.AsSpan());
        if (extension.IsEmpty) return null;

        foreach (var entry in Table)
        {
            foreach (var candidate in entry.Extensions)
            {
                if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return entry.Language;
            }
        }

        return null;
    }

    /// <summary>The <c>tree_sitter_&lt;name&gt;</c> the bundled library exports for it.</summary>
    public static string GrammarName(this CodeLanguage language) => Of(language).GrammarName;

    public static string QueryResourceName(this CodeLanguage language) => $"{language.GrammarName()}.scm";

    /// <summary>
    /// The embedded <c>highlights.scm</c> for a language, which only the languages we highlight
    /// with tree-sitter have.
    /// </summary>
    /// <remarks>
    /// Unlike the outline query, this one is allowed to be missing: Markdown and HTML ship no
    /// highlights query because theirs need injections we do not run, and that absence is what
    /// routes them to TextMate.
    /// </remarks>
    public static string HighlightQueryResourceName(this CodeLanguage language) =>
        $"highlights.{language.GrammarName()}.scm";

    public static IReadOnlyList<string> LeadingDecorationNodeTypes(this CodeLanguage language) =>
        Of(language).LeadingDecorations;

    private static Entry Of(CodeLanguage language)
    {
        foreach (var entry in Table)
        {
            if (entry.Language == language) return entry;
        }

        throw new ArgumentOutOfRangeException(nameof(language), language, null);
    }

    private sealed record Entry(
        CodeLanguage Language,
        string GrammarName,
        string[] Extensions,
        string[] LeadingDecorations);
}
