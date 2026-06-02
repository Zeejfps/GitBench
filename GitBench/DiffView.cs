using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Diff panel shown below the file lists in Local Changes whenever exactly one file is
/// selected. Assign <see cref="Target"/> to wire the panel to an <see cref="IReadable{T}"/>
/// of <see cref="DiffTarget"/> — the VM subscribes directly and renders banners, hunk
/// separators, and per-line gutter+glyph+text rows for the resulting <see cref="DiffResult"/>.
/// </summary>
/// <remarks>
/// Rendering is virtualized — only rows intersecting the viewport are drawn (see
/// <see cref="DiffContentView"/>). The previous implementation materialized one
/// <c>RectView</c>+<c>FlexRowView</c>+4×<c>TextView</c> per line into a <c>ColumnView</c>
/// inside a <c>ScrollPane</c>, which forced O(N) text measurement on every layout pass for
/// diffs of 5000 lines.
/// </remarks>
internal sealed class DiffView : MultiChildView, IBind<DiffViewModel>
{
    // Height of the always-visible header strip. Exposed so the parent split container
    // can pin the bottom panel to exactly this height when the diff is collapsed, so the
    // chevron stays clickable even when the body is hidden.
    public const float HeaderHeight = 24f;

    private readonly State<bool> _isCollapsed = new(false);
    private readonly State<LfsBadge> _lfsState = new(LfsBadge.None);
    private readonly State<DiffSide?> _side = new(null);

    private readonly DiffContentView _content;
    private readonly bool _showOpenInWindow;

    // Header button actions, wired in Bind. Held as fields so the buttons (built in the ctor)
    // invoke whatever VM is currently bound.
    private Action? _onOpenInWindow;
    private Action? _onStageFile;
    private Action? _onUnstageFile;

    public DiffView(bool showOpenInWindow = true)
    {
        _showOpenInWindow = showOpenInWindow;
        _content = new DiffContentView();
        var vScrollBar = ScrollBars.CreateVertical();
        var hScrollBar = ScrollBars.CreateHorizontal();

        var body = new BorderLayoutView
        {
            Center = _content,
            East = vScrollBar,
            South = hScrollBar,
        };

        // Outer layout: header always on top, body in the center. When collapsed, we
        // null out Center so the body's hScrollBar isn't laid out — otherwise its
        // South=hScrollBar measures at its natural height and draws over the header
        // (body draws after header in z-order, since it's added second).
        var outerLayout = new BorderLayoutView
        {
            North = BuildHeaderBar(),
            Center = body,
        };
        _isCollapsed.Subscribe(c => outerLayout.Center = c ? null : body);

        var panel = new RectView { Children = { outerLayout } };
        panel.BindThemedBackgroundColor(s => s.DiffView.PanelBackground);
        AddChildToSelf(panel);

        this.UseBehavior(_ => new ScrollSyncController(_content, vScrollBar, hScrollBar));
    }

    public IReadable<bool> IsCollapsed => _isCollapsed;

    public void Bind(DiffViewModel vm)
    {
        vm.RenderState.Subscribe(_content.SetRenderState);
        vm.LfsStatus.Subscribe(s => _lfsState.Value = s);
        vm.CurrentSide.Subscribe(s => _side.Value = s);
        _content.OnStageHunk = vm.StageHunk;
        _content.OnUnstageHunk = vm.UnstageHunk;
        _content.OnDiscardHunk = vm.RequestDiscardHunk;
        _onOpenInWindow = vm.RequestOpenInWindow;
        _onStageFile = vm.StageFile;
        _onUnstageFile = vm.UnstageFile;
    }

