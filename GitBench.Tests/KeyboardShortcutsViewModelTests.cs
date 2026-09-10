using GitBench.Features.Settings;
using GitBench.Input;
using GitBench.Localization;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

public class KeyboardShortcutsViewModelTests
{
    private static KeyboardShortcutsViewModel Create(Locale locale = Locale.En) =>
        new(new KeyMap(), new LocalizationService(new State<Locale>(locale)));

    private static IEnumerable<ShortcutRow> Rows(KeyboardShortcutsViewModel vm) =>
        vm.Sections.Value.SelectMany(s => s.Rows);

    [Fact]
    public void ListsEveryCommandOnce_GroupedBySection()
    {
        var vm = Create();

        Assert.Equal(KeyCommandSections.All, vm.Sections.Value.Select(s => s.Section));
        Assert.Equal(Enum.GetValues<KeyCommand>().Order(), Rows(vm).Select(r => r.Command).Order());
        foreach (var section in vm.Sections.Value)
            foreach (var row in section.Rows)
                Assert.Equal(section.Section, KeyCommandSections.Of(row.Command));
    }

    [Fact]
    public void CapsAreTheKeyMapsGestures_WithoutRepeats()
    {
        var keys = new KeyMap();
        var vm = Create();

        foreach (var row in Rows(vm))
        {
            var expected = keys.GesturesFor(row.Command).Select(g => g.Display).Distinct();
            Assert.Equal(expected, row.Caps);
            Assert.Equal(keys.Display(row.Command), row.Caps[0]);
        }
    }

    [Fact]
    public void RepoHotkeysNameTheirSlot()
    {
        var vm = Create();
        var rows = Rows(vm).Where(r => KeyCommands.RepoHotkeySlot(r.Command) is not null).ToList();

        Assert.Equal(9, rows.Count);
        for (var slot = 1; slot <= 9; slot++)
            Assert.Contains(slot.ToString(), rows[slot - 1].Label);
    }

