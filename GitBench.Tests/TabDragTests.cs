using GitBench.App;
using GitBench.Controls;
using GitBench.Features.FileBrowser;
using GitBench.Localization;
using GitBench.Theming;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Testing;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;
using Xunit;

namespace GitBench.Tests;

/// <summary>
/// Dragging a tab along the strip: that the gesture reorders the run, that a click is still a click,
/// and that a drag abandoned partway leaves the order alone.
/// </summary>
public sealed class TabDragTests : IDisposable
{
    private const int Width = 800;
    private const int Height = 200;

    private readonly FileBrowserTabs _files = new(_ => false);
    private readonly TabDrag _drag = new();
    private readonly List<string> _activated = [];

    private ContentTabRun _run = null!;
    private GuiTestHarness _harness = null!;
    private IDisposable _mounted = null!;

    public void Dispose()
    {
        _harness?.Dispose();
        _mounted?.Dispose();
    }

    [Fact]
    public void DraggingATabPastTheNextOne_PutsItAfterIt()
    {
        Open("a.cs", "b.cs", "c.cs");

        Drag("a.cs", onto: "b.cs");

        Assert.Equal(["b.cs", "a.cs", "c.cs"], Names());
    }

    [Fact]
    public void DraggingATabToTheFront_PutsItFirst()
    {
        Open("a.cs", "b.cs", "c.cs");

        Drag("c.cs", to: Left("a.cs"));

        Assert.Equal(["c.cs", "a.cs", "b.cs"], Names());
    }

    [Fact]
    public void DraggingATabToTheEnd_PutsItLast()
    {
        Open("a.cs", "b.cs", "c.cs");

        Drag("a.cs", to: Right("c.cs"));

        Assert.Equal(["b.cs", "c.cs", "a.cs"], Names());
    }

    [Fact]
    public void APressAndReleaseWithoutMoving_IsStillAClick()
    {
        // The gesture has to stay out of the way of the one it shares a button with.
        Open("a.cs", "b.cs");

        _harness.ClickOn(Tab("b.cs"));

        Assert.Equal(["b.cs"], _activated);
        Assert.Equal(["a.cs", "b.cs"], Names());
    }

    [Fact]
    public void ADragThatEndsWhereItStarted_ChangesNothingAndDoesNotActivate()
    {
        // Nudging a tab a few pixels and letting go is neither a move nor a click: the reader
        // started dragging, so the release is the end of that and not the start of anything.
        Open("a.cs", "b.cs");

        var from = Centre("a.cs");
        _harness.MoveTo(from.X, from.Y);
        _harness.Press();
        _harness.MoveTo(from.X + 20f, from.Y);
        _harness.Release();

        Assert.Equal(["a.cs", "b.cs"], Names());
        Assert.Empty(_activated);
    }

    [Fact]
    public void EscapeDuringADrag_AbandonsIt()
    {
        Open("a.cs", "b.cs", "c.cs");

        var from = Centre("a.cs");
        var onto = Centre("c.cs");
        _harness.MoveTo(from.X, from.Y);
        _harness.Press();
        _harness.MoveTo(onto.X, onto.Y);
        Assert.NotNull(_drag.Dragging.Value);

        _harness.PressKey(KeyboardKey.Escape);
        _harness.Release();

        Assert.Null(_drag.Dragging.Value);
        Assert.Equal(["a.cs", "b.cs", "c.cs"], Names());
    }

    [Fact]
    public void WhileDraggingOverItsOwnPlace_NoDropIsOffered()
    {
        // A line promising a move that would not happen is worse than no line.
        Open("a.cs", "b.cs");

        var from = Centre("a.cs");
        _harness.MoveTo(from.X, from.Y);
        _harness.Press();
        _harness.MoveTo(from.X + 10f, from.Y);

        Assert.NotNull(_drag.Dragging.Value);
        Assert.Null(_drag.Target.Value);
        _harness.Release();
    }

    private void Drag(string tab, string onto) => Drag(tab, Centre(onto));

    private void Drag(string tab, PointF to)
    {
        var from = Centre(tab);
        _harness.MoveTo(from.X, from.Y);
        _harness.Press();
        // Two moves: the first crosses the threshold and starts the drag, the second aims it.
        _harness.MoveTo(from.X + 10f, from.Y);
        _harness.MoveTo(to.X, to.Y);
        _harness.Release();
        _harness.Render();
    }

    private PointF Centre(string name)
    {
        var pos = _harness.Get(Tab(name)).Position;
        return new PointF(pos.Left + pos.Width * 0.5f, pos.Bottom + pos.Height * 0.5f);
    }

    private PointF Left(string name)
    {
        var pos = _harness.Get(Tab(name)).Position;
        return new PointF(pos.Left + 2f, pos.Bottom + pos.Height * 0.5f);
    }

    private PointF Right(string name)
    {
        var pos = _harness.Get(Tab(name)).Position;
        return new PointF(pos.Left + pos.Width - 2f, pos.Bottom + pos.Height * 0.5f);
    }

    private static string Tab(string name) => $"tab-{name}";

    private string[] Names() => _run.Tabs
        .OfType<ContentTab.File>()
        .Select(t => t.Tab.Name)
        .ToArray();

    /// <summary>Opens the named files and builds a strip over them.</summary>
    private void Open(params string[] names)
    {
        foreach (var name in names) _files.Open(name, pinned: true);

        _run = new ContentTabRun(null, _files.Items);
        _mounted = _drag.Bind(_run);

        _harness = GuiTestHarness.Create(
            ctx => new Box
            {
                Height = TabStrip.StripHeight,
                Children =
                [
                    new TabStrip
                    {
                        Tabs =
                        [
                            Each.Of(_run.Tabs, new DragTestTab { Drag = _drag, OnActivate = _activated.Add },
                                axis: Axis.Horizontal) with { CrossAxis = CrossAxisAlignment.Stretch },
                        ],
                    },
                ],
            }.BuildView(ctx),
            width: Width,
            height: Height,
            configure: ctx =>
            {
                ctx.AddService<IThemeService<ThemeStyles>>(
                    new ThemeService(new State<ThemeMode>(ThemeMode.Dark)));
                ctx.AddService<ILocalizationService>(
                    new LocalizationService(new State<Locale>(Locale.En)));
            });

        _harness.Render();
    }

    /// <summary>A tab with an id a test can find it by, over the same chrome the real ones use.</summary>
    private sealed record DragTestTab : Widget
    {
        public required TabDrag Drag { get; init; }
        public required Action<string> OnActivate { get; init; }

        protected override IWidget Build(Context ctx)
        {
            var tab = ctx.Require<ContentTab>();
            var name = ((ContentTab.File)tab).Tab.Name;

            return new TabChrome
            {
                Id = $"tab-{name}",
                Drag = Drag.Handle(tab),
                Label = name,
                ContentBackground = static s => s.Palette.Surface,
                IsActive = () => false,
                OnActivate = () => OnActivate(name),
            };
        }
    }
}
