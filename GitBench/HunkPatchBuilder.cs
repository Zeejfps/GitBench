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
        return true;
    }

    public static string Build(DiffResult diff, int hunkIndex)
    {
        var hunk = diff.Hunks[hunkIndex];
        var path = diff.Path;
        var sb = new StringBuilder();

        sb.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');

        // DiffResult.OldMode/NewMode are only populated when modes differ; absence does NOT
        // mean the file is new/deleted. The reliable signal is the hunk range — a new file
        // has a single hunk with -0,0; a deleted file has +0,0.
        var isNewFile = hunk.OldLines == 0 && hunk.OldStart == 0;
        var isDeletedFile = hunk.NewLines == 0 && hunk.NewStart == 0;
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

        var oldFile = isNewFile ? "/dev/null" : "a/" + path;
        var newFile = isDeletedFile ? "/dev/null" : "b/" + path;
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
        }

        return sb.ToString();
    }

    private static string FormatMode(int mode)
        => Convert.ToString(mode, 8).PadLeft(6, '0');
}
