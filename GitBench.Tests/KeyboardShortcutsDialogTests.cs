using GitBench.Controls.Dialogs;
using GitBench.Features.Settings;
using GitBench.Input;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Gui;
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

    private static GuiTestHarness Mount(Action onClose) => GuiTestHarness.Create(
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
        });

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
