using GitBench.App;
using GitBench.Input;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Rebound shortcuts as a stored preference. Entries are read leniently, one at a time: a command
/// or key this version does not know drops that entry alone, never the file.
/// </summary>
public sealed class KeyBindingsPreferenceTests : IDisposable
{
    private readonly TempDir _dir = new("gitbench-keybindings-prefs-");

    public void Dispose() => _dir.Dispose();

    private string PathFor(string name) => Path.Combine(_dir.Path, name);

    private string WriteFile(string keyBindingsJson)
    {
        var path = PathFor("prefs.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": 1,
          "theme": "Light",
          "keyBindings": {{keyBindingsJson}}
        }
        """);
        return path;
    }

    [Fact]
    public void TheDefaultHasNoOverrides()
    {
        Assert.Empty(Preferences.Default.KeyBindings);
    }

    [Fact]
    public void OverridesRoundTripThroughTheFile()
    {
        var path = PathFor("prefs.json");
        var ctrlShiftR = new KeyGesture(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift);
        var saved = Preferences.Default with
        {
            KeyBindings =
            [
                new KeyBinding(KeyCommand.Refresh, [ctrlShiftR]),
                new KeyBinding(KeyCommand.ListActivate, [new KeyGesture(KeyboardKey.Space), new KeyGesture(KeyboardKey.Enter)]),
            ],
        };

        PreferencesStore.Save(path, saved);
        var loaded = PreferencesStore.Load(path);

        Assert.Equal(saved.KeyBindings, loaded.KeyBindings, KeyBindingEquality.Instance);
    }

    [Fact]
    public void AnUnknownCommand_DropsOnlyItsEntry()
    {
        var loaded = PreferencesStore.Load(WriteFile("""
            [
              { "command": "NoSuchCommand", "keys": ["F5"] },
              { "command": "Refresh", "keys": ["Control+Shift+R"] }
            ]
            """));

        var binding = Assert.Single(loaded.KeyBindings);
        Assert.Equal(KeyCommand.Refresh, binding.Command);
        Assert.Equal(new KeyGesture(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift), Assert.Single(binding.Gestures));
        Assert.Equal(Theming.ThemeMode.Light, loaded.Theme);
    }

    [Fact]
    public void AnEntryWithNoReadableKey_IsDropped()
    {
        var loaded = PreferencesStore.Load(WriteFile("""
            [
              { "command": "Refresh", "keys": ["NoSuchKey"] },
              { "command": "SaveFile", "keys": [] },
              { "command": "FindFile" }
            ]
            """));

        Assert.Empty(loaded.KeyBindings);
    }

    [Fact]
    public void AnUnreadableKeyAmongReadableOnes_IsSkipped()
    {
        var loaded = PreferencesStore.Load(WriteFile("""
            [{ "command": "Refresh", "keys": ["NoSuchKey", "F6"] }]
            """));

        var binding = Assert.Single(loaded.KeyBindings);
        Assert.Equal([new KeyGesture(KeyboardKey.F6)], binding.Gestures);
    }

    [Fact]
    public void AFileWithoutTheField_HasNoOverrides()
    {
        var path = PathFor("prefs.json");
        File.WriteAllText(path, """{ "schemaVersion": 1 }""");

        Assert.Empty(PreferencesStore.Load(path).KeyBindings);
    }

    private sealed class KeyBindingEquality : IEqualityComparer<KeyBinding>
    {
        public static readonly KeyBindingEquality Instance = new();

        public bool Equals(KeyBinding? x, KeyBinding? y) =>
            x is not null && y is not null && x.Command == y.Command && x.Gestures.SequenceEqual(y.Gestures);

        public int GetHashCode(KeyBinding obj) => obj.Command.GetHashCode();
    }
}
