using System.Text;
using GitBench.Features.Diff;
using GitBench.Features.Editor;
using GitBench.Localization;
using Xunit;

namespace GitBench.Tests;

/// <summary>Suggested lines drawn into the editor: rows with no line of their own, after the line
/// they are anchored to, moving with the edits above it.</summary>
public sealed class EditorGhostRowsTests
{
    private static (TextDocument Document, EditorRowSet Rows) Document(int lines)
    {
        var text = new StringBuilder();
        for (var i = 1; i <= lines; i++) text.Append("line ").Append(i).Append('\n');
        var document = TextDocument.FromText(text.ToString(0, text.Length - 1));
        return (document, new EditorRowSet(document, new LocalizationService(new ZGF.Observable.State<Locale>(Locale.En))));
    }

    [Fact]
    public void GhostLines_FollowTheirLine_AndStandForNoLine()
    {
        var (_, rows) = Document(5);

        rows.SetGhost(new GhostLines(new FileLine(2), ["int Multiply()", "\treturn 0;"]));

        Assert.Equal(7, rows.Rows.Count);
        Assert.Equal("line 2", Assert.IsType<DiffRow.Line>(rows.Rows[1]).Text.Raw);
        Assert.Equal("int Multiply()", Assert.IsType<DiffRow.Ghost>(rows.Rows[2]).Text);
        Assert.IsType<DiffRow.Ghost>(rows.Rows[3]);
        Assert.Equal("line 3", Assert.IsType<DiffRow.Line>(rows.Rows[4]).Text.Raw);
        Assert.Null(rows.NewLineAt(new RowIndex(2)));
        Assert.Null(rows.NewLineAt(new RowIndex(3)));
        Assert.Equal(new FileLine(3), rows.NewLineAt(new RowIndex(4)));
        Assert.Equal(new RowIndex(4), rows.RowForNewLine(new FileLine(3)));
    }

    [Fact]
    public void AnEditAbove_MovesTheGhostDown()
    {
        var (document, rows) = Document(5);
        rows.SetGhost(new GhostLines(new FileLine(3), ["ghost"]));

        rows.Reproject(document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(1, 0)), "new\n")));

        Assert.Equal(new FileLine(4), rows.Ghost!.After);
        Assert.Equal("line 3", Assert.IsType<DiffRow.Line>(rows.Rows[3]).Text.Raw);
        Assert.IsType<DiffRow.Ghost>(rows.Rows[4]);
    }

    [Fact]
    public void AnEditBelow_LeavesTheGhostWhereItIs()
    {
        var (document, rows) = Document(5);
        rows.SetGhost(new GhostLines(new FileLine(2), ["ghost"]));

        rows.Reproject(document.Apply(new TextEdit(TextRange.Caret(TextPosition.At(4, 0)), "new\n")));

        Assert.Equal(new FileLine(2), rows.Ghost!.After);
        Assert.IsType<DiffRow.Ghost>(rows.Rows[2]);
    }

    [Fact]
    public void ClearingTheGhost_TakesItsRowsAway()
    {
        var (_, rows) = Document(5);
        rows.SetGhost(new GhostLines(new FileLine(2), ["ghost"]));

        rows.SetGhost(null);

        Assert.Equal(5, rows.Rows.Count);
        Assert.All(rows.Rows, r => Assert.IsType<DiffRow.Line>(r));
    }
}