    [Fact]
    public void EveryLocaleNamesEverySectionAndCommand()
    {
        foreach (var option in LocaleOptions.All)
        {
            var vm = Create(option.Locale);
            foreach (var section in vm.Sections.Value)
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Title), $"{option.Locale}: {section.Section}");
                foreach (var row in section.Rows)
                    Assert.False(string.IsNullOrWhiteSpace(row.Label), $"{option.Locale}: {row.Command}");
            }
        }
    }

    [Fact]
    public void AQueryMatchesACommandsName_IgnoringCase()
    {
        var vm = Create();

        vm.Query.Value = "find FILE";

        var rows = Rows(vm).ToList();
        Assert.Equal([KeyCommand.FindFile], rows.Select(r => r.Command));
        Assert.False(vm.NoMatches.Value);
    }

    [Fact]
    public void AQueryMatchesACap()
    {
        var keys = new KeyMap();
        var vm = Create();

        vm.Query.Value = keys.Display(KeyCommand.ToggleRepoBar).ToLowerInvariant();

        Assert.Contains(KeyCommand.ToggleRepoBar, Rows(vm).Select(r => r.Command));
        Assert.All(Rows(vm), r => Assert.Contains(r.Caps, c => c.Contains(vm.Query.Value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AQueryMatchingASectionTitle_KeepsTheWholeSection()
    {
        var vm = Create();

        vm.Query.Value = "terminal";

        var section = Assert.Single(vm.Sections.Value);
        Assert.Equal(KeyCommandSection.Terminal, section.Section);
        Assert.Equal(KeyCommandSections.CommandsIn(KeyCommandSection.Terminal), section.Rows.Select(r => r.Command));
    }

    [Fact]
    public void SectionsWithNoMatchingRow_Disappear()
    {
        var vm = Create();

        vm.Query.Value = "F12";

        Assert.Equal([KeyCommandSection.CodeNavigation], vm.Sections.Value.Select(s => s.Section));
    }

    [Fact]
    public void AQueryNothingMatches_LeavesNoSections()
    {
        var vm = Create();

        vm.Query.Value = "no such command";

        Assert.Empty(vm.Sections.Value);
        Assert.True(vm.NoMatches.Value);
    }

    [Fact]
    public void ClearingTheQuery_ShowsEverythingAgain()
    {
        var vm = Create();
        vm.Query.Value = "F12";

        vm.Query.Value = "   ";

        Assert.Equal(Enum.GetValues<KeyCommand>().Order(), Rows(vm).Select(r => r.Command).Order());
    }

    [Fact]
    public void CommittingARecording_RebindsTheCommandAndTheRowFollows()
    {
        var keys = new KeyMap();
        var vm = new KeyboardShortcutsViewModel(keys, new LocalizationService(new State<Locale>(Locale.En)));
        var gesture = new KeyGesture(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift);

        vm.BeginRecording(KeyCommand.Refresh);
        Assert.Equal(KeyCommand.Refresh, vm.Recording.Value);
        vm.CommitRecording(gesture);

        Assert.Null(vm.Recording.Value);
        Assert.Equal([gesture], keys.GesturesFor(KeyCommand.Refresh));
        var row = Rows(vm).Single(r => r.Command == KeyCommand.Refresh);
        Assert.Equal(["Ctrl+Shift+R"], row.Caps);
        Assert.False(row.IsDefault);
        Assert.True(vm.HasOverrides.Value);
    }

    [Fact]
    public void CancellingARecording_ChangesNothing()
    {
        var keys = new KeyMap();
        var vm = new KeyboardShortcutsViewModel(keys, new LocalizationService(new State<Locale>(Locale.En)));

        vm.BeginRecording(KeyCommand.Refresh);
        vm.CancelRecording();
        vm.CommitRecording(new KeyGesture(KeyboardKey.F6));

        Assert.Null(vm.Recording.Value);
        Assert.True(keys.IsDefault(KeyCommand.Refresh));
        Assert.False(vm.HasOverrides.Value);
    }

    [Fact]
    public void ResettingARow_PutsItsDefaultBack()
    {
        var keys = new KeyMap();
        var vm = new KeyboardShortcutsViewModel(keys, new LocalizationService(new State<Locale>(Locale.En)));
        vm.BeginRecording(KeyCommand.Refresh);
        vm.CommitRecording(new KeyGesture(KeyboardKey.F6));

        vm.Reset(KeyCommand.Refresh);

        var row = Rows(vm).Single(r => r.Command == KeyCommand.Refresh);
        Assert.Equal(["F5"], row.Caps);
        Assert.True(row.IsDefault);
        Assert.False(vm.HasOverrides.Value);
    }

    [Fact]
    public void ARowNamesTheCommandsItsKeysAlsoFire()
    {
        var keys = new KeyMap();
        var vm = new KeyboardShortcutsViewModel(keys, new LocalizationService(new State<Locale>(Locale.En)));

        vm.BeginRecording(KeyCommand.SaveFile);
        vm.CommitRecording(new KeyGesture(KeyboardKey.F5));

        Assert.Equal(["Refresh"], Rows(vm).Single(r => r.Command == KeyCommand.SaveFile).ConflictsWith);
        Assert.Equal(["Save file"], Rows(vm).Single(r => r.Command == KeyCommand.Refresh).ConflictsWith);
        Assert.Empty(Rows(vm).Single(r => r.Command == KeyCommand.FindFile).ConflictsWith);
    }

    [Fact]
    public void LabelsFollowTheLocale()
    {
        var locale = new State<Locale>(Locale.En);
        var vm = new KeyboardShortcutsViewModel(new KeyMap(), new LocalizationService(locale));
        var english = vm.Sections.Value[0].Title;

        locale.Value = Locale.Es;

        Assert.NotEqual(english, vm.Sections.Value[0].Title);
    }
}
