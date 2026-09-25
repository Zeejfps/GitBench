using GitBench.Features.Pairing;
using GitBench.Localization;
using GitBench.Platform;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>A step the agent dropped from the roadmap is struck through; the others are not.</summary>
public sealed class PairingRoadmapRowTests
{
    [Fact]
    public void ADroppedStep_IsStruckThrough()
    {
        var canvas = Draw(new RoadmapEntry("Wire the admin page", Done: false, RoadmapChange.Removed));
        var title = Assert.Single(canvas.Texts, t => t.Inputs.Text == "Wire the admin page");
        var line = Assert.Single(canvas.Lines);
        Assert.Equal(line.Inputs.Start.Y, line.Inputs.End.Y);
        Assert.InRange(line.Inputs.Start.Y, title.Inputs.Position.Bottom, title.Inputs.Position.Top);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AStepStillOnTheRoadmap_IsNotStruckThrough(bool done, bool added)
    {
        var canvas = Draw(new RoadmapEntry("Wire the admin page", done, added ? RoadmapChange.Added : RoadmapChange.Kept));
        Assert.Contains(canvas.Texts, t => t.Inputs.Text == "Wire the admin page");
        Assert.Empty(canvas.Lines);
    }

    private static RecordingCanvas Draw(RoadmapEntry entry)
    {
        using var harness = GuiTestHarness.Create(
            ctx => new Column
            {
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [new PairingRoadmapRow { Entry = entry }],
            }.BuildView(ctx),
            width: 420,
            height: 120,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(new LocalizationService(new State<Locale>(Locale.En)));
                ctx.AddService<IPlatformShell>(new NoopPlatformShell());
            });
        harness.Layout();
        return harness.Render();
    }
}
