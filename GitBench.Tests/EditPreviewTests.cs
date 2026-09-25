using GitBench.Features.AgentConnections.Acp;
using GitBench.Features.Assistant;
using GitBench.Features.Pairing;
using Xunit;

namespace GitBench.Tests;

/// <summary>What an edit's approval card shows of the change, and the "this file" answer.</summary>
public sealed class EditPreviewTests
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "edit-preview-repo"));

    [Fact]
    public void Replacement_ShowsTheChangedLines_WithOneLineAround()
    {
        var lines = EditPreview.Of([new AcpFileEdit("a.md", "a\nb\nc\nd\ne", "a\nb\nX\nd\ne")], Repo);
        Assert.Equal<EditPreviewLine>(
            [
                new EditPreviewLine.Context("b"),
                new EditPreviewLine.Removed("c"),
                new EditPreviewLine.Added("X"),
                new EditPreviewLine.Context("d"),
            ],
            lines);
    }

    [Fact]
    public void NewFile_IsAllAdded()
    {
        var lines = EditPreview.Of([new AcpFileEdit("a.md", null, "one\r\ntwo\r\n")], Repo);
        Assert.Equal<EditPreviewLine>([new EditPreviewLine.Added("one"), new EditPreviewLine.Added("two")], lines);
    }

    [Fact]
    public void LongChange_IsCutOff_WithTheRestCounted()
    {
        var text = string.Join('\n', Enumerable.Range(0, EditPreview.MaxLines + 5));
        var lines = EditPreview.Of([new AcpFileEdit("a.md", null, text)], Repo);
        Assert.Equal(EditPreview.MaxLines + 1, lines.Count);
        Assert.Equal(new EditPreviewLine.Elided(5), lines[^1]);
    }

    [Fact]
    public void EditsAcrossFiles_AreHeadedByTheirFile()
    {
        var lines = EditPreview.Of([new AcpFileEdit(Path.Combine(Repo, "a.md"), "x", "y"), new AcpFileEdit("b.md", "x", "y")], Repo);
        Assert.Equal(new EditPreviewLine.File("a.md"), lines[0]);
        Assert.Equal(new EditPreviewLine.File("b.md"), lines[3]);
    }

    [Fact]
    public void Allowances_CoverTheSameFile_HoweverItIsNamed()
    {
        var allowances = new EditAllowances(Repo);
        Assert.False(allowances.Cover(["a.md"]));
        allowances.Grant([Path.Combine(Repo, "a.md")]);
        Assert.True(allowances.Cover(["a.md"]));
        Assert.False(allowances.Cover(["a.md", "b.md"]));
        Assert.False(allowances.Cover([]));
    }

    [Fact]
    public void AllowingTheFile_ApprovesTheEdit()
    {
        using var pending = new PendingToolApproval("Edit a.md", "Edit");
        var granted = false;
        var allowance = new FileAllowance(pending, () => granted = true);
        allowance.Allow.Execute();
        Assert.True(granted);
        Assert.True(allowance.Granted.Value);
        Assert.Equal(ToolApprovalOutcome.Approved, pending.Outcome.Value);
    }
}
