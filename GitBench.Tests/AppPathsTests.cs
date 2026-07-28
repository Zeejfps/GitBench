using GitBench.App;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// First run after a rename is the case that silently reads as data loss, so it is covered
/// headlessly rather than by launching the app. See docs/plans/rename-safe-identity.md.
/// </summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "gitbench-appdata-" + Guid.NewGuid().ToString("N"));

    private string Current => Path.Combine(_appData, AppIdentity.DataFolderName);
    private string Legacy => Path.Combine(_appData, AppIdentity.LegacyDataFolderNames[0]);

    public void Dispose()
    {
        if (Directory.Exists(_appData)) Directory.Delete(_appData, recursive: true);
    }

    private static string Resolve(string appData, Func<string, string?>? env = null) =>
        AppPaths.ResolveRoot(env ?? (_ => null), appData);

    private static void WriteFile(string directory, string relativePath, string contents)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    [Fact]
    public void FirstRunWithNoPriorStateCreatesNothing()
    {
        var root = Resolve(_appData);

        Assert.Equal(Current, root);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void FirstRunAfterRenameCopiesStateFromTheOldFolder()
    {
        WriteFile(Legacy, "preferences.json", "{\"theme\":\"dark\"}");
        WriteFile(Legacy, Path.Combine("nested", "state.json"), "{}");

        var root = Resolve(_appData);

        Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(root, "preferences.json")));
        Assert.True(File.Exists(Path.Combine(root, "nested", "state.json")));
    }

    [Fact]
    public void MigrationLeavesTheOldFolderIntactForARollback()
    {
        WriteFile(Legacy, "preferences.json", "{}");

        Resolve(_appData);

        Assert.True(File.Exists(Path.Combine(Legacy, "preferences.json")));
    }

    [Fact]
    public void MigrationLeavesNoStagingFolderBehind()
    {
        WriteFile(Legacy, "preferences.json", "{}");

        Resolve(_appData);

        Assert.Equal(
            [AppIdentity.DataFolderName, AppIdentity.LegacyDataFolderNames[0]],
            Directory.EnumerateDirectories(_appData).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void AnExistingFolderIsNeverReseeded()
    {
        WriteFile(Current, "preferences.json", "current");
        WriteFile(Legacy, "preferences.json", "legacy");

        var root = Resolve(_appData);

        Assert.Equal("current", File.ReadAllText(Path.Combine(root, "preferences.json")));
    }

    [Fact]
    public void MigrationDoesNotRepeatAfterTheUserDeletesFiles()
    {
        WriteFile(Legacy, "preferences.json", "{}");
        var root = Resolve(_appData);
        File.Delete(Path.Combine(root, "preferences.json"));

        Resolve(_appData);

        Assert.False(File.Exists(Path.Combine(root, "preferences.json")));
    }

    [Fact]
    public void TheEnvironmentOverrideWins()
    {
        WriteFile(Legacy, "preferences.json", "{}");
        var scratch = Path.Combine(_appData, "scratch");

        var root = Resolve(_appData, name => name == AppIdentity.DataDirEnvVar ? scratch : null);

        Assert.Equal(scratch, root);
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public void TheLegacyEnvironmentOverrideStillWorks()
    {
        var scratch = Path.Combine(_appData, "scratch");

        var root = Resolve(_appData, name => name == AppIdentity.LegacyDataDirEnvVars[0] ? scratch : null);

        Assert.Equal(scratch, root);
    }

    [Fact]
    public void TheCurrentEnvironmentOverrideBeatsTheLegacyOne()
    {
        var current = Path.Combine(_appData, "current");
        var legacy = Path.Combine(_appData, "legacy");

        var root = Resolve(_appData, name => name == AppIdentity.DataDirEnvVar ? current : legacy);

        Assert.Equal(current, root);
    }
}
