using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
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
/// There is deliberately no rule along the bottom. The strip is a plane of its own and the active
/// tab wears the colour of what is underneath it, so the boundary is a change of colour everywhere
/// except across the active tab — which is what makes that tab read as the surface below rather
/// than as a chip sitting on top of it. A rule would cut straight through the one place the join
/// has to be invisible.
/// </para>
/// </remarks>
internal sealed record TabStrip : Widget
{
    public const float Height = 32f;

    /// <summary>
    /// The tabs, in order. A single <see cref="Each{T}"/> is as valid here as a fixed set, and a
    /// control that belongs beside them — the terminal's <c>+</c> — is just the last entry: it
    /// travels with the tabs rather than being pinned to an edge away from them.
    /// </summary>
    public required IWidget[] Tabs { get; init; }

    /// <summary>
    /// A control on the leading edge, outside the scroller — the file browser's back and forward.
    /// </summary>
    /// <remarks>
    /// Outside it because it is not one of the tabs: it stays put while they pan under it, and a
    /// history button that scrolled away with the twentieth tab would be a history button you have
    /// to go looking for. Anything that belongs <em>with</em> the tabs — the terminal's <c>+</c> —
    /// goes in <see cref="Tabs"/> instead.
    /// </remarks>
    public IWidget? Leading { get; init; }

    public Prop<uint> Background { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var children = new List<IWidget>();
        if (Leading is { } leading) children.Add(leading);
        children.Add(new Grow
        {
            Child = new Padding
            {
                // A hair of lead-in, so the first tab is not welded to whatever the pane's leading
                // edge happens to be.
                Amount = new PaddingStyle { Left = Spacing.Xs },
                Children =
                [
                    // Reuses the Actions toolbar's scrollbar-less horizontal scroller: once the tabs
                    // overflow the strip it clips them and the wheel — the vertical wheel included —
                    // pans it sideways.
                    new HorizontalScrollArea
                    {
                        VerticalWheelPans = true,
                        Child = new Row
                        {
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children = Tabs,
                        },
                    },
                ],
            },
        });

        return new Box
        {
            Height = Height,
            Background = Background,
            Children =
            [
                new Row
                {
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children = children.ToArray(),
                },
            ],
        };
    }
}

/// <summary>
/// One tab: a label that ellipsizes when long, an optional leading mark, an optional close button,
/// and — when it is the active one — the colour of the surface below it under an accent bar.
/// </summary>
/// <remarks>
/// The active tab is not a highlighted chip. It wears <see cref="ContentBackground"/>, the colour of
/// whatever the strip sits over, so it reads as a notch cut out of the strip onto the surface below;
/// the accent bar along its top is what makes that legible when the two planes are only a few values
/// apart, as they are in this theme. A saturated fill was what this had first, and it put a second
/// row of the mode switcher's own selected-segment colour directly beneath the mode switcher.
/// </remarks>
internal sealed record TabChrome : Widget
{
    // Tabs shrink to their content, capped here: a longer name ellipsizes, a shorter one stays snug.
    private const float MaxTabWidth = 220f;

    // Reserved on every tab, painted only on the active one, so activating a tab never moves its
    // label — the same trick the working-changes underline tabs use for their rule.
    private const int ActiveBarHeight = 2;

    public required Prop<string?> Label { get; init; }
    public required Func<bool> IsActive { get; init; }
    public required Action OnActivate { get; init; }

    /// <summary>The label's font family. Unset leaves it in the UI font; the file browser hands it
    /// the italic face for a tab the reader is only previewing.</summary>
    public Prop<string> LabelFontFamily { get; init; }

    /// <summary>
    /// The background of the surface this strip sits over — the grid for the terminal, the details
    /// panel for the commit strip. The active tab wears it; that is the whole of the "this tab is
    /// what you are looking at" signal, so it is the caller's to supply rather than something the
    /// control could guess.
    /// </summary>
    public required Func<ThemeStyles, uint> ContentBackground { get; init; }

    /// <summary>Closing this tab. Null for a tab that is never closable — which also withdraws the
    /// middle click, so the gesture never half-works.</summary>
    public Action? OnClose { get; init; }

