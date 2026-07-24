using GitBench.Features.Commits;
using GitBench.Features.LocalChanges;
using Xunit;

namespace GitBench.Tests;

// A cold load is "loading with nothing on screen to keep" — what the Changes surfaces stand a
// skeleton up for. A refresh that still holds file lists must not read as cold: tearing them down
// would flicker content the user is reading.
public sealed class LocalChangesStateTests
{
    private static readonly IReadOnlyList<FileChange> OneFile =
        new[] { new FileChange("a.txt", null, FileChangeStatus.Modified) };

    [Fact]
    public void Loading_with_empty_lists_is_cold()
    {
        var state = LocalChangesState.Initial with { HasRepo = true, IsLoading = true };

        Assert.True(state.IsColdLoad);
        Assert.Equal(LocalChangesState.LoadingPlaceholder, state.Placeholder);
    }

    [Fact]
    public void Loading_with_files_on_screen_is_not_cold()
    {
        var state = LocalChangesState.Initial with { HasRepo = true, IsLoading = true, Unstaged = OneFile };

        Assert.False(state.IsColdLoad);
        Assert.Null(state.Placeholder);
    }

    [Fact]
    public void Settled_empty_tree_is_not_cold()
    {
        var state = LocalChangesState.Initial with { HasRepo = true };

        Assert.False(state.IsColdLoad);
    }

    [Fact]
    public void Load_failure_is_not_cold()
    {
        var state = LocalChangesState.Initial with { HasRepo = true, IsLoading = true, LoadError = "boom" };

        Assert.False(state.IsColdLoad);
        Assert.Equal("boom", state.Placeholder);
    }
}
