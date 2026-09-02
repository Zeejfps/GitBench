using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

// A worktree row is named after its folder, and worktree discovery rewrites that name on every
// sync. A name the user types over it therefore has to live somewhere the sync can't reach, or it
// would survive exactly until the next `git worktree list`.
public sealed class WorktreeRenameTests : IDisposable
{
    private readonly string _dir;
    private readonly string _statePath;
    private readonly RepoRegistry _registry;
    private readonly Guid _primaryId;

    public WorktreeRenameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gitbench-wtname-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _statePath = Path.Combine(_dir, "state.json");

        _registry = new RepoRegistry(RepoStateStore.Load(_statePath), _statePath);
        _primaryId = Guid.NewGuid();
        _registry.Repos.Add(new Repo(_primaryId, Path.Combine(_dir, "app"), "app"));
        Sync();
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Sync(string? branch = "feature/login")
        => _registry.ReplaceWorktreesFor(_primaryId, [new WorktreeDescriptor(WorktreePath, "app-feature-login", branch)]);

    private string WorktreePath => Path.Combine(_dir, "app-feature-login");

    private Repo Worktree => _registry.Repos.First(r => r.IsWorktree);

    [Fact]
    public void AWorktreeIsNamedAfterItsFolder()
    {
        Assert.Equal("app-feature-login", Worktree.DisplayName);
        Assert.Null(Worktree.CustomName);
    }

    [Fact]
    public void ATypedNameReplacesTheFolderName()
    {
        _registry.RenameRepo(Worktree.Id, "Login work");

        Assert.Equal("Login work", Worktree.DisplayName);
        Assert.Equal("Login work", Worktree.CustomName);
    }

    // The point of the whole exercise: discovery runs constantly and must not undo the rename.
    [Fact]
    public void ATypedNameSurvivesWorktreeDiscovery()
    {
        _registry.RenameRepo(Worktree.Id, "Login work");

        Sync(branch: "feature/login-v2");

        Assert.Equal("Login work", Worktree.DisplayName);
        Assert.Equal("feature/login-v2", Worktree.Branch);
    }

    [Fact]
    public void ATypedNameSurvivesAReload()
    {
        _registry.RenameRepo(Worktree.Id, "Login work");
        _registry.Dispose(); // flushes the pending state write

        using var reloaded = new RepoRegistry(RepoStateStore.Load(_statePath), _statePath);

        Assert.Equal("Login work", reloaded.Repos.First(r => r.IsWorktree).DisplayName);
    }

    [Fact]
    public void ResettingTheNameGoesBackToTrackingTheFolder()
    {
        _registry.RenameRepo(Worktree.Id, "Login work");

        _registry.ResetRepoName(Worktree.Id);

        Assert.Equal("app-feature-login", Worktree.DisplayName);
        Assert.Null(Worktree.CustomName);
    }

    // Typing the folder name back is the same thing as resetting: no override is left pinned.
    [Fact]
    public void TypingTheFolderNameClearsTheOverride()
    {
        _registry.RenameRepo(Worktree.Id, "Login work");

        _registry.RenameRepo(Worktree.Id, "app-feature-login");

        Assert.Null(Worktree.CustomName);
    }

    [Fact]
    public void ASubmoduleNameIsStillGitsToSay()
    {
        var submoduleId = Guid.NewGuid();
        _registry.Repos.Add(new Repo(submoduleId, Path.Combine(_dir, "app", "vendor", "lib"), "vendor/lib", _primaryId)
        {
            Kind = RepoKind.Submodule,
        });

        _registry.RenameRepo(submoduleId, "Vendored lib");

        Assert.Equal("vendor/lib", _registry.Repos.First(r => r.Id == submoduleId).DisplayName);
    }
}
