using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.KeyboardModule;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A row of tabs across the top of a region: the tabs themselves in a scroller that pans once they
/// overflow, with optional controls pinned outside it on either edge.
/// </summary>
/// <remarks>
/// Shared by the commit-details strip and the terminal's, because everything a tab strip has to get
/// right — the width cap and the ellipsis, the close button that does not also activate, the
/// middle-click, the fills, the overflow pan — is the same decision on both surfaces and a second
/// copy is the one that silently stops matching. What each caller supplies is what its tabs are
/// bound to and what closing one means — not the plane the strip is drawn on, which is the theme's
/// so that the fill an unselected tab wears can be derived a fixed step behind it.
/// <para>
/// The rule along the bottom is the strip's own and is drawn under the tabs, so the active tab
/// repaints the pixel it covers in the colour of the surface below. That break is what makes the
/// tab read as the surface below rather than as a chip sitting on top of it — an unbroken rule,
/// which is what a border on the surface's own header would be, cuts through the one place the join
/// has to be invisible.
/// </para>
/// </remarks>
internal sealed record TabStrip : Widget
{
    public const float StripHeight = 32f;

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

    /// <summary>
    /// A control on the trailing edge, outside the scroller — the content panel's "new terminal".
    /// </summary>
    /// <remarks>
    /// Pinned for the opposite reason to <see cref="Leading"/>: it makes something the strip does not
    /// yet have. A control that panned away with the twentieth tab would be one you have to scroll
    /// past the tabs to reach in order to add another.
    /// </remarks>
    public IWidget? Trailing { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var children = new List<IWidget>();
        if (Leading is { } leading)
        {
            children.Add(leading);
            children.Add(Divider);
        }

        children.Add(new Grow
        {
            Child = new Padding
            {
                // A hair of lead-in, so the first tab is not welded to whatever the pane's leading
                // edge happens to be. None of it behind a leading slot: there the divider is the
                // edge, and a gap either side of it strands it between two things it is joining.
                Amount = new PaddingStyle { Left = Leading is null ? Spacing.Xs : 0 },
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

        if (Trailing is { } trailing)
        {
            children.Add(Divider);
            children.Add(trailing);
        }

        return new Box
        {
            Height = StripHeight,
            Background = Theme.Color(static s => s.TabStrip.Background),
            Children =
            [
                new Stack
                {
                    Children =
                    [
                        Rule,
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children = children.ToArray(),
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>
    /// The join with the surface below, drawn under the tabs so each of them repaints the pixel it
    /// covers: the line runs the width of the strip and breaks at exactly the active tab, which
    /// paints that pixel in the colour of the surface the line separates.
    /// </summary>
    /// <remarks>
    /// A layer rather than a border on the surface's own header, because a border there is one
    /// unbroken line the tab above has no way to cut — and the break is the whole point.
    /// </remarks>
    private static IWidget Rule => new Box
    {
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        BorderColor = Theme.BorderColor(static s => new BorderColorStyle { Bottom = s.TabStrip.Separator }),
    };

    /// <summary>Between the leading slot and the tabs, so the history buttons read as their own
    /// control rather than as the run's first tab.</summary>
    private static IWidget Divider => new Box
    {
        Width = 1,
        Background = Theme.Color(static s => s.TabStrip.Separator),
    };
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
/// row of the working-changes layout switcher's own selected-segment colour directly beneath it.
/// <para>
/// The others are the strip, with a hairline on the trailing edge and the strip's rule under them.
/// Giving them a fill a step behind it was tried and read as the run being recessed rather than as
/// the active one being lifted out of it.
/// </para>
/// </remarks>
internal sealed record TabChrome : Widget
{
    // Tabs shrink to their content, capped here: a longer name ellipsizes, a shorter one stays snug.
    private const float MaxTabWidth = 220f;

    // Reserved on every tab, painted only on the active one, so activating a tab never moves its
    // label — the same trick the working-changes underline tabs use for their rule.
    private const int ActiveBarHeight = 2;

    // On the trailing edge of every tab, the last one included: the line after it reads as the end
    // of the run rather than as a tab that lost its divider.
    private const int SeparatorWidth = 1;

    // The strip's own rule along the join runs under the tabs, so every tab repaints that pixel:
    // the others in the rule's colour, the active one in the colour of the surface below.
    private const int JoinHeight = 1;

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

    /// <summary>
    /// How this tab is dragged to a new place in its run. Null for a strip whose order is not the
    /// reader's to change — the commit details' file tabs, whose order is the diff's.
    /// </summary>
    public ITabDrag? Drag { get; init; }

    /// <summary>
    /// An optional widget after the label, before the close button — the file strip's unsaved dot.
    /// </summary>
    /// <remarks>
    /// Its own slot rather than sharing the leading one, so a tab says what it is and what state it
    /// is in at the same time instead of trading one for the other. Laid out whether or not it
    /// paints, for the same reason the close button is: a mark that appeared would resize the tab.
    /// </remarks>
    public IWidget? Mark { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var hover = new State<bool>(false);

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
        if (Mark is { } mark) rowChildren.Add(mark);
        if (OnClose is { } close) rowChildren.Add(CloseButton(close));

        uint Fill(ThemeStyles s) =>
            IsActive() ? ContentBackground(s)
            : hover.Value ? s.TabStrip.InactiveHoverBackground
            : s.TabStrip.Background;

        var pill = new Box
        {
            MaxWidth = MaxTabWidth,
            BorderSize = new BorderSizeStyle
            {
                Top = ActiveBarHeight,
                Right = SeparatorWidth,
                Bottom = JoinHeight,
            },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle
            {
                // The bar's band is the tab's own fill when it is not the active one: a border the
                // background does not paint under, so left unset it would notch the strip's colour
                // into the top of every other tab.
                Top = IsActive() ? s.Palette.Accent : Fill(s),
                Right = s.TabStrip.Separator,
                // The active tab carries the join across itself in the colour of the surface below,
                // which is the break that reads as the tab being that surface.
                Bottom = IsActive() ? ContentBackground(s) : s.TabStrip.Separator,
            }),
            Background = Theme.Color(Fill),
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

        var isActive = IsActive;
        return pill
            .WithController(input, view =>
                new TabClickController(hover, OnActivate, OnClose, OnContextMenu, Drag, view, input))
            .Use(view => new RevealWhenActive(view, isActive));
    }

    // Painted on every closable tab rather than only the one being looked at. The space is reserved
    // either way — a button that appeared under the pointer would resize the tab — so hiding it only
    // made the strip harder to read for nothing: what closes was something you had to hover to find
    // out.
    private static IWidget CloseButton(Action onClose) => new ButtonWidget
    {
        Style = ButtonStyle.Bare(state => Theme.Color(t =>
            state.Hovered.Value ? t.Palette.TextPrimary : t.Palette.TextMuted)),
        Command = new Command(onClose),
        Children = [new ButtonIcon { Value = LucideIcons.X, FontSize = FontSize.Caption }],
    }.WithTooltip(L.T(s => s.CommonClose)).WithController<KbmController>();
}

/// <summary>
/// Scrolls the strip to a tab whenever it becomes the active one, so a tab opened past the right
/// edge of an overflowing strip is not left out of sight.
/// </summary>
internal sealed class RevealWhenActive : IDisposable
{
    private readonly Derived<bool> _active;
    private readonly IDisposable _subscription;

    public RevealWhenActive(View tab, Func<bool> isActive)
    {
        var scroller = tab.GetParentOfType<HorizontalScrollView>();
        _active = new Derived<bool>(isActive);
        _subscription = _active.Subscribe(active =>
        {
            if (active) scroller?.Reveal(tab);
        });
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _active.Dispose();
    }
}

// Hover tracking + left-click activation for a tab pill, plus middle-click to close (closable tabs
// only) and right-click for the tab's own menu. The close button consumes its own press first
// (bubbling), so pressing it closes the tab without also arming it here. Activation fires on release,
// but only when the press armed on this tab with the same button — and only when the press did not
// turn into a drag, since dragging a tab somewhere is not asking to look at it.
internal sealed class TabClickController : KeyboardMouseController, IDisposable
{
    // Far enough that a click with a shaky hand is still a click, and near enough that a drag feels
    // like it started when the pointer moved. The repo bar's rows use the same number.
    private const float DragThresholdSq = 6f * 6f;

    private readonly State<bool> _hover;
    private readonly Action _onClick;
    private readonly Action? _onClose;
    private readonly Action<PointF>? _onContextMenu;
    private readonly ITabDrag? _drag;
    private readonly View? _view;
    private readonly InputSystem? _input;

    private MouseButton? _armed;
    private bool _dragging;
    private PointF _pressed;

    public TabClickController(
        State<bool> hover,
        Action onClick,
        Action? onClose,
        Action<PointF>? onContextMenu = null,
        ITabDrag? drag = null,
        View? view = null,
        InputSystem? input = null)
    {
        _hover = hover;
        _onClick = onClick;
        _onClose = onClose;
        _onContextMenu = onContextMenu;
        _drag = drag;
        _view = view;
        _input = input;

        // The strip resolves a drop from where the tabs actually ended up, so each of them has to
        // say which view it is.
        if (_drag is not null && _view is not null) _drag.Register(_view);
    }

    public void Dispose()
    {
        if (_drag is null || _view is null) return;
        _drag.Unregister(_view);
    }

    public override void OnMouseEnter(ref MouseEnterEvent e)
    {
        if (_dragging) return;
        _hover.Value = true;
    }

    public override void OnMouseExit(ref MouseExitEvent e)
    {
        // A drag is meant to leave the tab: the pointer is out over the strip looking for a slot,
        // and forgetting the press there would drop the tab the moment it started moving.
        if (_dragging) return;
        _hover.Value = false;
        _armed = null;
    }

    public override void OnMouseMoved(ref MouseMoveEvent e)
    {
        if (_drag is null || _armed != MouseButton.Left) return;

        if (!_dragging)
        {
            var dx = e.Mouse.Point.X - _pressed.X;
            var dy = e.Mouse.Point.Y - _pressed.Y;
            if (dx * dx + dy * dy < DragThresholdSq) return;

            _dragging = true;
            _hover.Value = false;
            // The keyboard is taken only once the drag is real. A plain click on a tab has to leave
            // it wherever it was — in the terminal, in the editor — and taking it on every press
            // would pull it out from under the reader for a gesture they did not make.
            _input?.StealFocus(this);
            _drag.Start(e.Mouse.Point);
            e.Consume();
            return;
        }

        _drag.Update(e.Mouse.Point);
        e.Consume();
    }

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;

        // On the press, like every other menu in the app — and without activating the tab, since
        // asking a tab what it can do is not asking to look at it.
        if (e.Button == MouseButton.Right)
        {
            if (_onContextMenu == null || e.State != InputState.Pressed || _dragging) return;

            _onContextMenu(e.Mouse.Point);
            e.Consume();
            return;
        }

        if (e.Button != MouseButton.Left && (e.Button != MouseButton.Middle || _onClose == null)) return;

        if (e.State == InputState.Pressed)
        {
            _armed = e.Button;
            _dragging = false;
            _pressed = e.Mouse.Point;
            e.Consume();
            return;
        }

        if (e.State != InputState.Released || _armed != e.Button) return;
        _armed = null;

        if (_dragging)
        {
            _dragging = false;
            _drag?.Complete();
            _input?.Blur(this);
        }
        else if (e.Button == MouseButton.Left) _onClick();
        else _onClose!();

        e.Consume();
    }

    public override void OnKeyboardKeyStateChanged(ref KeyboardKeyEvent e)
    {
        if (!_dragging || e.State != InputState.Pressed || e.Key != KeyboardKey.Escape) return;

        Abandon();
        e.Consume();
    }

    public override void OnFocusLost()
    {
        if (_dragging) Abandon();
        _armed = null;
    }

    private void Abandon()
    {
        _dragging = false;
        _armed = null;
        _drag?.Cancel();
        _input?.Blur(this);
    }
}