    /// <summary>
    /// Opening this tab's own menu, given the point that asked for it. Null for a tab with nothing to
    /// offer, which leaves the right click to whatever is underneath.
    /// </summary>
    /// <remarks>
    /// A callback rather than a list of items, so this control never learns what a menu is: what a
    /// tab can do belongs to the surface whose tabs these are, and both strips would otherwise be
    /// carrying the other's vocabulary.
    /// </remarks>
    public Action<PointF>? OnContextMenu { get; init; }

    /// <summary>
    /// An optional widget before the label. The caller's, not this control's: the commit strip's
    /// Viewed check means something only there, and a shared pill that knew what it meant would be
    /// carrying one surface's vocabulary for every other.
    /// </summary>
    public IWidget? Leading { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var hover = new State<bool>(false);

        // The close button is laid out on every tab and painted on the one being looked at, so a
        // strip of four does not carry four X's — and so that hovering a tab does not resize it,
        // which is what hiding the button outright would do.
        bool Closable() => IsActive() || hover.Value;

        // The label grows so it ellipsizes into whatever width the (capped) tab leaves it. A flex
        // container measures its intrinsic width from children's *unclamped* natural widths but lays
        // them out clamped, so capping the tab with MaxWidth alone would size the pill to the full name
        // yet clamp the label, leaving dead space. With the label in a Grow, the cap on the pill flows
        // down: a long name shrinks the Grow slot and ellipsizes; a short name leaves the pill snug.
        var label = new Text
        {
            Value = Label,
            FontFamily = LabelFontFamily,
            FontSize = FontSize.Body,
            VAlign = TextAlignment.Center,
            Overflow = TextOverflow.Ellipsis,
            Color = Theme.Color(s => IsActive() ? s.Palette.TextPrimary : s.Palette.TextSecondary),
        };

        var rowChildren = new List<IWidget>();
        if (Leading is { } leading) rowChildren.Add(leading);
        rowChildren.Add(new Grow { Child = label });
        if (OnClose is { } close) rowChildren.Add(CloseButton(close, Closable));

        var pill = new Box
        {
            MaxWidth = MaxTabWidth,
            BorderSize = new BorderSizeStyle { Top = ActiveBarHeight },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle
            {
                Top = IsActive() ? s.Palette.Accent : 0u,
            }),
            Background = Theme.Color(s =>
            {
                if (IsActive()) return ContentBackground(s);
                return hover.Value ? s.Palette.SurfaceHover : 0u;
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Lg, Right = Spacing.Md },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children = rowChildren.ToArray(),
                        },
                    ],
                },
            ],
        };

        return pill.WithController(input, () => new TabClickController(hover, OnActivate, OnClose, OnContextMenu));
    }

    // Transparent rather than hidden when the tab is neither active nor hovered: an unpainted button
    // still holds its place, so tabs keep their width as the pointer crosses them. It is only
    // reachable while it is painted anyway — the pointer has to be on the tab to get to it.
    private static IWidget CloseButton(Action onClose, Func<bool> shown) => new ButtonWidget
    {
        Style = ButtonStyle.Bare(state => Theme.Color(t =>
            !shown() ? 0u
            : state.Hovered.Value ? t.Palette.TextPrimary
            : t.Palette.TextMuted)),
        Command = new Command(onClose),
        Children = [new ButtonIcon { Value = LucideIcons.X, FontSize = FontSize.Caption }],
    }.WithTooltip(L.T(s => s.CommonClose)).WithController<KbmController>();
}

// Hover tracking + left-click activation for a tab pill, plus middle-click to close (closable tabs
// only) and right-click for the tab's own menu. The close button consumes its own press first
// (bubbling), so pressing it closes the tab without also arming it here. Activation fires on release,
// but only when the press armed on this tab with the same button.
internal sealed class TabClickController : KeyboardMouseController
{
    private readonly State<bool> _hover;
    private readonly Action _onClick;
    private readonly Action? _onClose;
    private readonly Action<PointF>? _onContextMenu;
    private MouseButton? _armed;

    public TabClickController(State<bool> hover, Action onClick, Action? onClose, Action<PointF>? onContextMenu = null)
    {
        _hover = hover;
        _onClick = onClick;
        _onClose = onClose;
        _onContextMenu = onContextMenu;
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

        // On the press, like every other menu in the app — and without activating the tab, since
        // asking a tab what it can do is not asking to look at it.
        if (e.Button == MouseButton.Right)
        {
            if (_onContextMenu == null || e.State != InputState.Pressed) return;

            _onContextMenu(e.Mouse.Point);
            e.Consume();
            return;
        }

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
