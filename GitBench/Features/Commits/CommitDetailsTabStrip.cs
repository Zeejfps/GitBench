using GitBench.Controls;
using GitBench.Features.Diff;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Commits;

/// <summary>
/// The tab strip across the top of the commit-details (metadata/diff) region: a fixed "Details" tab
/// (commit metadata) followed by one tab per open file. The active tab takes the shared row-selection
/// fill; file tabs carry a close button. Clicking a tab activates it; opening a file from the list
/// adds or focuses its tab through the <see cref="CommitDetailsViewModel"/>.
/// </summary>
internal sealed record CommitDetailsTabStrip : Widget
{
    public const float StripHeight = TabStrip.Height;

    public required CommitDetailsViewModel Vm { get; init; }

    /// <summary>What the active tab wears: the panel the strip sits over.</summary>
    internal static uint Content(ThemeStyles s) => s.CommitDetailsView.Background;

    protected override IWidget Build(Context ctx) => new TabStrip
    {
        // A plane of its own, above the panel — the active tab drops back to the panel's own colour,
        // and that difference is what reads as the tab being the thing below it.
        Background = Theme.Color(s => s.Palette.SurfaceRaised),
        Tabs =
        [
            new CommitDetailsTab { Vm = Vm },
            Each.Of(Vm.OpenTabs, new CommitFileTabButton { Vm = Vm }, axis: Axis.Horizontal)
                with { CrossAxis = CrossAxisAlignment.Stretch },
        ],
    };
}

/// <summary>The leftmost, always-present tab: shows the commit metadata. Not closable.</summary>
internal sealed record CommitDetailsTab : Widget
{
    public required CommitDetailsViewModel Vm { get; init; }

    protected override IWidget Build(Context ctx) => new TabChrome
    {
        Label = L.T(s => s.CommitsDetailsTab),
        ContentBackground = CommitDetailsTabStrip.Content,
        IsActive = () => Vm.SelectedPath.Value == null,
        OnActivate = () => Vm.ActivateTab(null),
    };
}

/// <summary>One open file's tab. Resolves its <see cref="CommitFileTab"/> from the list scope.</summary>
internal sealed record CommitFileTabButton : Widget
{
    public required CommitDetailsViewModel Vm { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var tab = ctx.Require<CommitFileTab>();
        // Present only in a review window; elsewhere null ⇒ the tab shows no Viewed mark.
        var reviewed = ctx.Get<IReviewedFileTracker>();
        return new TabChrome
        {
            Label = tab.FileName,
            ContentBackground = CommitDetailsTabStrip.Content,
            IsActive = () => Vm.SelectedPath.Value == tab.Path,
            OnActivate = () => Vm.ActivateTab(tab.Path),
            OnClose = () => Vm.CloseTab(tab.Path),
            Leading = reviewed == null ? null : ViewedMark(reviewed, tab.Path),
        };
    }

    static IWidget ViewedMark(IReviewedFileTracker reviewed, string path) => new Text
    {
        FontFamily = LucideIcons.FontFamily,
        FontSize = FontSize.Caption,
        Value = LucideIcons.CheckSquare,
        VAlign = TextAlignment.Center,
        Visible = Prop.Bind(() =>
        {
            _ = reviewed.Revision.Value;
            return reviewed.IsViewed(path);
        }),
        Color = Theme.Color(s => s.Status.Success),
    };
}
