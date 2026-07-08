using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.VerticalScrollBar;
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
        .Use(_ => new CommitsPanelController(commits, scrollBar, showBanner));
    }
}

internal sealed class CommitsPanelController : IDisposable
{
    private const float BottomSlack = 8f;

    private readonly CommitsView.Core _commits;
    private readonly VerticalScrollBarView _scrollBar;
    private readonly State<bool> _showBanner;
    private bool _isTruncated;

    public CommitsPanelController(
        CommitsView.Core commits, VerticalScrollBarView scrollBar, State<bool> showBanner)
    {
        _commits = commits;
        _scrollBar = scrollBar;
        _showBanner = showBanner;

        _commits.ScrollPositionChanged += OnCommitsScrollChanged;
        _commits.ScaleChanged += OnCommitsScaleChanged;
        _commits.TruncatedChanged += OnTruncatedChanged;
        _scrollBar.ScrollPositionChanged += OnScrollBarScrollChanged;
        OnTruncatedChanged(_commits.Truncated);
        OnCommitsScaleChanged(_commits.Scale);
    }

    public void Dispose()
    {
        _commits.ScrollPositionChanged -= OnCommitsScrollChanged;
        _commits.ScaleChanged -= OnCommitsScaleChanged;
        _commits.TruncatedChanged -= OnTruncatedChanged;
        _scrollBar.ScrollPositionChanged -= OnScrollBarScrollChanged;
    }

    private void OnCommitsScrollChanged(float normalized)
    {
        _scrollBar.SetNormalizedScrollPosition(normalized);
        UpdateBanner();
    }

    private void OnCommitsScaleChanged(float scale)
    {
        _scrollBar.Width = scale < 1f ? ScrollBarSync.Thickness : 0f;
        _scrollBar.Scale = scale;
        UpdateBanner();
    }

    private void OnScrollBarScrollChanged(float normalized)
    {
        _commits.SetNormalizedScrollPosition(normalized);
    }

    private void OnTruncatedChanged(bool truncated)
    {
        _isTruncated = truncated;
        UpdateBanner();
    }

    // The banner only appears once the user scrolls to the end of the list. Showing it
    // steals its height from the list, pushing "the bottom" further away — so while
    // visible, the at-bottom test gets that much extra slack (hysteresis, no flicker).
    private void UpdateBanner()
    {
        var threshold = _showBanner.Value
            ? CommitsPanelWidget.WarningBarHeight + BottomSlack
            : BottomSlack;
        _showBanner.Value = _isTruncated && _commits.DistanceFromBottom <= threshold;
    }
}
