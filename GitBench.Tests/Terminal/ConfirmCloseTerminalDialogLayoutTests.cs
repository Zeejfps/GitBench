using GitBench.Controls;
using GitBench.Features.Terminal;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.AppUtils;
using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Desktop.Inspection;
using ZGF.Gui.Testing;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests.Terminal;

/// <summary>
/// That the close-terminal confirmation comes to rest at a UI scale other than 100%.
/// </summary>
/// <remarks>
/// At 125% its body sat at exactly its content height, give or take the rounding of fractional line
/// heights. Read as overflow, that showed a scrollbar, whose gutter rewrapped the body a line taller;
/// the taller body fit, the bar hid, the body unwrapped, and the dialog flipped between the two
/// every frame.
/// </remarks>
public class ConfirmCloseTerminalDialogLayoutTests
{
    [Fact]
    public void At125Percent_TheDialogSettlesWithoutAScrollbar()
    {
        const float scale = 1.25f;
        using var fonts = new FreeTypeFontBackend();
        var font = fonts.LoadFontFromMemory(
            EmbeddedAssets.LoadBytes(typeof(Context).Assembly, "Inter-Regular.ttf"), (int)(16 * scale));
        var icons = fonts.LoadFontFromMemory(
            EmbeddedAssets.LoadBytes(typeof(LucideIcons).Assembly, "Lucide.ttf"), (int)(16 * scale));

        using var harness = GuiTestHarness.Create(
            ctx => new CenterView
            {
                Children =
                {
                    new ConfirmCloseTerminalDialog { Terminal = "pwsh", OnClose = () => { }, OnConfirm = () => { } }
                        .BuildView(ctx),
                },
            },
            width: 900, height: 700,
            configure: ctx =>
            {
                var canvas = new RasterCanvas(900, 700, fonts, font, dpiScale: scale);
                canvas.RegisterFont(LucideIcons.FontFamily, icons);
                ctx.Canvas = canvas;
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
            });

        var pane = harness.Root.SelfAndDescendants().OfType<VerticalScrollPane>().Single();
        var bar = harness.Root.SelfAndDescendants().OfType<VerticalScrollBar>().Single();

        harness.Layout();
        var settled = pane.Position;
        for (var i = 0; i < 3; i++)
        {
            harness.Layout();
            Assert.Equal(settled, pane.Position);
            Assert.False(bar.IsVisible);
        }
    }
}
