using GitBench.Controls.Dialogs;
using GitBench.Features.Settings;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>The shortcuts dialog mounted headlessly: what it shows, and that Esc dismisses it.</summary>
public class KeyboardShortcutsDialogTests
{
    private const int WindowWidth = 800;
    private const int WindowHeight = 700;

    private static GuiTestHarness Mount(Action onClose) => Mount(onClose, new KeyMap());

    private static GuiTestHarness Mount(Action onClose, KeyMap keys)
    {
        var harness = Create(onClose, keys);
        // The list's scrollbar appears during the first layout and takes its gutter on the next, which
        // shifts every row; settle that before a test reads a position or clicks one.
        harness.Layout();
        return harness;
    }

    private static GuiTestHarness Create(Action onClose, KeyMap keys) => GuiTestHarness.Create(
        ctx => new Box
        {
            Width = WindowWidth,
            Height = WindowHeight,
            Children =
            [
                new Center
                {
                    Child = new KeyboardShortcutsDialog { OnClose = onClose }
                        .WithController<DialogKbmController>(),
                },
            ],
        }.BuildView(ctx),
        width: WindowWidth,
        height: WindowHeight,
        configure: ctx =>
        {
            ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
            ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            ctx.AddService<IKeyMap>(keys);
            ctx.AddService(keys);
        });

    [Fact]
    public void ClickingACommandsKeys_ThenPressingAChord_RebindsIt()
    {
        var keys = new KeyMap();
        using var harness = Mount(() => { }, keys);

        harness.ClickOn(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh));
        harness.Layout();
        Assert.True(HasText(harness.Render(), "Press the new shortcut"));

        harness.PressKey(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift);
        harness.Layout();

        Assert.Equal([new KeyGesture(KeyboardKey.R, InputModifiers.Control | InputModifiers.Shift)], keys.GesturesFor(KeyCommand.Refresh));
        var canvas = harness.Render();
        Assert.True(HasText(canvas, "Ctrl+Shift+R"));
        Assert.False(HasText(canvas, "Press the new shortcut"));
        Assert.True(HasText(canvas, "Reset all"));
    }

    [Fact]
    public void AModifierOnItsOwn_IsNotAGesture()
    {
        var keys = new KeyMap();
        using var harness = Mount(() => { }, keys);

        harness.ClickOn(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh));
        harness.PressKey(KeyboardKey.LeftShift, InputModifiers.Shift);
        harness.Layout();

        Assert.True(keys.IsDefault(KeyCommand.Refresh));
        Assert.True(HasText(harness.Render(), "Press the new shortcut"));
    }

    [Fact]
    public void EscapeWhileRecording_CancelsTheRecording_NotTheDialog()
    {
        var closed = false;
        var keys = new KeyMap();
        using var harness = Mount(() => closed = true, keys);

        harness.ClickOn(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh));
        harness.PressKey(KeyboardKey.Escape);
        harness.Layout();

        Assert.False(closed);
        Assert.True(keys.IsDefault(KeyCommand.Refresh));
        var canvas = harness.Render();
        Assert.False(HasText(canvas, "Press the new shortcut"));
        Assert.True(HasText(canvas, "F5"));
    }

    [Fact]
    public void WhileRecording_KeysDoNotReachTheSearchBox()
    {
        var keys = new KeyMap();
        using var harness = Mount(() => { }, keys);

        harness.ClickOn(KeyboardShortcutsDialog.CapsId(KeyCommand.Refresh));
        harness.PressKey(KeyboardKey.F6);
        harness.Layout();

        Assert.Equal([new KeyGesture(KeyboardKey.F6)], keys.GesturesFor(KeyCommand.Refresh));
        Assert.Equal(string.Empty, ((TextInputView)harness.Get(KeyboardShortcutsDialog.SearchInputId)).Text);
    }

    [Fact]
    public void ResetPutsTheDefaultBack()
    {
        var keys = new KeyMap();
        keys.Rebind(KeyCommand.Refresh, new KeyGesture(KeyboardKey.F6));
        using var harness = Mount(() => { }, keys);

        harness.ClickOn(KeyboardShortcutsDialog.ResetId(KeyCommand.Refresh));
        harness.Layout();

        Assert.True(keys.IsDefault(KeyCommand.Refresh));
        Assert.True(HasText(harness.Render(), "F5"));
    }

    [Fact]
    public void ResetAllPutsEveryDefaultBack()
    {
        var keys = new KeyMap();
        keys.Rebind(KeyCommand.Refresh, new KeyGesture(KeyboardKey.F6));
        keys.Rebind(KeyCommand.SaveFile, new KeyGesture(KeyboardKey.F7));
        using var harness = Mount(() => { }, keys);

        harness.ClickOn(KeyboardShortcutsDialog.ResetAllId);
        harness.Layout();

        Assert.Empty(keys.Overrides);
    }

    [Fact]
    public void ShowsTheFirstSection_WithItsCommandsAndCaps()
    {
        using var harness = Mount(() => { });
        var keys = new KeyMap();

        var canvas = harness.Render();

        Assert.True(HasText(canvas, "Keyboard shortcuts"));
        Assert.True(HasText(canvas, "Application"));
        Assert.True(HasText(canvas, "Refresh"));
        Assert.True(HasText(canvas, keys.Display(KeyCommand.Refresh)));
        Assert.True(HasText(canvas, keys.Display(KeyCommand.ToggleRepoBar)));
        Assert.True(HasText(canvas, "Switch to repository 9"));
    }

    [Fact]
    public void TypingFiltersTheList_WithoutAClickFirst()
    {
        using var harness = Mount(() => { });

        harness.Type("F12");
        harness.Layout();

        var canvas = harness.Render();
        Assert.True(HasText(canvas, "Code navigation"));
        Assert.True(HasText(canvas, "Go to definition"));
        Assert.False(HasText(canvas, "Application"));
        Assert.False(HasText(canvas, "Refresh"));
    }

    [Fact]
    public void AQueryNothingMatches_SaysSo()
    {
        using var harness = Mount(() => { });

        harness.Type("zzzz");
        harness.Layout();

        Assert.True(HasText(harness.Render(), "No shortcuts match."));
    }

    [Fact]
    public void EscapeCloses()
    {
        var closed = false;
        using var harness = Mount(() => closed = true);
        harness.MoveTo(WindowWidth / 2f, WindowHeight / 2f);

        harness.PressKey(KeyboardKey.Escape);

        Assert.True(closed);
    }

    private static bool HasText(RecordingCanvas canvas, string text)
    {
        foreach (var drawn in canvas.Texts)
            if (drawn.Inputs.Text.Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }
}
