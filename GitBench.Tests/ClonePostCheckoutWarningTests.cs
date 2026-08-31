using GitBench.Features.Identity;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// `git clone` folds the post-checkout hook's exit status into its own, so a hook that fails (husky,
// git-lfs) makes git report a failed clone over a working tree that is completely fine. Reporting
// that as a failure loses the repo AND leaves a directory the user has to delete by hand before
// they can retry.
public sealed class ClonePostCheckoutWarningTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly WarningClone _git = new();

    public ClonePostCheckoutWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-clone-warning-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
    }

    [Fact]
    public void AWarnedCloneStillOpensTheRepoAndReportsWhatGitSaid()
    {
        _git.Warning = "post-checkout hook exited with 1";
        var shown = new List<ShowOperationErrorMessage>();
        _bus.Subscribe<ShowOperationErrorMessage>(shown.Add);

        var closed = false;
        var vm = Build();
        vm.CloseRequested += () => closed = true;
        vm.Url.Value = "git@github.com:series-ai/app.git";
        vm.ParentDir.Value = _root;
        RunClone(vm);

        Assert.Single(_registry.Repos);
        Assert.True(closed);
        Assert.Equal("post-checkout hook exited with 1", Assert.Single(shown).Message);
    }

    [Fact]
    public void ACleanCloneSaysNothing()
    {
        var shown = new List<ShowOperationErrorMessage>();
        _bus.Subscribe<ShowOperationErrorMessage>(shown.Add);

        var vm = Build();
        vm.Url.Value = "git@github.com:series-ai/app.git";
        vm.ParentDir.Value = _root;
        RunClone(vm);

        Assert.Single(_registry.Repos);
        Assert.Empty(shown);
    }

    // Hook output usually carries no git prefix at all, so the last line is the only anchor there is.
    [Fact]
    public void ErrorTailFallsBackToTheLastLineWhenGitPrefixesNothing()
    {
        var captured = "Cloning into 'app'...\nReceiving objects: 100% (12/12), done.\npost-checkout hook says no\n";
        Assert.Equal("post-checkout hook says no", GitProcessRunner.ErrorTail(captured));
    }

    [Fact]
    public void ErrorTailKeepsTheWholeBlockFromTheFirstPrefixedLine()
    {
        var captured = "Cloning into 'app'...\nerror: cannot spawn .git/hooks/post-checkout\nhint: check the file\n";
        Assert.Equal(
            "error: cannot spawn .git/hooks/post-checkout\nhint: check the file",
            GitProcessRunner.ErrorTail(captured));
    }

    [Fact]
    public void ErrorTailCollapsesProgressCarriageReturns()
    {
        var captured = "Receiving objects:  50% (6/12)\rReceiving objects: 100% (12/12), done.\n";
        Assert.Equal("Receiving objects: 100% (12/12), done.", GitProcessRunner.ErrorTail(captured));
    }

    private CloneRepoDialogViewModel Build()
    {
        var profiles = new IdentityProfileService(Array.Empty<IdentityProfile>(), Path.Combine(_root, "profiles.json"));
        var identity = new GitIdentityService(new StubReader(), profiles, _bus, _registry);
        return new CloneRepoDialogViewModel(_git, _registry, profiles, identity, _dispatcher, _bus, _loc);
    }

    private void RunClone(CloneRepoDialogViewModel vm)
    {
        vm.Clone.Execute();
        Pump.WaitFor(_dispatcher, () => !vm.Clone.IsRunning.Value, "the clone to complete");
        Assert.Null(vm.Clone.Error.Value);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class WarningClone : IGitRemoteOperations
    {
        public string? Warning;

        public CloneOutcome Clone(string url, string targetPath, LocalIdentityConfig? identity = null, Action<string>? onLine = null)
        {
            Directory.CreateDirectory(Path.Combine(targetPath, ".git"));
            return new CloneOutcome.Cloned(Path.GetFullPath(targetPath), Warning);
        }

        public IReadOnlyList<string> GetRemoteNames(Repo repo) => Array.Empty<string>();
        public string? GetRemoteUrl(Repo repo, string remoteName) => null;
        public GitOutcome AddRemote(Repo repo, string name, string url) => GitOutcome.Ok;
        public GitOutcome EditRemote(Repo repo, string oldName, string newName, string url) => GitOutcome.Ok;
        public GitOutcome Push(Repo repo, bool force = false) => GitOutcome.Ok;
        public PullOutcome Pull(Repo repo, PullStrategy? strategy = null) => PullOutcome.Ok;
        public GitOutcome Fetch(Repo repo) => GitOutcome.Ok;
    }

    private sealed class StubReader : IGitRawConfigReader
    {
        public bool IsRepoAvailable(string repoPath) => true;
        public IReadOnlyList<string> GetRemoteNamesRaw(string repoPath) => Array.Empty<string>();
        public string? GetRemoteUrlRaw(string repoPath, string remoteName) => null;
        public (string? Name, string? Email) GetLocalIdentityRaw(string repoPath) => (null, null);
        public void AttachIdentityResolver(GitIdentityService identity) { }
    }
}
