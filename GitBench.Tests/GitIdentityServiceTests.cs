using GitBench.Features.Identity;
using GitBench.Git;
using GitBench.Messages;
using Xunit;

namespace GitBench.Tests;

public class GitIdentityServiceTests
{
    private sealed class FakeReader : IGitRawConfigReader
    {
        public Dictionary<string, List<string>> Remotes = new();
        public Dictionary<string, string> Urls = new();   // "<path>|<remote>" -> url
        public Dictionary<string, (string? Name, string? Email)> Local = new();

        public bool Available = true;

        public bool IsRepoAvailable(string repoPath) => Available;
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

    private sealed class FakeOverrides : IIdentityOverrides
    {
        public Func<string, Guid?> ByPath = _ => null;
        public Func<Guid, string?> PathById = _ => null;
        public Guid? GetIdentityOverrideByPath(string path) => ByPath(path);
        public string? GetRepoPathById(Guid repoId) => PathById(repoId);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"gb-{Guid.NewGuid():N}.json");

    private static (GitIdentityService svc, FakeReader reader, IdentityProfileService profiles) Build(
        params IdentityProfile[] seed)
    {
        var reader = new FakeReader();
        var profiles = new IdentityProfileService(seed, TempPath());
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

        var fp = Assert.IsType<Identity.FromProfile>(r);
        Assert.Equal("dev@series.ai", fp.Profile.UserEmail);
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

        Assert.Equal(work.Id, Assert.IsType<Identity.FromProfile>(r).Profile.Id);
    }

    [Fact]
    public void HostOnlyRuleMatchesOtherOwners()
    {
        var (svc, reader, _) = Build(Personal);
        var path = "/repos/side";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:someoneelse/toy.git";

        var r = svc.Resolve(path);

        Assert.Equal("me@personal.com", Assert.IsType<Identity.FromProfile>(r).Profile.UserEmail);
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

        var fc = Assert.IsType<Identity.FromConfig>(r);
        Assert.Empty(r.PrefixArgs);
        Assert.Equal("pinned@x.com", fc.UserEmail);
    }

    [Fact]
    public void ManualOverrideBeatsLocalConfig()
    {
        // A repo with an explicit local user.email, but the user picked a profile from the chip
        // menu. The deliberate override must win over local config (the venus bug).
        var work = Work;
        var reader = new FakeReader();
        var overrides = new FakeOverrides();
        var profiles = new IdentityProfileService(new[] { work }, TempPath());
        var svc = new GitIdentityService(reader, profiles, new FakeBus(), overrides);
        var path = "/repos/venus";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";
        reader.Local[path] = ("Zee", "zvasilyev@series.ai");

        // Without an override, local config is honored.
        Assert.IsType<Identity.FromConfig>(svc.Resolve(path));

        // Pick the profile via override.
        overrides.ByPath = _ => work.Id;
        svc.FlushAll();

        var r = svc.Resolve(path);
        Assert.Equal(work.Id, Assert.IsType<Identity.FromProfile>(r).Profile.Id);
        Assert.Contains("user.email=dev@series.ai", r.PrefixArgs);
    }

    [Fact]
    public void NoRemotesReportsNoRemotes()
    {
        var (svc, _, _) = Build(Work);
        var r = svc.Resolve("/repos/empty");
        Assert.IsType<Identity.NoRemote>(r);
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

        Assert.IsType<Identity.Unmatched>(r);
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

        Assert.Equal(work.Id, Assert.IsType<Identity.FromProfile>(r).Profile.Id);
    }

    [Fact]
    public void MemoFlushesWhenProfilesChange()
    {
        var (svc, reader, profiles) = Build();
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";

        Assert.IsType<Identity.Unmatched>(svc.Resolve(path)); // cached: no profiles yet

        profiles.Add(Work); // fires Changed -> FlushAll

        Assert.IsType<Identity.FromProfile>(svc.Resolve(path));
    }

    [Fact]
    public void LocalNameWithoutEmailIsHonoredNotOverridden()
    {
        // A repo with a deliberate local user.name but no local user.email must not have its name
        // overridden by an auto-matched profile.
        var (svc, reader, _) = Build(Work);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";
        reader.Local[path] = ("Bot Name", null);

        var r = svc.Resolve(path);

        var fc = Assert.IsType<Identity.FromConfig>(r);
        Assert.Empty(r.PrefixArgs);
        Assert.Equal("Bot Name", fc.UserName);
    }

    [Fact]
    public void UnavailableRepoIsTransientAndNotMemoized()
    {
        var (svc, reader, _) = Build(Work);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";

        // Volume not mounted yet: transient, must not be cached.
        reader.Available = false;
        Assert.IsType<Identity.Pending>(svc.Resolve(path));

        // Once available, the next resolve must succeed (the transient result wasn't pinned).
        reader.Available = true;
        Assert.IsType<Identity.FromProfile>(svc.Resolve(path));
    }

