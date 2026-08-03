using System.Text;
using GitBench.Git;

namespace GitBench.Features.Diff.Reading;

/// <summary>Where one numbered row sits in the immutable source diffs.</summary>
internal readonly record struct ReadingRowRef(int FileIndex, int HunkIndex, int LineIndex);

/// <summary>
/// A stable 1-based numbering of every changed and context line across an ordered set of
/// diffs, plus the numbered rendering the model reads.
/// </summary>
/// <remarks>
/// The numbering is the coordinate space a <see cref="ReadingPlan"/> is written in. It is
/// derived from the parsed <see cref="DiffResult"/>s alone and never shifts, so a plan stays
/// valid for as long as the diffs it was built against are unchanged — which is what makes a
/// compiled plan cacheable and safe to apply as presentation state.
/// </remarks>
internal sealed class ReadingRowIndex
{
    private readonly ReadingRowRef[] _rows;
    private readonly IReadOnlyList<DiffResult> _files;
    private readonly int[][] _hunkStart;

    private ReadingRowIndex(IReadOnlyList<DiffResult> files, ReadingRowRef[] rows, int[][] hunkStart)
    {
        _files = files;
        _rows = rows;
        _hunkStart = hunkStart;
    }

    public IReadOnlyList<DiffResult> Files => _files;

    /// <summary>Highest valid row ordinal; rows run 1..<see cref="Count"/>.</summary>
    public int Count => _rows.Length;

    public static ReadingRowIndex Build(IReadOnlyList<DiffResult> files)
    {
        var rows = new List<ReadingRowRef>();
        var hunkStart = new int[files.Count][];
        for (var f = 0; f < files.Count; f++)
        {
            var file = files[f];
            hunkStart[f] = new int[file.Hunks.Count];
            for (var h = 0; h < file.Hunks.Count; h++)
            {
                hunkStart[f][h] = rows.Count + 1;
                var lines = file.Hunks[h].Lines;
                for (var l = 0; l < lines.Count; l++)
                    rows.Add(new ReadingRowRef(f, h, l));
            }
        }
        return new ReadingRowIndex(files, rows.ToArray(), hunkStart);
    }

    public bool IsValidRow(int ordinal) => ordinal >= 1 && ordinal <= _rows.Length;

    public ReadingRowRef Locate(int ordinal) => _rows[ordinal - 1];

    /// <summary>The ordinal of a line addressed structurally, or 0 when it is out of range.</summary>
    public int OrdinalOf(int fileIndex, int hunkIndex, int lineIndex)
    {
        if (fileIndex < 0 || fileIndex >= _hunkStart.Length) return 0;
        var starts = _hunkStart[fileIndex];
        if (hunkIndex < 0 || hunkIndex >= starts.Length) return 0;
        if (lineIndex < 0 || lineIndex >= _files[fileIndex].Hunks[hunkIndex].Lines.Count) return 0;
        return starts[hunkIndex] + lineIndex;
    }

    public DiffLine Line(int ordinal)
    {
        var at = _rows[ordinal - 1];
        return _files[at.FileIndex].Hunks[at.HunkIndex].Lines[at.LineIndex];
    }

    /// <summary>Whether two ordinals sit in the same hunk of the same file.</summary>
    public bool SameHunk(int a, int b)
    {
        var x = _rows[a - 1];
        var y = _rows[b - 1];
        return x.FileIndex == y.FileIndex && x.HunkIndex == y.HunkIndex;
    }

    public static char Marker(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Added => '+',
        DiffLineKind.Removed => '-',
        _ => ' ',
    };

    /// <summary>
    /// The numbered diff handed to the model: <c>N|</c> then the line's diff marker and source
    /// text, with unnumbered file and hunk headings for orientation.
    /// </summary>
    /// <remarks>The gutter is display-only. A plan addresses rows by <c>N</c> and never quotes
    /// the gutter or the marker as source text.</remarks>
    public string Render()
    {
        var b = new StringBuilder();
        var ordinal = 0;
        for (var f = 0; f < _files.Count; f++)
        {
            var file = _files[f];
            b.Append("=== ").Append(file.Path);
            if (file.OldPath != null)
                b.Append(" (renamed from ").Append(file.OldPath).Append(')');
            b.Append('\n');

            foreach (var hunk in file.Hunks)
            {
                b.Append("@@ -").Append(hunk.OldStart).Append(',').Append(hunk.OldLines)
                    .Append(" +").Append(hunk.NewStart).Append(',').Append(hunk.NewLines).Append(" @@");
                if (!string.IsNullOrEmpty(hunk.Header))
                    b.Append(' ').Append(hunk.Header);
                b.Append('\n');

                foreach (var line in hunk.Lines)
                {
                    ordinal++;
                    b.Append(ordinal).Append('|').Append(Marker(line.Kind)).Append(line.Text).Append('\n');
                }
            }
        }
        return b.ToString();
    }
}
