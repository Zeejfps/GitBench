using Xunit;

namespace GitBench.Tests;

public class GitIdentityServiceTests
{
    private sealed class FakeReader : IGitRawConfigReader
    {
        public Dictionary<string, List<string>> Remotes = new();
        public Dictionary<string, string> Urls = new();   // "<path>|<remote>" -> url
        public Dictionary<string, (string? Name, string? Email)> Local = new();

        public IReadOnlyList<string> GetRemoteNamesRaw(string repoPath)
            => Remotes.TryGetValue(repoPath, out var r) ? r : new List<string>();
        public string? GetRemoteUrlRaw(string repoPath, string remoteName)
            => Urls.TryGetValue($"{repoPath}|{remoteName}", out var u) ? u : null;
        public (string? Name, string? Email) GetLocalIdentityRaw(string repoPath)
            => Local.TryGetValue(repoPath, out var v) ? v : (null, null);
    }

    private sealed class FakeBus : IMessageBus
    {
        public void Broadcast<T>(T message = default) where T : struct { }
        public void Subscribe<T>(Action<T> handler) where T : struct { }
        public void Unsubscribe<T>(Action<T> handler) where T : struct { }
    }

    private static (GitIdentityService svc, FakeReader reader, IdentityProfileService profiles) Build(
        params IdentityProfile[] seed)
    {
        var reader = new FakeReader();
        var profiles = new IdentityProfileService(seed, Path.Combine(Path.GetTempPath(), $"gb-{Guid.NewGuid():N}.json"));
        var svc = new GitIdentityService(reader, profiles, new FakeBus());
        return (svc, reader, profiles);
    }

    private static IdentityProfile Work => new(
        Guid.NewGuid(), "Work", "Work Dev", "dev@series.ai",
        Match: new List<IdentityMatchRule> { new("github.com", "series-ai") });

    private static IdentityProfile Personal => new(
        Guid.NewGuid(), "Personal", "Me", "me@personal.com",
        Match: new List<IdentityMatchRule> { new("github.com") }); // host-only catch-all

    [Fact]
    public void MatchesProfileByHostAndOwner()
    {
        var (svc, reader, _) = Build(Work);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";

        var r = svc.Resolve(path);

        Assert.Equal(IdentitySource.Profile, r.Source);
        Assert.Equal("dev@series.ai", r.UserEmail);
        Assert.Contains("user.name=Work Dev", r.PrefixArgs);
        Assert.Contains("user.email=dev@series.ai", r.PrefixArgs);
    }

    [Fact]
    public void OwnerSpecificRuleBeatsHostOnlyRule()
    {
        // Personal is host-only catch-all; Work is owner-specific. A series-ai repo must pick Work.
        var work = Work;
        var (svc, reader, _) = Build(Personal, work);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "https://github.com/series-ai/app.git";

        var r = svc.Resolve(path);

        Assert.Equal(work.Id, r.ProfileId);
    }

    [Fact]
    public void HostOnlyRuleMatchesOtherOwners()
    {
        var (svc, reader, _) = Build(Personal);
        var path = "/repos/side";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:someoneelse/toy.git";

        var r = svc.Resolve(path);

        Assert.Equal(IdentitySource.Profile, r.Source);
        Assert.Equal("me@personal.com", r.UserEmail);
    }

    [Fact]
    public void ExplicitLocalEmailIsHonoredAndNotInjected()
    {
        var (svc, reader, _) = Build(Work);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";
        reader.Local[path] = ("Pinned", "pinned@x.com");

        var r = svc.Resolve(path);

        Assert.Equal(IdentitySource.RepoConfig, r.Source);
        Assert.Empty(r.PrefixArgs);
        Assert.Equal("pinned@x.com", r.UserEmail);
    }

    [Fact]
    public void ManualOverrideBeatsLocalConfig()
    {
        // A repo with an explicit local user.email, but the user picked a profile from the chip
        // menu. The deliberate override must win over local config (the venus bug).
        var work = Work;
        var (svc, reader, _) = Build(work);
        var path = "/repos/venus";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";
        reader.Local[path] = ("Zee", "zvasilyev@series.ai");

        // Without an override, local config is honored.
        Assert.Equal(IdentitySource.RepoConfig, svc.Resolve(path).Source);

        // Pick the profile via override.
        svc.SetOverrideLookup(_ => work.Id);
        svc.FlushAll();

        var r = svc.Resolve(path);
        Assert.Equal(IdentitySource.Profile, r.Source);
        Assert.Equal(work.Id, r.ProfileId);
        Assert.Contains("user.email=dev@series.ai", r.PrefixArgs);
    }

    [Fact]
    public void NoRemotesReportsNoRemotes()
    {
        var (svc, _, _) = Build(Work);
        var r = svc.Resolve("/repos/empty");
        Assert.Equal(IdentitySource.NoRemotes, r.Source);
        Assert.Empty(r.PrefixArgs);
    }

    [Fact]
    public void UnmatchedRemoteReportsNoMatch()
    {
        var (svc, reader, _) = Build(Work);
        var path = "/repos/gitlab";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@gitlab.com:foo/bar.git";

        var r = svc.Resolve(path);

        Assert.Equal(IdentitySource.NoMatch, r.Source);
        Assert.Empty(r.PrefixArgs);
    }

    [Fact]
    public void SshKeyProducesQuotedSshCommandWithIdentitiesOnly()
    {
        var p = new IdentityProfile(
            Guid.NewGuid(), "Work", "Work Dev", "dev@series.ai",
            SshKeyPath: "~/.ssh/id_work",
            Match: new List<IdentityMatchRule> { new("github.com", "series-ai") });
        var (svc, reader, _) = Build(p);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";

        var r = svc.Resolve(path);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = $"core.sshCommand=ssh -i \"{home}/.ssh/id_work\" -o IdentitiesOnly=yes";
        Assert.Contains(expected, r.PrefixArgs);
    }

    [Fact]
    public void SigningOnlyInjectedWhenKeySet()
    {
        var (svc, reader, _) = Build(Work); // Work has no signing key
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";

        var r = svc.Resolve(path);

        Assert.DoesNotContain("commit.gpgsign=true", r.PrefixArgs);
    }

    [Fact]
    public void OriginIsPreferredOverOtherRemotes()
    {
        var work = Work;
        var (svc, reader, _) = Build(work, Personal);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "upstream", "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";
        reader.Urls[$"{path}|upstream"] = "git@github.com:someoneelse/app.git";

        var r = svc.Resolve(path);

        Assert.Equal(work.Id, r.ProfileId);
    }

    [Fact]
    public void MemoFlushesWhenProfilesChange()
    {
        var (svc, reader, profiles) = Build();
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";

        Assert.Equal(IdentitySource.NoMatch, svc.Resolve(path).Source); // cached: no profiles yet

        profiles.Add(Work); // fires Changed -> FlushAll

        Assert.Equal(IdentitySource.Profile, svc.Resolve(path).Source);
    }
}