    private View BuildHeaderBar()
    {
        var hovered = new State<bool>(false);

        var title = new TextView
        {
            Text = "Diff View",
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        title.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var chevron = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16f,
        };
        chevron.BindText(_isCollapsed, c => c ? LucideIcons.ChevronUp : LucideIcons.ChevronDown);
        chevron.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var bar = new RectView
        {
            Height = HeaderHeight,
            BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
            Padding = new PaddingStyle { Left = 8, Right = 6 },
            Children =
            {
                new FlexRowView
                {
                    Gap = 6f,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        new FlexItem { Grow = 1, Child = title },
                        BuildStageFileButton(),
                        BuildOpenInWindowButton(),
                        BuildLfsBadge(),
                        chevron,
                    },
                },
            },
        };
        bar.BindThemedBackgroundColor(s =>
            hovered.Value ? s.DiffView.HeaderBackgroundHover : s.DiffView.HeaderBackgroundIdle);
        bar.BindThemedBorderColor(s => new BorderColorStyle
        {
            Top = s.DiffView.HeaderBorderTop,
            Bottom = s.DiffView.HeaderBorderBottom,
        });

        // Bubble phase only: the bar wraps interactive child buttons (stage / open-in-window).
        // Registering on capture (the default) would let the bar consume their clicks before
        // they reach the buttons — bubble lets a button consume first, and clicks on the bar's
        // empty area still bubble up here to toggle collapse.
        bar.UseController(_ => new HoverableButtonController(
            () => _isCollapsed.Value = !_isCollapsed.Value,
            h => hovered.Value = h), EventPhaseFilter.Bubble);

        return bar;
    }

    // File-level Stage/Unstage action shown in the diff header — the review-workflow shortcut
    // ("looks good → stage the file"). It tracks the loaded diff's side: "Stage" for unstaged
    // changes, "Unstage" for staged, and hides entirely for commit-side (history) diffs, which
    // aren't stageable.
    private View BuildStageFileButton()
    {
        var hovered = new State<bool>(false);

        var label = new TextView
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        label.BindText(_side, s => s == DiffSide.Staged ? "Unstage" : "Stage");
        label.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var btn = new RectView { Children = { label } };
        btn.BindIsVisible(_side, s => s is DiffSide.Unstaged or DiffSide.Staged);
        btn.UseController(_ => new HoverableButtonController(
            () =>
            {
                if (_side.Value == DiffSide.Staged) _onUnstageFile?.Invoke();
                else _onStageFile?.Invoke();
            },
            h => hovered.Value = h));
        return btn;
    }

    // Pops the current diff into its own top-level window. Hidden on the DiffView instance that
    // already lives inside such a window (re-popping is pointless).
    private View BuildOpenInWindowButton()
    {
        var hovered = new State<bool>(false);

        var icon = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            Text = LucideIcons.ExternalLink,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16f,
        };
        icon.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var btn = new RectView { Children = { icon } };
        btn.IsVisible = _showOpenInWindow;
        btn.UseController(_ => new HoverableButtonController(
            () => _onOpenInWindow?.Invoke(),
            h => hovered.Value = h));
        return btn;
    }

    // Small pill in the header that reports a binary file's storage. It only surfaces for
    // binary files (the VM yields None otherwise) so the user can tell at a glance whether a
    // blob lives in Git LFS or is committed inline. Colors/text are bound to _lfsState, which
    // mirrors the VM's LfsStatus; selectors re-read it on each repaint, the same way the
    // header title re-reads its hover state.
    private View BuildLfsBadge()
    {
        var label = new TextView
        {
            FontSize = 10f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        label.BindText(_lfsState, s => s switch
        {
            LfsBadge.Tracked => "Git LFS",
            LfsBadge.NotTracked => "Not in LFS",
            _ => string.Empty,
        });
        label.BindThemedTextColor(s => _lfsState.Value == LfsBadge.Tracked
            ? s.DiffView.LfsBadgeTrackedText
            : s.DiffView.LfsBadgeUntrackedText);

        var badge = new RectView
        {
            Height = 16f,
            BorderRadius = BorderRadiusStyle.All(8),
            Padding = new PaddingStyle { Left = 7, Right = 7 },
            Children = { label },
        };
        badge.BindThemedBackgroundColor(s => _lfsState.Value == LfsBadge.Tracked
            ? s.DiffView.LfsBadgeTrackedBackground
            : s.DiffView.LfsBadgeUntrackedBackground);
        badge.BindIsVisible(_lfsState, s => s != LfsBadge.None);
        return badge;
    }
}
