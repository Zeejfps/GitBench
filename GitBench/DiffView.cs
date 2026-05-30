using ZGF.Gui;
using ZGF.Gui.Bindings;
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

    private readonly DiffContentView _content;

    public DiffView()
    {
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
        _content.OnStageHunk = vm.StageHunk;
        _content.OnUnstageHunk = vm.UnstageHunk;
        _content.OnDiscardHunk = vm.RequestDiscardHunk;
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
                    Gap = 4f,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        new FlexItem { Grow = 1, Child = title },
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

        bar.UseController(_ => new HoverableButtonController(
            () => _isCollapsed.Value = !_isCollapsed.Value,
            h => hovered.Value = h));

        return bar;
    }
}
