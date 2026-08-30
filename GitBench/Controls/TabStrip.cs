using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A row of tabs across the top of a region: the tabs themselves in a scroller that pans once they
/// overflow, and an optional trailing control pinned outside it.
/// </summary>
/// <remarks>
/// Shared by the commit-details strip and the terminal's, because everything a tab strip has to get
/// right — the width cap and the ellipsis, the close button that does not also activate, the
/// middle-click, the fills, the overflow pan — is the same decision on both surfaces and a second
/// copy is the one that silently stops matching. What each caller supplies is what its tabs are
/// bound to and what closing one means.
/// <para>
/// The trailing slot sits outside the scroller so a control that acts on the strip as a whole (the
/// terminal's <c>+</c>) stays reachable however far the tabs have overflowed.
/// </para>
/// </remarks>
internal sealed record TabStrip : Widget
{
    public const float Height = 32f;

    /// <summary>The tabs, in order. A single <see cref="Each{T}"/> is as valid here as a fixed set.</summary>
    public required IWidget[] Tabs { get; init; }

    public Prop<uint> Background { get; init; }

    /// <summary>Pinned to the trailing edge, outside the scroller. Null for a strip with no such control.</summary>
    public IWidget? Trailing { get; init; }

    protected override IWidget Build(Context ctx)
    {
        // Reuses the Actions toolbar's scrollbar-less horizontal scroller: once the tabs overflow
        // the strip it clips them and the wheel — the vertical wheel included — pans it sideways.
        IWidget scroller = new Grow
        {
            Child = new HorizontalScrollArea
            {
                VerticalWheelPans = true,
                Child = new Row
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children = Tabs,
                },
            },
        };

        return new Box
        {
            Height = Height,
            Background = Background,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
            Children =
            [
                new Row
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children = Trailing is { } trailing ? [scroller, trailing] : [scroller],
                },
            ],
        };
    }
}

/// <summary>
/// One tab pill: a label that ellipsizes when long, an optional leading mark, an optional close
/// button, the row-selection fill when active and the hover fill on hover.
/// </summary>
internal sealed record TabChrome : Widget
{
    // Tabs shrink to their content, capped here: a longer name ellipsizes, a shorter one stays snug.
    private const float MaxTabWidth = 220f;

    public required Prop<string?> Label { get; init; }
    public required Func<bool> IsActive { get; init; }
    public required Action OnActivate { get; init; }

    /// <summary>Closing this tab. Null for a tab that is never closable — which also withdraws the
    /// middle click, so the gesture never half-works.</summary>
    public Action? OnClose { get; init; }

    /// <summary>
    /// Whether the close button is offered right now, for a tab whose answer changes over its life.
    /// A tab is built once and outlives the siblings it was built beside, so a strip whose last tab
    /// must not offer an X cannot decide it by whether <see cref="OnClose"/> was supplied.
    /// </summary>
    public Prop<bool> ShowClose { get; init; } = true;

    /// <summary>
    /// An optional widget before the label. The caller's, not this control's: the commit strip's
    /// Viewed check means something only there, and a shared pill that knew what it meant would be
    /// carrying one surface's vocabulary for every other.
    /// </summary>
    public IWidget? Leading { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();
        var hover = new State<bool>(false);

        // The label grows so it ellipsizes into whatever width the (capped) tab leaves it. A flex
        // container measures its intrinsic width from children's *unclamped* natural widths but lays
        // them out clamped, so capping the tab with MaxWidth alone would size the pill to the full name
        // yet clamp the label, leaving dead space. With the label in a Grow, the cap on the pill flows
        // down: a long name shrinks the Grow slot and ellipsizes; a short name leaves the pill snug.
        var label = new Text
        {
            Value = Label,
            FontSize = FontSize.Body,
            VAlign = TextAlignment.Center,
            Overflow = TextOverflow.Ellipsis,
            Color = Prop.Bind(() => IsActive()
                ? theme.Styles.Value.Palette.TextPrimary
                : theme.Styles.Value.Palette.TextSecondary),
        };

        var rowChildren = new List<IWidget>();
        if (Leading is { } leading) rowChildren.Add(leading);
        rowChildren.Add(new Grow { Child = label });
        if (OnClose is { } close) rowChildren.Add(CloseButton(close, ShowClose));

        var pill = new Box
        {
            MaxWidth = MaxTabWidth,
            // A trailing 1px divider between adjacent tabs (and after the last one).
            BorderSize = new BorderSizeStyle { Right = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Right = s.Palette.Border }),
            Background = Prop.Bind(() =>
            {
                var sel = theme.Styles.Value.RowSelection;
                if (IsActive()) return sel.Fill;
                return hover.Value ? sel.FillHover : 0u;
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Sm,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = rowChildren.ToArray(),
                        },
                    ],
                },
            ],
        };

        return pill.WithController(input, () => new TabClickController(hover, OnActivate, OnClose));
    }

    private static IWidget CloseButton(Action onClose, Prop<bool> visible) => new ButtonWidget
    {
        Visible = visible,
        Style = ButtonStyle.Bare(s => Theme.Color(t => t.Palette.TextMuted)),
        Command = new Command(onClose),
        Children = [new ButtonIcon { Value = LucideIcons.X, FontSize = FontSize.Caption }],
    }.WithTooltip(L.T(s => s.CommonClose)).WithController<KbmController>();
}

// Hover tracking + left-click activation for a tab pill, plus middle-click to close (closable tabs
// only). The close button consumes its own press first (bubbling), so pressing it closes the tab
// without also arming it here. Activation fires on release, but only when the press armed on this
// tab with the same button.
internal sealed class TabClickController : KeyboardMouseController
{
    private readonly State<bool> _hover;
    private readonly Action _onClick;
    private readonly Action? _onClose;
    private MouseButton? _armed;

    public TabClickController(State<bool> hover, Action onClick, Action? onClose)
    {
        _hover = hover;
        _onClick = onClick;
        _onClose = onClose;
    }

    public override void OnMouseEnter(ref MouseEnterEvent e) => _hover.Value = true;

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        _hover.Value = false;
        _armed = null;
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button != MouseButton.Left && (e.Button != MouseButton.Middle || _onClose == null)) return;

        if (e.State == InputState.Pressed)
        {
            _armed = e.Button;
            e.Consume();
            return;
        }

        if (e.State != InputState.Released || _armed != e.Button) return;
        _armed = null;
        if (e.Button == MouseButton.Left) _onClick();
        else _onClose!();
        e.Consume();
    }
}
