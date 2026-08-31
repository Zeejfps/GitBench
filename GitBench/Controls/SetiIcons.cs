using GitBench.Features.CodeIntel;

namespace GitBench.Controls;

/// <summary>
/// Language marks from the Seti UI icon font, for the file browser's rows.
/// </summary>
/// <remarks>
/// <para>
/// One glyph per language we can parse, so the icon set and the outline set say the same thing: a
/// row that shows a language mark is a row whose declarations the tree can open. Everything else
/// keeps <see cref="LucideIcons.File"/>.
/// </para>
/// <para>
/// Monochrome, and drawn in the same per-kind tint a plain file gets. Seti ships a colour per icon
/// and we deliberately do not use it — <c>FileBrowserRowStyles</c> spends five hues on purpose, and
/// a hue per language is the colour chart that decision exists to prevent. Shape carries the
/// language; colour still carries the kind.
/// </para>
/// <para>
/// Codepoints read out of the font's own <c>post</c> and <c>cmap</c> tables rather than copied from
/// its stylesheet, the same way <see cref="LucideIcons"/>'s were.
/// </para>
/// </remarks>
internal static class SetiIcons
{
    public const string FontFamily = "seti";

    private const string CSharp = "";
    private const string TypeScript = "";
    private const string React = "";
    private const string JavaScript = "";
    private const string Json = "";
    private const string Css = "";
    private const string Html = "";
    private const string Markdown = "";
    private const string Yaml = "";
    private const string Python = "";
    private const string Go = "";
    private const string Rust = "";
    private const string Java = "";
    private const string Shell = "";
    private const string C = "";

    /// <summary>The mark for a language, or null where the font has none for it.</summary>
    public static string? For(CodeLanguage language) => language switch
    {
        CodeLanguage.CSharp => CSharp,
        CodeLanguage.TypeScript => TypeScript,
        // Seti has no TSX mark of its own and uses React's, which is what the extension means.
        CodeLanguage.Tsx => React,
        CodeLanguage.JavaScript => JavaScript,
        CodeLanguage.Json => Json,
        CodeLanguage.Css => Css,
        CodeLanguage.Html => Html,
        CodeLanguage.Markdown => Markdown,
        CodeLanguage.Yaml => Yaml,
        CodeLanguage.Python => Python,
        CodeLanguage.Go => Go,
        CodeLanguage.Rust => Rust,
        CodeLanguage.Java => Java,
        CodeLanguage.Bash => Shell,
        CodeLanguage.C => C,
        _ => null,
    };
}
