using GitBench.Features.Commits;
using GitBench.Features.Review;
using Xunit;

namespace GitBench.Tests;

// The review window's multi-select state: paths normalize to range order (so the lead — the file the
// diff surface focuses — is the topmost one), and a reload prunes whatever left the range.
public sealed class ReviewSelectionTests
{
    private static readonly IReadOnlyList<FileChange> Files =
    [
        new FileChange("a.cs", null, FileChangeStatus.Modified),
        new FileChange("b.cs", null, FileChangeStatus.Modified),
        new FileChange("c.cs", null, FileChangeStatus.Modified),
    ];

    [Fact]
    public void Empty_selection_has_no_lead()
    {
        Assert.Null(ReviewSelection.Empty.Lead);
        Assert.Equal(0, ReviewSelection.Empty.Count);
    }

    [Fact]
    public void Create_sorts_paths_into_range_order()
    {
        var selection = ReviewSelection.Create(["c.cs", "a.cs"], "c.cs", "a.cs", Files);

        Assert.Equal(["a.cs", "c.cs"], selection.Paths);
        Assert.Equal("a.cs", selection.Lead);
        Assert.Equal("c.cs", selection.Anchor);
        Assert.Equal("a.cs", selection.Cursor);
    }

    [Fact]
    public void Create_dedupes_paths()
    {
        var selection = ReviewSelection.Create(["b.cs", "b.cs"], null, null, Files);

        Assert.Equal(["b.cs"], selection.Paths);
    }

    [Fact]
    public void Create_drops_paths_the_range_no_longer_has()
    {
        var selection = ReviewSelection.Create(["a.cs", "gone.cs", "c.cs"], "gone.cs", "c.cs", Files);

        Assert.Equal(["a.cs", "c.cs"], selection.Paths);
        Assert.Null(selection.Anchor);
        Assert.Equal("c.cs", selection.Cursor);
        Assert.False(selection.Contains("gone.cs"));
    }

    [Fact]
    public void Create_collapses_to_empty_when_nothing_survives()
    {
        Assert.Same(ReviewSelection.Empty, ReviewSelection.Create(["gone.cs"], "gone.cs", "gone.cs", Files));
    }

    [Fact]
    public void Single_anchors_and_leads_on_the_one_path()
    {
        var selection = ReviewSelection.Single("b.cs", Files);

        Assert.Equal(["b.cs"], selection.Paths);
        Assert.Equal("b.cs", selection.Lead);
        Assert.Equal("b.cs", selection.Anchor);
        Assert.Equal("b.cs", selection.Cursor);
    }
}
