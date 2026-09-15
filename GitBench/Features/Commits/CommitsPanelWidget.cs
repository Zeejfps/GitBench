using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

public sealed record CommitsPanelWidget : Widget
{
    internal const float WarningBarHeight = 24f;

    protected override IWidget Build(Context ctx)
    {
        var styles = ctx.Theme().Styles;
        var showBanner = new State<bool>(false);

        var commits = new CommitsView.Core(ctx);
        var scrollBar = ScrollBars.CreateVertical(ctx);

        return new BorderLayout
        {
            North = new CommitSearchBarView
            {
                OnQueryChanged = commits.SetSearchQuery,
                RemoteFilterActive = commits.RemoteFilterActive,
                OnToggleRemoteFilter = commits.ToggleRemoteFilter,
            },
            Center = new Raw { View = commits },
            East = new Raw { View = scrollBar },
            South = new Box
            {
                Height = showBanner.Bind(t => t ? WarningBarHeight : 0f),
                BorderSize = showBanner.Bind(t => t ? new BorderSizeStyle { Top = 1 } : new BorderSizeStyle()),
                Background = Prop.Bind(() => showBanner.Value ? styles.Value.Banner.Background : 0u),
                BorderColor = Prop.Bind(() => showBanner.Value
                    ? new BorderColorStyle { Top = styles.Value.Banner.Border }
                    : new BorderColorStyle()),
                Children =
                [
                    new Text
                    {
                        Value = L.T(s => s.CommitsTruncatedBanner),
                        Visible = showBanner.Bind(t => t),
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Banner.Text),
                    },
                ],
            },
        }
        .Use(_ => new ScrollSyncController(commits.Scroll, scrollBar))
        .Use(_ => new TruncationBanner(commits, showBanner));
    }
}

// The banner only appears once the user scrolls to the end of the list. Showing it steals its
// height from the list, pushing "the bottom" further away — so while visible, the at-bottom test
// gets that much extra slack (hysteresis, no flicker).
internal sealed class TruncationBanner : IDisposable
{
    private const float BottomSlack = 8f;

    private readonly CommitsView.Core _commits;
    private readonly State<bool> _showBanner;
    private bool _isTruncated;

    public TruncationBanner(CommitsView.Core commits, State<bool> showBanner)
    {
        _commits = commits;
        _showBanner = showBanner;

        _commits.Scroll.VerticalScrollPositionChanged += OnScrolled;
        _commits.TruncatedChanged += OnTruncatedChanged;
        OnTruncatedChanged(_commits.Truncated);
    }

    public void Dispose()
    {
        _commits.Scroll.VerticalScrollPositionChanged -= OnScrolled;
        _commits.TruncatedChanged -= OnTruncatedChanged;
    }

    private void OnScrolled(float normalized) => Update();

    private void OnTruncatedChanged(bool truncated)
    {
        _isTruncated = truncated;
        Update();
    }

    private void Update()
    {
        var threshold = _showBanner.Value
            ? CommitsPanelWidget.WarningBarHeight + BottomSlack
            : BottomSlack;
        _showBanner.Value = _isTruncated && _commits.DistanceFromBottom <= threshold;
    }
}
