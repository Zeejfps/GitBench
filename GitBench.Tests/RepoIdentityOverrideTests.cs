using Xunit;

namespace GitBench.Tests;

public class RepoIdentityOverrideTests
{
    private static RepoRegistry NewRegistry()
    {
        var state = RepoStateStore.Load(Path.Combine(Path.GetTempPath(), $"gb-state-{Guid.NewGuid():N}.json"));
        return new RepoRegistry(state, Path.Combine(Path.GetTempPath(), $"gb-state-{Guid.NewGuid():N}.json"));
    }

    private static string NewGitRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"gb-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    [Fact]
    public void OverrideRoundTripsByPath()
    {
        var registry = NewRegistry();
        var dir = NewGitRepo();
        registry.Open(dir);
        var repo = registry.Active.Value!;
        var profileId = Guid.NewGuid();

        registry.SetIdentityOverride(repo.Id, profileId);

        // Look up via the exact path the resolver uses (Path.GetFullPath, trailing sep trimmed).
        var key = Path.GetFullPath(repo.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(profileId, registry.GetIdentityOverrideByPath(key));
        Assert.Equal(profileId, registry.GetIdentityOverride(repo.Id));
    }

    private sealed class FakeReader : IGitRawConfigReader
    {
        public string Url = "git@github.com:series-ai/app.git";
        public IReadOnlyList<string> GetRemoteNamesRaw(string repoPath) => new[] { "origin" };
        public string? GetRemoteUrlRaw(string repoPath, string remoteName) => Url;
        public (string? Name, string? Email) GetLocalIdentityRaw(string repoPath) => (null, null);
    }

    private sealed class FakeBus : IMessageBus
    {
        public void Broadcast<T>(T message = default) where T : struct { }
        public void Subscribe<T>(Action<T> handler) where T : struct { }
        public void Unsubscribe<T>(Action<T> handler) where T : struct { }
    }

    [Fact]
    public void OverrideWinsOverAutoMatchThroughRegistry()
    {
        var registry = NewRegistry();
        var dir = NewGitRepo();
        registry.Open(dir);
        var repo = registry.Active.Value!;

        var work = new IdentityProfile(Guid.NewGuid(), "Work", "W", "w@series.ai",
            Match: new List<IdentityMatchRule> { new("github.com", "series-ai") });
        var personal = new IdentityProfile(Guid.NewGuid(), "Personal", "P", "p@home.com",
            Match: new List<IdentityMatchRule>());
        var profiles = new IdentityProfileService(new[] { work, personal },
            Path.Combine(Path.GetTempPath(), $"gb-{Guid.NewGuid():N}.json"));

        var identity = new GitIdentityService(new FakeReader(), profiles, new FakeBus());
        identity.SetOverrideLookup(registry.GetIdentityOverrideByPath);

        // Auto-detect picks Work (owner-specific rule).
        Assert.Equal(work.Id, identity.Resolve(repo.Path).ProfileId);

        // Switch to Personal via override, mirroring the context-menu action.
        registry.SetIdentityOverride(repo.Id, personal.Id);
        identity.FlushAll();

        Assert.Equal(personal.Id, identity.Resolve(repo.Path).ProfileId);
    }

    [Fact]
    public void OverrideLookupToleratesTrailingSeparatorMismatch()
    {
        // Stored repo path with a trailing separator (folder pickers / worktree output produce
        // these), looked up by the trimmed key the resolver uses. Must still match.
        var dir = NewGitRepo();
        var statePath = Path.Combine(Path.GetTempPath(), $"gb-state-{Guid.NewGuid():N}.json");
        var repoId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var state = new RepoStateStore.State(
            new List<Repo> { new(repoId, dir + Path.DirectorySeparatorChar, "repo") },
            new List<Group> { new(Guid.NewGuid(), "g", false, new List<Guid> { repoId }) },
            repoId,
            new(), new(),
            new Dictionary<Guid, Guid> { [repoId] = profileId });
        var registry = new RepoRegistry(state, statePath);

        var trimmedKey = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(profileId, registry.GetIdentityOverrideByPath(trimmedKey));
    }

    [Fact]
    public void ClearingOverrideReturnsNull()
    {
        var registry = NewRegistry();
        var dir = NewGitRepo();
        registry.Open(dir);
        var repo = registry.Active.Value!;

        registry.SetIdentityOverride(repo.Id, Guid.NewGuid());
        registry.SetIdentityOverride(repo.Id, null);

        Assert.Null(registry.GetIdentityOverrideByPath(repo.Path));
    }
}
