using GitBench.Features.Repos;
using PngSharp.Api;
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
        WriteImage(iconPath, 512, 256);
        var statePath = Path.Combine(_dir.Path, "state.json");

        Guid repoId;
        string managedPath;
        using (var registry = new RepoRegistry(RepoStateStore.Load(statePath), statePath))
        {
            Assert.Equal(OpenRepoOutcome.Opened, registry.Open(repoPath));
            repoId = registry.Active.Value!.Id;

            Assert.True(registry.SetCustomIcon(repoId, iconPath));
            managedPath = registry.Repos.Single(r => r.Id == repoId).CustomIconPath!;
            Assert.Equal(Path.Combine(_dir.Path, "repo-icons"), Path.GetDirectoryName(managedPath));
            Assert.NotEqual(iconPath, managedPath);
            var frame = Assert.IsType<GitBench.Features.Diff.ImageFrame>(RepoIconImage.Load(managedPath));
            Assert.Equal(128, frame.Width);
            Assert.Equal(64, frame.Height);
            Assert.Equal(512, RepoIconImage.Load(iconPath)!.Width);
        }

        File.Delete(iconPath);

        using (var restored = new RepoRegistry(RepoStateStore.Load(statePath), statePath))
        {
            Assert.Equal(
                managedPath,
                restored.Repos.Single(r => r.Id == repoId).CustomIconPath);
            Assert.NotNull(RepoIconImage.Load(managedPath));
            Assert.True(restored.SetCustomIcon(repoId, managedPath));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(managedPath)!));

            restored.SetCustomIcon(repoId, null);

            Assert.Null(restored.Repos.Single(r => r.Id == repoId).CustomIconPath);
        }
    }

    [Theory]
    [InlineData(1024, 512, 128, 64)]
    [InlineData(512, 1024, 64, 128)]
    [InlineData(333, 199, 128, 76)]
    [InlineData(16, 24, 16, 24)]
    public void ImportPreservesProportionsAndDoesNotUpscale(int width, int height, int expectedWidth, int expectedHeight)
    {
        var source = Path.Combine(_dir.Path, "source.png");
        WriteImage(source, width, height);
        var saved = RepoIconStore.Import(source, Path.Combine(_dir.Path, "repo-icons"));
        Assert.NotNull(saved);
        var image = RepoIconImage.Load(saved)!;
        Assert.Equal(expectedWidth, image.Width);
        Assert.Equal(expectedHeight, image.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, image.Rgba[..4]);
    }

    [Fact]
    public void DownsamplingPreservesTransparencyWithoutBleedingInvisibleColors()
    {
        var source = Path.Combine(_dir.Path, "transparent.png");
        var pixels = new byte[256 * 256 * 4];
        for (var i = 0; i < pixels.Length; i += 8)
        {
            pixels[i] = 255;
            pixels[i + 3] = 255;
            pixels[i + 6] = 255; // Transparent blue alongside opaque red.
        }
        Png.EncodeToFile(Png.CreateRgba(256, 256, pixels), source);
        var saved = RepoIconStore.Import(source, Path.Combine(_dir.Path, "repo-icons"));
        Assert.NotNull(saved);
        var image = RepoIconImage.Load(saved)!;
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, image.Rgba[..4]);
    }

    [Fact]
    public void FailedImportsPreservePreviousIconAndReplacingDoesNotOverwriteItsFile()
    {
        var repo = Path.Combine(_dir.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var state = Path.Combine(_dir.Path, "state.json");
        using var registry = new RepoRegistry(RepoStateStore.Load(state), state);
        registry.Open(repo);
        var id = registry.Active.Value!.Id;
        var source = Path.Combine(_dir.Path, "source.png");
        WriteImage(source, 1, 1);
        Assert.True(registry.SetCustomIcon(id, source));
        var original = registry.Repos.Single().CustomIconPath;
        Assert.False(registry.SetCustomIcon(id, Path.Combine(_dir.Path, "missing.png")));
        File.WriteAllText(source, "invalid image");
        Assert.False(registry.SetCustomIcon(id, source));
        Assert.Equal(original, registry.Repos.Single().CustomIconPath);
        Png.EncodeToFile(Png.CreateRgba(1, 1, [0, 255, 0, 255]), source);
        Assert.True(registry.SetCustomIcon(id, source));
        Assert.NotEqual(original, registry.Repos.Single().CustomIconPath);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, RepoIconImage.Load(original!)!.Rgba);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, RepoIconImage.Load(registry.Repos.Single().CustomIconPath!)!.Rgba);
    }

    private static void WriteImage(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
            pixels[i + 3] = 255;
        }
        Png.EncodeToFile(Png.CreateRgba(width, height, pixels), path);
    }
}
