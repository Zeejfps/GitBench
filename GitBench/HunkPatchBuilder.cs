using System.Text;

namespace GitGui;

internal static class HunkPatchBuilder
{
    public static bool CanPatchHunk(DiffResult diff)
    {
        if (diff.IsBinary) return false;
        if (diff.IsModeOnly) return false;
        if (diff.OldPath != null) return false;
        if (diff.Hunks.Count == 0) return false;
        // A truncated diff drops body lines past the line cap but keeps the original @@ counts,
        // so a rebuilt patch's body wouldn't match its header and git apply would reject or
        // misapply it. Fall back to whole-file staging for these.
        if (diff.Truncated) return false;
        return true;
    }

    public static string Build(DiffResult diff, int hunkIndex)
    {
        var hunk = diff.Hunks[hunkIndex];
        var path = diff.Path;
        var sb = new StringBuilder();

        sb.Append("diff --git ").Append(HeaderPath("a/", path)).Append(' ')
            .Append(HeaderPath("b/", path)).Append('\n');

        // DiffResult.OldMode/NewMode are only populated when modes differ; absence does NOT
        // mean the file is new/deleted. The reliable signal is the hunk range — a new file has
        // a single hunk with -0,0; a deleted file has +0,0. A new/deleted file always has
        // exactly one hunk, so require that too: it stops a coincidental -0,0 / +0,0 range on
        // one hunk of a multi-hunk diff from injecting a spurious new/deleted-file header.
        var singleHunk = diff.Hunks.Count == 1;
        var isNewFile = singleHunk && hunk.OldLines == 0 && hunk.OldStart == 0;
        var isDeletedFile = singleHunk && hunk.NewLines == 0 && hunk.NewStart == 0;
        const int DefaultFileMode = 33188; // 0o100644

        if (isNewFile)
            sb.Append("new file mode ").Append(FormatMode(diff.NewMode ?? DefaultFileMode)).Append('\n');
        else if (isDeletedFile)
            sb.Append("deleted file mode ").Append(FormatMode(diff.OldMode ?? DefaultFileMode)).Append('\n');
        else if (diff.OldMode is int om && diff.NewMode is int nm && om != nm)
        {
            sb.Append("old mode ").Append(FormatMode(om)).Append('\n');
            sb.Append("new mode ").Append(FormatMode(nm)).Append('\n');
        }

        var oldFile = isNewFile ? "/dev/null" : FilePath("a/", path);
        var newFile = isDeletedFile ? "/dev/null" : FilePath("b/", path);
        sb.Append("--- ").Append(oldFile).Append('\n');
        sb.Append("+++ ").Append(newFile).Append('\n');

        sb.Append("@@ -").Append(hunk.OldStart).Append(',').Append(hunk.OldLines)
            .Append(" +").Append(hunk.NewStart).Append(',').Append(hunk.NewLines).Append(" @@");
        if (!string.IsNullOrEmpty(hunk.Header))
            sb.Append(' ').Append(hunk.Header);
        sb.Append('\n');

        foreach (var line in hunk.Lines)
        {
            var prefix = line.Kind switch
            {
                DiffLineKind.Added => '+',
                DiffLineKind.Removed => '-',
                _ => ' ',
            };
            sb.Append(prefix).Append(line.Text).Append('\n');
            if (line.NoNewlineAtEof)
                sb.Append("\\ No newline at end of file\n");
        }

        return sb.ToString();
    }

    // The name token for the `diff --git` line: C-quoted when git would quote it, otherwise the
    // bare `a/path`. That line carries both names, so a spaced path is left unquoted here and
    // disambiguated by the trailing tab git apply reads from the ---/+++ lines (see FilePath).
    private static string HeaderPath(string prefix, string path)
        => NeedsCQuote(path) ? CQuote(prefix + path) : prefix + path;

    // The name token for a ---/+++ line: C-quoted when needed, else bare — but an unquoted path
    // containing a space gets a trailing tab so git apply can find where the name ends. This
    // mirrors git's own patch output exactly.
    private static string FilePath(string prefix, string path)
    {
        if (NeedsCQuote(path)) return CQuote(prefix + path);
        return path.Contains(' ') ? prefix + path + '\t' : prefix + path;
    }

    // Whether git would C-style quote this path in patch headers, matching the default
    // core.quotePath=true: any control byte, DEL, the quote/backslash specials, or a non-ASCII
    // (>= 0x80) UTF-8 byte forces quoting. A plain space does NOT (git handles that with a tab).
    private static bool NeedsCQuote(string path)
    {
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            if (b < 0x20 || b == 0x7f || b >= 0x80) return true;
            if (b == (byte)'"' || b == (byte)'\\') return true;
        }
        return false;
    }

    // git's quote_c_style: wrap in double quotes, escape the named control chars and "/\, and
    // octal-escape every other control/non-ASCII byte (3-digit \nnn over the UTF-8 encoding).
    private static string CQuote(string text)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            switch (b)
            {
                case (byte)'"': sb.Append("\\\""); break;
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'\a': sb.Append("\\a"); break;
                case (byte)'\b': sb.Append("\\b"); break;
                case (byte)'\t': sb.Append("\\t"); break;
                case (byte)'\n': sb.Append("\\n"); break;
                case (byte)'\v': sb.Append("\\v"); break;
                case (byte)'\f': sb.Append("\\f"); break;
                case (byte)'\r': sb.Append("\\r"); break;
                default:
                    if (b < 0x20 || b == 0x7f || b >= 0x80)
                        sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    else
                        sb.Append((char)b);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string FormatMode(int mode)
        => Convert.ToString(mode, 8).PadLeft(6, '0');
}
