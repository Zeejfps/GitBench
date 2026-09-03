namespace GitBench.Features.Diff;

internal static class DiffOptions
{
    public const int ContextLines = 3;
    public const int TruncationLineCap = 5000;
    // Lines revealed per click of a hunk-gap expander arrow.
    public const int ContextExpandStep = 20;
    public const int TabWidth = 4;

    // Per-token syntax highlighting in the diff body. On by default; flip to false to fall back
    // to flat single-color rendering. A mutable field (not const) so a future setting/menu can
    // toggle it at runtime.
    public static bool SyntaxHighlightingEnabled = true;

    // Intra-line (changed-character) emphasis in replace blocks. On by default. Baked into rows
    // at flatten time like SyntaxHighlightingEnabled, so a runtime flip takes effect on the next
    // FlattenRows (next diff load / re-emit), not instantly.
    public static bool IntraLineHighlightingEnabled = true;

    // Tree-sitter parsing of the diff's file text, which backs the declaration a hunk separator
    // names. Off means every hunk falls back to git's own xfuncname header — exactly what shipped
    // before the parser existed.
    public static bool StructureEnabled = true;

    // The "N usages" row above each declaration in the whole-file viewer. The rows appear only for
    // files a language server is actually answering about, so with no server configured this costs
    // a parse the outline was doing anyway and shows nothing.
    public static bool UsageLensEnabled = true;
}
