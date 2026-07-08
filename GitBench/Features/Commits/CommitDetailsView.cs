using GitBench.App;
using GitBench.Controls;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Commits;

/// <summary>
/// The History pane's commit details panel: the reused <see cref="CommitChangesPanelView"/> (top,
/// always visible) over the reused <see cref="CommitDiffTabsPanelView"/> in a draggable split, with
/// the loading skeleton, no-selection/error placeholder, and enter fades handled here. Owns the
/// <see cref="CommitDetailsViewModel"/> both panels bind to.
/// </summary>
internal sealed class CommitDetailsView : ContainerView
{
    private readonly Context _ctx;
    private readonly IThemeService<ThemeStyles> _theme;
    private readonly RectView _panel;
    // The "Content" surface: a draggable split of the file list (top, always visible) over the tabbed
    // metadata/diff region (bottom). Opening a file swaps the bottom region to its diff; the list stays.
    private readonly VerticalSplitContainer _contentView;
    private readonly FlexColumnView _placeholderHost;
    private readonly TextView _placeholder;
    // Details fade up as a commit's data arrives; the placeholder text blooms in. Both park when
    // settled, and neither replays on a commit-to-commit change of already-shown details.
    private readonly Tween _enterTween;
    private readonly Tween _placeholderTween;
    private enum Shown { None, Content, Skeleton, Placeholder }
    private Shown _shown = Shown.None;
    private CommitDetailsViewModel? _vm;

    public CommitDetailsView(Context ctx)
    {
        _ctx = ctx;
        var input = ctx.Require<InputSystem>();
        _theme = ctx.Theme();
        var preferences = ctx.Require<PreferencesService>();
        var vm = ctx.Require<CommitDetailsViewModel>();

        var changesPanel = new CommitChangesPanelView(ctx, vm);
        var tabbedRegion = new CommitDiffTabsPanelView(ctx, vm);

        var splitterHovered = new State<bool>(false);
        var splitter = new RectView();
        splitter.BindThemedBackgroundColor(_theme, s =>
            splitterHovered.Value ? s.CommitDetailsView.SplitterHover : s.CommitDetailsView.SplitterIdle);
        // Top: the file list, always visible. Bottom: the tabbed metadata/diff region (the larger
        // share), shown once a commit is loaded (BottomVisible toggled off SetRenderState).
        _contentView = new VerticalSplitContainer(changesPanel, tabbedRegion, splitter,
            bottomFraction: preferences.Current.CommitDetailsSplitFraction)
        {
            BottomVisible = false,
            FractionChanged = preferences.SetCommitDetailsSplitFraction,
        };
        splitter.UseController(input, () => new SplitterController(
            ctx,
            DragAxis.Y,
            _contentView.AdjustBottomFractionByPixels,
            h => splitterHovered.Value = h));

        _placeholder = new TextView(ctx.Canvas)
        {
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _placeholder.BindThemedTextColor(_theme, s => s.CommitDetailsView.PlaceholderText);
        _placeholderHost = new FlexColumnView
        {
            MainAxisAlignment = MainAxisAlignment.Center,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _placeholder },
        };

        _panel = new RectView
        {
            BorderSize = new BorderSizeStyle { Left = 1 },
            Children = { _contentView },
        };
        _panel.BindThemedBackgroundColor(_theme, s => s.CommitDetailsView.Background);
        _panel.BindThemedBorderColor(_theme, s => new BorderColorStyle { Left = s.CommitDetailsView.BorderLeft });
        AddChildToSelf(_panel);

        var ticker = ctx.Require<IFrameTicker>();
        _enterTween = new Tween(ticker, Transitions.ContentEnterSeconds, Easings.EaseOutCubic);
        _placeholderTween = new Tween(ticker, Transitions.PlaceholderBloomSeconds, Easings.EaseInCubic);
        // Bound before the view model (which drives the first SetRenderState) so the opacities start at
        // zero before the first show, rather than flashing fully opaque for a frame.
        this.Bind(_enterTween.LinearProgress, p => _contentView.Opacity = p);
        this.Bind(_placeholderTween.Progress, p => _placeholderHost.Opacity = p);
        this.Use(() => _enterTween);
        this.Use(() => _placeholderTween);

        this.UseViewModel(() => vm, Bind);
    }

    private void Bind(CommitDetailsViewModel vm)
    {
        if (ReferenceEquals(_vm, vm)) return;
        _vm = vm;
        this.Bind(vm.RenderState, SetRenderState);
    }

    private void SetRenderState(CommitDetailsRenderState state)
    {
        switch (state)
        {
            case CommitDetailsRenderState.Loading:
                ShowSkeleton();
                break;
            case CommitDetailsRenderState.Placeholder p:
                ShowPlaceholder(p.Text);
                break;
            case CommitDetailsRenderState.Loaded:
                ShowContent();
                break;
        }
    }

    private void ShowSkeleton()
    {
        // Stale-while-revalidate: keep the current commit's details visible while the next loads — the
        // skeleton is only for a cold load, where there's nothing to preserve.
        if (_shown is Shown.Content or Shown.Skeleton) return;

        // Rebuilt fresh each time (not cached) so its pulse starts clean: the pulse is disposed when the
        // skeleton is swapped out, and a reused view's disposed pulse would never breathe again. FadeIn
        // blooms it in (ease-in) so a fast cold load doesn't flash it.
        var skeleton = new FadeIn { Child = new CommitDetailsSkeleton(), Bloom = true }.BuildView(_ctx);
        _panel.Children.Clear();
        _panel.Children.Add(skeleton);
        _shown = Shown.Skeleton;
    }

    private void ShowPlaceholder(string text)
    {
        // Centered in the whole details panel, so it swaps in over the split container rather than
        // sitting at the top of the (content-height) header scroll column. The panels clear their own
        // contents off the same render state.
        _placeholder.Text = text;
        if (!_panel.Children.Contains(_placeholderHost))
        {
            _panel.Children.Clear();
            _panel.Children.Add(_placeholderHost);
        }
        // Bloom only when the placeholder first appears, not on a re-render while it's already up.
        if (_shown != Shown.Placeholder) _placeholderTween.Restart();
        _shown = Shown.Placeholder;
        _contentView.BottomVisible = false;
    }

    private void ShowContent()
    {
        if (!_panel.Children.Contains(_contentView))
        {
            _panel.Children.Clear();
            _panel.Children.Add(_contentView);
        }
        // Fade up only when details emerge from a placeholder/skeleton; a commit-to-commit change of
        // already-shown details swaps instantly, matching how the other panels skip the fade on refresh.
        if (_shown != Shown.Content) _enterTween.Restart();
        _shown = Shown.Content;
        _contentView.BottomVisible = true;
    }
}
