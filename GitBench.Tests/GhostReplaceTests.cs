using System.Text;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Localization;
using Xunit;

namespace GitBench.Tests;

/// <summary>A suggestion replacing a run of lines reads as a diff: lines it keeps stay the file's
/// own, and only what it removes is lit up and only what it adds is drawn, where it goes.</summary>
public sealed class GhostReplaceTests
{
    private static GhostReplacement Of(int from, string[] old, params string[] draft) =>
        GhostReplace.Of(old, new FileLine(from), new FileLine(from + old.Length - 1), new EditorGhost(new GhostPlace.Replace(new FileLine(from), new FileLine(from + old.Length - 1)), draft));

    [Fact]
    public void KeptLines_AreNeitherLitNorDrawn()
    {
        string[] old =
        [
            "export type EntryReviewAction = 'approve' | 'remove'",
            "export type EventEntryStatus = 'approved' | 'removed'",
            "export type EventEntrySource = 'creator' | 'manual'",
            "export interface AdminEventEntry {",
            "  contentId: string",
            "  name: string",
            "  status: EventEntryStatus",
            "  reviewedAt?: EpochMs",
            "}",
        ];

        var replacement = Of(103, old,
            "export type EventEntrySource = 'creator' | 'manual'",
            "export interface AdminEventEntry {",
            "  contentId: string",
            "  name: string",
            "  disqualification?: EntryDisqualification",
            "}");

        Assert.Equal([true, true, false, false, false, false, true, true, false], replacement.Replaced.Select(r => r is not null));
        Assert.Equal(new FileLine(103), replacement.FirstRemoved);
        Assert.Equal(["  disqualification?: EntryDisqualification"], replacement.Ghost.Lines);
        Assert.Equal(new FileLine(110), replacement.Ghost.AnchorOf(0));
    }

    [Fact]
    public void AChangedLine_PairsWithTheLineItReplaces()
    {
        var replacement = Of(10, ["a", "int x = 1;", "b"], "a", "int x = 2;", "b");

        Assert.Equal([false, true, false], replacement.Replaced.Select(r => r is not null));
        Assert.NotNull(replacement.Replaced[1]!.Emphasis);
        Assert.NotNull(replacement.Ghost.Emphasis![0]);
        Assert.Equal(new FileLine(11), replacement.Ghost.AnchorOf(0));
    }

    [Fact]
    public void LinesAddedBetweenKeptOnes_HangUnderTheLineAboveThem()
    {
        var replacement = Of(1, ["a", "b", "c"], "a", "x", "b", "y", "c");

        Assert.All(replacement.Replaced, Assert.Null);
        Assert.Null(replacement.FirstRemoved);
        Assert.Equal(["x", "y"], replacement.Ghost.Lines);
        Assert.Equal([new FileLine(1), new FileLine(2)], replacement.Ghost.Anchors!);
    }

    [Fact]
    public void ALineTheDraftCouldKeepTwice_KeepsTheEarlierOne()
    {
        var replacement = Of(1, ["a", "}", "f", "}", "g"], "a", "x", "}", "y");

        Assert.Equal([false, false, true, true, true], replacement.Replaced.Select(r => r is not null));
        Assert.Equal(["x", "y"], replacement.Ghost.Lines);
        Assert.Equal([new FileLine(1), new FileLine(5)], replacement.Ghost.Anchors!);
    }

    [Fact]
    public void LinesAddedAboveTheFirstLine_ReplaceIt()
    {
        var replacement = Of(1, ["a", "b"], "x", "a", "b");

        Assert.Equal([true, false], replacement.Replaced.Select(r => r is not null));
        Assert.Equal(["x", "a"], replacement.Ghost.Lines);
        Assert.Equal([new FileLine(1), new FileLine(1)], replacement.Ghost.Anchors!);
    }

    [Fact]
    public void RowSet_DrawsEachAddedLineUnderItsAnchor()
    {
        var text = new StringBuilder();
        for (var i = 1; i <= 4; i++) text.Append("line ").Append(i).Append('\n');
        var document = TextDocument.FromText(text.ToString(0, text.Length - 1));
        var rows = new EditorRowSet(document, new LocalizationService(new ZGF.Observable.State<Locale>(Locale.En)));

        rows.SetGhost(new GhostLines(new FileLine(3), ["x", "y", "z"], Anchors: [new FileLine(1), new FileLine(3), new FileLine(3)]));

        Assert.Equal(["line 1", "+x", "line 2", "line 3", "+y", "+z", "line 4"], rows.Rows.Select(Describe));
        Assert.Equal(new RowIndex(2), rows.RowForNewLine(new FileLine(2)));

        rows.Reproject(document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(2, 0)), "new\n")));

        Assert.Equal(["line 1", "+x", "new", "line 2", "line 3", "+y", "+z", "line 4"], rows.Rows.Select(Describe));
        Assert.Equal(new FileLine(4), rows.Ghost!.After);
    }

    private static string Describe(DiffRow row) => row switch
    {
        DiffRow.Ghost ghost => "+" + ghost.Text,
        DiffRow.Line line => line.Text.Raw,
        _ => row.GetType().Name,
    };
}
