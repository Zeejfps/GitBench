using GitBench.Features.Repos;
using Xunit;

namespace GitBench.Tests;

public sealed class RepoCustomIconTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-repo-icon-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Custom_icon_can_be_set_persisted_and_cleared()
    {
        var repoPath = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        var iconPath = Path.Combine(_dir.Path, "icon.png");
        File.WriteAllBytes(iconPath, [1, 2, 3]);
        var statePath = Path.Combine(_dir.Path, "state.json");

        Guid repoId;
        using (var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath))
        {
            Assert.Equal(OpenRepoOutcome.Opened, registry.Open(repoPath));
            repoId = registry.Active.Value!.Id;

            registry.SetCustomIcon(repoId, iconPath);

            Assert.Equal(
                Path.GetFullPath(iconPath),
                registry.Repos.Single(r => r.Id == repoId).CustomIconPath);
        }

        using (var restored = new RepoRegistry(RepoStateStore.Load(statePath), statePath))
        {
            Assert.Equal(
                Path.GetFullPath(iconPath),
                restored.Repos.Single(r => r.Id == repoId).CustomIconPath);

            restored.SetCustomIcon(repoId, null);

            Assert.Null(restored.Repos.Single(r => r.Id == repoId).CustomIconPath);
        }
    }
}
