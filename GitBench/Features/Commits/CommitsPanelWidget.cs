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
    private const float WarningBarHeight = 24f;

    protected override IWidget Build(Context ctx)
    {
        var styles = ctx.Theme().Styles;
        var truncated = new State<bool>(false);

        var commits = new CommitsView.Core(ctx);
        var scrollBar = ScrollBars.CreateVertical(ctx);

        return new BorderLayout
        {
            North = new CommitSearchBarView { OnQueryChanged = commits.SetSearchQuery },
            Center = new Raw { View = commits },
            East = new Raw { View = scrollBar },
            South = new Box
            {
                Height = truncated.Bind(t => t ? WarningBarHeight : 0f),
                BorderSize = truncated.Bind(t => t ? new BorderSizeStyle { Top = 1 } : new BorderSizeStyle()),
                Background = Prop.Bind(() => truncated.Value ? styles.Value.Banner.Background : 0u),
                BorderColor = Prop.Bind(() => truncated.Value
                    ? new BorderColorStyle { Top = styles.Value.Banner.Border }
                    : new BorderColorStyle()),
                Children =
                [
                    new Text
                    {
                        Value = L.T(s => s.CommitsTruncatedBanner),
                        Visible = truncated.Bind(t => t),
                        HAlign = TextAlignment.Center,
                        VAlign = TextAlignment.Center,
                        Color = Theme.Color(s => s.Banner.Text),
                    },
                ],
            },
        }
        .Use(_ => new CommitsPanelController(commits, scrollBar, truncated));
    }
}

internal sealed class CommitsPanelController : IDisposable
{
    private readonly CommitsView.Core _commits;
    private readonly VerticalScrollBarView _scrollBar;
    private readonly State<bool> _truncated;

    public CommitsPanelController(
        CommitsView.Core commits, VerticalScrollBarView scrollBar, State<bool> truncated)
    {
        _commits = commits;
        _scrollBar = scrollBar;
        _truncated = truncated;

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
    }

    private void OnCommitsScaleChanged(float scale)
    {
        _scrollBar.Width = scale < 1f ? ScrollBarSync.Thickness : 0f;
        _scrollBar.Scale = scale;
    }

    private void OnScrollBarScrollChanged(float normalized)
    {
        _commits.SetNormalizedScrollPosition(normalized);
    }

    private void OnTruncatedChanged(bool truncated) => _truncated.Value = truncated;
}