    [Fact]
    public void GitReadFailureIsTransient()
    {
        var reader = new ThrowingReader();
        var profiles = new IdentityProfileService(new[] { Work }, TempPath());
        var svc = new GitIdentityService(reader, profiles, new FakeBus());

        Assert.IsType<Identity.Pending>(svc.Resolve("/repos/locked"));
    }

    [Fact]
    public void ExplicitSigningKeyFormatIsEmitted()
    {
        // A bare-filename ssh signing key the heuristic can't detect — the explicit format wins.
        var p = new IdentityProfile(
            Guid.NewGuid(), "Work", "Work Dev", "dev@series.ai",
            SigningKey: "id_work.pub", SigningKeyFormat: "ssh",
            Match: new List<IdentityMatchRule> { new("github.com", "series-ai") });
        var (svc, reader, _) = Build(p);
        var path = "/repos/work";
        reader.Remotes[path] = new() { "origin" };
        reader.Urls[$"{path}|origin"] = "git@github.com:series-ai/app.git";

        var r = svc.Resolve(path);

        Assert.Contains("user.signingKey=id_work.pub", r.PrefixArgs);
        Assert.Contains("gpg.format=ssh", r.PrefixArgs);
    }

    [Fact]
    public void LocalConfigUnsetsKeysAKeylessProfileDoesNotUse()
    {
        // The pin path writes every managed key, unsetting the ones the profile doesn't carry, so
        // re-pinning a keyless profile clears a previous profile's leftover SSH/signing config.
        var keyless = new IdentityProfile(Guid.NewGuid(), "Personal", "Me", "me@home.com");
        var entries = LocalIdentityConfig.For(keyless).Entries().ToDictionary(e => e.Key, e => e.Value);

        Assert.Equal("Me", entries["user.name"]);
        Assert.Equal("me@home.com", entries["user.email"]);
        Assert.Null(entries["core.sshCommand"]);
        Assert.Null(entries["user.signingKey"]);
        Assert.Null(entries["commit.gpgsign"]);
        Assert.Null(entries["gpg.format"]);
    }

    [Fact]
    public void LocalConfigAndInjectionAgreeOnSigning()
    {
        // Injection and "pin to repo" must not diverge: the same signing config that gets injected
        // must also be the config a pin writes.
        var p = new IdentityProfile(Guid.NewGuid(), "Work", "Work Dev", "dev@series.ai",
            SigningKey: "id_work.pub", SigningKeyFormat: "ssh");
        var entries = LocalIdentityConfig.For(p).Entries().ToDictionary(e => e.Key, e => e.Value);

        Assert.Equal("id_work.pub", entries["user.signingKey"]);
        Assert.Equal("true", entries["commit.gpgsign"]);
        Assert.Equal("ssh", entries["gpg.format"]);
    }

    [Fact]
    public void RefsChangedFlushesOnlyTheNamedRepo()
    {
        // A commit/fetch in one repo must not dump every repo's cached identity (the re-resolution
        // storm) — only the named repo's memo entry is dropped.
        var reader = new FakeReader();
        var profiles = new IdentityProfileService(new[] { Work }, TempPath());
        var bus = new MessageBus();
        var repoA = Guid.NewGuid();
        var pathA = "/repos/a";
        var pathB = "/repos/b";
        var overrides = new FakeOverrides { PathById = id => id == repoA ? pathA : null };
        var svc = new GitIdentityService(reader, profiles, bus, overrides);

        foreach (var p in new[] { pathA, pathB })
        {
            reader.Remotes[p] = new() { "origin" };
            reader.Urls[$"{p}|origin"] = "git@github.com:series-ai/app.git";
        }

        svc.Resolve(pathA);
        svc.Resolve(pathB);
        Assert.NotNull(svc.Cached(pathA));
        Assert.NotNull(svc.Cached(pathB));

        bus.Broadcast(new RefsChangedMessage(repoA));

        Assert.Null(svc.Cached(pathA));    // flushed: its ref changed
        Assert.NotNull(svc.Cached(pathB)); // untouched: a different repo changed
    }

    private sealed class ThrowingReader : IGitRawConfigReader
    {
        public bool IsRepoAvailable(string repoPath) => true;
        public IReadOnlyList<string> GetRemoteNamesRaw(string repoPath) => throw new IOException("git remote: locked");
        public string? GetRemoteUrlRaw(string repoPath, string remoteName) => null;
        public (string? Name, string? Email) GetLocalIdentityRaw(string repoPath) => (null, null);
    }
}
