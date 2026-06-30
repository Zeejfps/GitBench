namespace GitBench.Features.Diff;

internal static class DiffOptions
{
    public const int ContextLines = 3;
    public const int TruncationLineCap = 5000;
    public const int TabWidth = 4;
    public const string MonoFontFamily = "jetbrains-mono";

    // Per-token syntax highlighting in the diff body. On by default; flip to false to fall back
    // to flat single-color rendering. A mutable field (not const) so a future setting/menu can
    // toggle it at runtime.
    public static bool SyntaxHighlightingEnabled = true;

    // Intra-line (changed-character) emphasis in replace blocks. On by default. Baked into rows
    // at flatten time like SyntaxHighlightingEnabled, so a runtime flip takes effect on the next
    // FlattenRows (next diff load / re-emit), not instantly.
    public static bool IntraLineHighlightingEnabled = true;
}
