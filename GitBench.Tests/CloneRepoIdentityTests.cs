using GitBench.Features.Identity;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Localization;
using GitBench.Messages;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

// A clone has no repo for the identity resolver to read, so whichever profile the remote needs has
// to travel with the command itself — and a profile the user picked by hand has to outlive the
// clone, or the first fetch afterwards falls back to the wrong key.
public sealed class CloneRepoIdentityTests : IDisposable
{
    private readonly string _root;
    private readonly RepoRegistry _registry;
    private readonly QueuedDispatcher _dispatcher = new();
    private readonly MessageBus _bus = new();
    private readonly LocalizationService _loc = new(new State<Locale>(Locale.En));
    private readonly RecordingClone _git = new();

    private static readonly IdentityProfile Work = new(
        Guid.NewGuid(), "Work", "Work Dev", "dev@series.ai",
        SshKeyPath: "~/.ssh/id_work",
        Match: new List<IdentityMatchRule> { new("github.com", "series-ai") });

    private static readonly IdentityProfile Personal = new(
        Guid.NewGuid(), "Personal", "Me", "me@home.com",
        SshKeyPath: "~/.ssh/id_personal");

    private const string WorkUrl = "git@github.com:series-ai/app.git";

    public CloneRepoIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitbench-clone-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var statePath = Path.Combine(_root, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
    }

    [Fact]
    public void AppliesTheProfileTheUrlMatchesWhenNothingIsPicked()
    {
        var vm = Build(Work, Personal);
        vm.Url.Value = WorkUrl;
        vm.ParentDir.Value = _root;

        RunClone(vm);

        Assert.Equal("dev@series.ai", _git.Identity!.UserEmail);
        Assert.Contains("id_work", _git.Identity.SshCommand!);
    }

    [Fact]
    public void AnExplicitPickBeatsTheUrlMatch()
    {
        var vm = Build(Work, Personal);
        vm.Url.Value = WorkUrl;
        vm.ParentDir.Value = _root;
        vm.ProfileId.Value = Personal.Id;

        RunClone(vm);

        Assert.Equal("me@home.com", _git.Identity!.UserEmail);
        Assert.Contains("id_personal", _git.Identity.SshCommand!);
    }

    [Fact]
    public void SendsNoIdentityWhenNothingMatchesAndNothingIsPicked()
    {
        var vm = Build(Personal);   // Personal carries no match rules
        vm.Url.Value = WorkUrl;
        vm.ParentDir.Value = _root;

        RunClone(vm);

        Assert.True(_git.Cloned);
        Assert.Null(_git.Identity);
    }

    [Fact]
    public void AnExplicitPickBecomesTheNewRepoIdentityOverride()
    {
        var vm = Build(Work, Personal);
        vm.Url.Value = WorkUrl;
        vm.ParentDir.Value = _root;
        vm.ProfileId.Value = Personal.Id;

        RunClone(vm);

        var repo = Assert.Single(_registry.Repos);
        Assert.Equal(Personal.Id, _registry.GetIdentityOverride(repo.Id));
    }

    // The resolver reaches the same profile from the remote URL on its own, so pinning an override
    // here would only freeze a match the user never asked to freeze.
    [Fact]
    public void AUrlMatchLeavesNoOverrideBehind()
    {
        var vm = Build(Work, Personal);
        vm.Url.Value = WorkUrl;
        vm.ParentDir.Value = _root;

        RunClone(vm);

        var repo = Assert.Single(_registry.Repos);
        Assert.Null(_registry.GetIdentityOverride(repo.Id));
    }

    private CloneRepoDialogViewModel Build(params IdentityProfile[] seed)
    {
        var profiles = new IdentityProfileService(seed, Path.Combine(_root, "profiles.json"));
        var identity = new GitIdentityService(new StubReader(), profiles, _bus, _registry);
        return new CloneRepoDialogViewModel(_git, _registry, profiles, identity, _dispatcher, _bus, _loc);
    }

    private void RunClone(CloneRepoDialogViewModel vm)
    {
        vm.Clone.Execute();
        // The command hands its work to the thread pool and posts the continuation back; drain
        // until it has landed, so the registry is touched exactly where it expects.
        Pump.WaitFor(_dispatcher, () => !vm.Clone.IsRunning.Value, "the clone to complete");
        Assert.Null(vm.Clone.Error.Value);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Captures the identity the clone was asked to run under and lays down a working tree the
    // registry will accept, so the post-clone Open is the real thing.
    private sealed class RecordingClone : IGitRemoteOperations
    {
        public LocalIdentityConfig? Identity;
        public bool Cloned;

        public CloneOutcome Clone(string url, string targetPath, LocalIdentityConfig? identity = null, Action<string>? onLine = null)
        {
            Identity = identity;
            Cloned = true;
            Directory.CreateDirectory(Path.Combine(targetPath, ".git"));
            return new CloneOutcome.Cloned(Path.GetFullPath(targetPath));
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
