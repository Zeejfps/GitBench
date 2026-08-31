using GitBench.Features.CodeIntel;
using GitBench.Git;

namespace GitBench.Features.Diff;

/// <summary>
/// What a diff's file text is known to mean: its syntax colors, plus the parsed declaration
/// outline of each side. Everything here is addressed by file line number, so a re-indexing of
/// the hunk list — an optimistic stage drops one out of the middle — leaves it valid.
/// </summary>
internal sealed record DiffAnnotations(DiffHighlight? Highlight, FileOutline? NewSide, FileOutline? OldSide)
{
    /// <summary>
    /// The declaration a hunk sits in, as a dotted containment path (<c>AuthService.Login(string)</c>).
    /// Null when the hunk carries no lines, when the side it needs has no outline, or when its
    /// first changed line is inside no declaration — the caller then shows git's own header.
    /// </summary>
    public string? HunkHeader(DiffHunk hunk) => FileOutline.RenderPath(EnclosingPath(hunk));

    // The first changed line picks both the line number and the side: an addition is only in the
    // new file and a removal only in the old, which is the same split NeededSides fetches by. A
    // hunk that only deletes therefore names the declaration the lines were removed from rather
    // than whatever now sits at that position.
    private IReadOnlyList<OutlineNode> EnclosingPath(DiffHunk hunk)
    {
        foreach (var line in hunk.Lines)
        {
            switch (line)
            {
                case { Kind: DiffLineKind.Added, NewLineNumber: int n }:
                    return NewSide?.EnclosingPathAt(n) ?? [];
                case { Kind: DiffLineKind.Removed, OldLineNumber: int o }:
                    return OldSide?.EnclosingPathAt(o) ?? [];
            }
        }

        return [];
    }

}
