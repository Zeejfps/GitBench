namespace GitGui;

/// <summary>
/// Flat row stream the virtualized content view walks. Banners (rename/mode/truncated),
/// hunk separators, and individual diff lines all share a uniform row height so visible-range
/// math is trivial (floor/ceil on scrollY÷rowHeight).
/// </summary>
internal abstract record DiffRow
{
    public sealed record Banner(string Text) : DiffRow;
    public sealed record HunkSeparator(string Range, string? Header) : DiffRow;
    /// <summary>
    /// Pre-formatted strings (line numbers stringified, tabs expanded) so per-frame draw
    /// work doesn't allocate.
    /// </summary>
    public sealed record Line(
        DiffLineKind Kind,
        string OldNumber,
        string NewNumber,
        string Text,
        int Chars) : DiffRow;
}