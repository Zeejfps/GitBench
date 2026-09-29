using GitBench.App;
using Xunit;

namespace GitBench.Tests;

public sealed class SettingsWindowSizePreferenceTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-settings-size-prefs-");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ASavedSizeRoundTrips()
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        PreferencesStore.Save(path, Preferences.Default with { SettingsWindowWidth = 1200, SettingsWindowHeight = 950 });

        var loaded = PreferencesStore.Load(path);

        Assert.Equal(1200, loaded.SettingsWindowWidth);
        Assert.Equal(950, loaded.SettingsWindowHeight);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("null")]
    public void AMissingOrNonPositiveSizeFallsBackToTheDefault(string json)
    {
        var path = Path.Combine(_dir.Path, "prefs.json");
        File.WriteAllText(path, $$"""{ "schemaVersion": 1, "settingsWindowWidth": {{json}}, "settingsWindowHeight": {{json}} }""");

        var loaded = PreferencesStore.Load(path);

        Assert.Null(loaded.SettingsWindowWidth);
        Assert.Null(loaded.SettingsWindowHeight);
    }
}
