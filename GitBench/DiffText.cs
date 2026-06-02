namespace GitGui;

/// <summary>
/// Shared text shaping for the diff body. Tab expansion lives here (rather than privately in
/// the renderer) because syntax highlighting must expand tabs the exact same way before
/// tokenizing — token columns are produced in tab-expanded space so they align 1:1 with the
/// rendered line text. Any divergence here would slide highlight colors off their glyphs.
/// </summary>
internal static class DiffText
{
    private static readonly string TabReplacement = new(' ', DiffOptions.TabWidth);

    public static string ExpandTabs(string s)
    {
        if (s.IndexOf('\t') < 0) return s;
        return s.Replace("\t", TabReplacement);
    }
}
