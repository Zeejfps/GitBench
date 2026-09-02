using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.AppUtils;
using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// That the paste confirmation's footer holds its three buttons, in every language it ships in.
/// </summary>
/// <remarks>
/// <para>
/// A dialog footer normally carries two buttons and is sized for two. This one carries three, which
/// puts it close enough to the edge that a translation decides whether the last one is readable —
/// and the first version of it ran off the edge in English before it ever reached a translator.
/// </para>
/// <para>
/// Laid out against real font metrics, because the question is about text width and a stub canvas
/// answering a fixed number per glyph cannot be wrong in the way that matters here. The bundled
/// italic is the only face the test assembly can count on across platforms; it is a shade wider than
/// the face the application draws with, which makes this the conservative measurement.
/// </para>
/// </remarks>
public class TerminalPasteDialogLayoutTests
{
    [Theory]
    [MemberData(nameof(Locales))]
    public void EveryLocale_FitsItsThreeButtonsInsideTheDialog(Locale locale)
    {
        var fonts = new FreeTypeFontBackend();
        var font = fonts.LoadFontFromMemory(
            EmbeddedAssets.LoadBytes(typeof(LucideIcons).Assembly, "Inter-Italic.ttf"), 16);

        using var harness = GuiTestHarness.CreateRaster(
            ctx => new ConfirmPasteDialog
            {
                Lines = 19,
                FirstLine = "git status",
                OnClose = () => { },
                OnRun = () => { },
                OnFlatten = () => { },
            }.BuildView(ctx),
            fonts, font, (int)ConfirmPasteDialog.FrameWidth + 200, 320,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(locale)));
                ctx.AddService<IMessageBus>(new MessageBus());
            });

        harness.Layout();

        var s = Strings.For(locale);
        var buttons = new[] { s.TerminalPasteConfirmRun, s.CommonCancel, s.TerminalPasteConfirmFlatten }
            .Select(label => harness.Get(label).Position)
            .ToArray();

        // The row is padded evenly, so the left edge of the first button is the margin the last one
        // has to stay inside of at the other end. Overflow shows up as a right edge past it.
        var margin = buttons.Min(b => b.Left);
        var reached = buttons.Max(b => b.Right);

        Assert.True(
            reached <= ConfirmPasteDialog.FrameWidth - margin,
            $"{locale}: the footer reaches {reached:0} of {ConfirmPasteDialog.FrameWidth - margin:0} available, " +
            $"so its last button is cut off. Shorten a label or widen the dialog.");
    }

    public static TheoryData<Locale> Locales()
    {
        var data = new TheoryData<Locale>();
        foreach (var locale in Enum.GetValues<Locale>()) data.Add(locale);
        return data;
    }
}
