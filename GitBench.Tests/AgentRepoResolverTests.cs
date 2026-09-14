using System.Diagnostics;
using GitBench.Features.AgentConnections;
using GitBench.Features.Repos;
using GitBench.Git;
using Xunit;

namespace GitBench.Tests;

/// <summary>How a call's <c>repo</c> argument lands on an open repository: by name, by a path
/// inside it — the deepest match winning, so a path in a nested checkout picks the nested one —
/// and, with nothing named, on the session's last-driven repository or the active one.</summary>
public sealed class AgentRepoResolverTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-agent-repos-");
    private readonly RepoRegistry _registry;
    private readonly Repo _outer;
    private readonly Repo _inner;
    private readonly AgentRepoResolver _resolver;

    public AgentRepoResolverTests()
    {
        var outerPath = Path.Combine(_dir.Path, "outer");
        var innerPath = Path.Combine(outerPath, "nested");
        Directory.CreateDirectory(innerPath);
        Git(outerPath, "init", "-q");
        Git(innerPath, "init", "-q");

        var statePath = Path.Combine(_dir.Path, "repos.json");
        _registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath);
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(outerPath));
        Assert.Equal(OpenRepoOutcome.Opened, _registry.Open(innerPath));
        _outer = _registry.Repos.Single(r => r.Path == outerPath);
        _inner = _registry.Repos.Single(r => r.Path == innerPath);
        _resolver = new AgentRepoResolver(_registry, new NoReviewWindows());
    }

    public void Dispose() => _dir.Dispose();

    private static void Git(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    private static Repo Resolved(RepoResolution resolution) => Assert.IsType<RepoResolution.Resolved>(resolution).Repo;

    [Fact]
    public void ByName_IgnoringCase()
    {
        Assert.Equal(_outer.Id, Resolved(_resolver.Resolve(_outer.DisplayName.ToUpperInvariant(), new RepoDefault.None())).Id);
    }

    [Fact]
    public void ByPath_TheDeepestRepositoryWins()
    {
        Assert.Equal(_outer.Id, Resolved(_resolver.Resolve(_outer.Path, new RepoDefault.None())).Id);
        Assert.Equal(_outer.Id, Resolved(_resolver.Resolve(Path.Combine(_outer.Path, "src"), new RepoDefault.None())).Id);
        Assert.Equal(_inner.Id, Resolved(_resolver.Resolve(_inner.Path + Path.DirectorySeparatorChar, new RepoDefault.None())).Id);
        Assert.Equal(_inner.Id, Resolved(_resolver.Resolve(Path.Combine(_inner.Path, "deep", "er"), new RepoDefault.None())).Id);
    }

    [Fact]
    public void APathBesideTheRepositories_IsUnknown_AndListsThem()
    {
        var unknown = Assert.IsType<RepoResolution.Unknown>(_resolver.Resolve(Path.Combine(_dir.Path, "outerly"), new RepoDefault.None()));

        Assert.Equal(2, unknown.Open.Count);
    }

    [Fact]
    public void NothingNamed_TakesTheLastDriven_ThenTheActive()
    {
        _registry.SetActive(_outer.Id);

        Assert.Equal(_inner.Id, Resolved(_resolver.Resolve(null, new RepoDefault.LastDriven(_inner))).Id);
        Assert.Equal(_outer.Id, Resolved(_resolver.Resolve("  ", new RepoDefault.None())).Id);
    }

    [Fact]
    public void NothingNamed_AndARemovedLastDriven_FallsThrough()
    {
        _registry.SetActive(_outer.Id);
        var gone = new Repo(Guid.NewGuid(), Path.Combine(_dir.Path, "gone"), "gone");

        Assert.Equal(_outer.Id, Resolved(_resolver.Resolve(null, new RepoDefault.LastDriven(gone))).Id);
    }
}
