using GitBench.Input;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;

namespace GitBench.Features.Review;

/// <summary>
/// Modal keyboard cheatsheet for the review window: a dimmed scrim over the whole window with a
/// centered card listing the review loop's shortcuts. Shown by the <c>?</c> key (and the header's
/// help button); dismissed by clicking the scrim, pressing <c>Esc</c>, or <c>?</c> again. Layered
/// over the window content through the root <see cref="Stack"/>.
/// </summary>
internal sealed record ReviewCheatsheetOverlay : Widget
{
    private const float CardWidth = 460f;
    private const float KeyColumnWidth = 96f;

    // Draw order is global by cumulative ZIndex (View.GetDrawZIndex sums down the tree), and the
    // diff/file panes draw their content a few levels above their box. Sit the whole overlay well
    // above them — matching the app's other modals (DialogSurface = 1000) — so nothing bleeds through.
    private const int OverlayZIndex = 1000;

    public required Action OnClose { get; init; }

    /// <summary>What the surface's marks mean, so the two mark rows read "viewed" or "staged".</summary>
    public ReviewMarkKind MarkKind { get; init; } = ReviewMarkKind.Viewed;

    /// <summary>Whether this surface can host a walkthrough, so its keys are listed only where a
    /// narrator could ever drive one.</summary>
    public bool HasWalkthrough { get; init; } = true;

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var keys = ctx.KeyMap();
        var staged = MarkKind == ReviewMarkKind.Staged;

        var rows = new List<IWidget>
        {
            new Text
            {
                Value = L.T(s => s.ReviewShortcutsTitle),
                FontSize = FontSize.Heading,
                Color = Theme.Color(s => s.Palette.TextPrimary),
            },
            ShortcutRow(
                [keys.Display(KeyCommand.ReviewNextFile), keys.Display(KeyCommand.ReviewPrevFile)],
                L.T(s => s.ReviewShortcutFileNav)),
            ShortcutRow(Caps(keys, KeyCommand.ReviewToggleMark), staged
                ? L.T(s => s.ReviewShortcutToggleStaged)
                : L.T(s => s.ReviewShortcutToggleViewed)),
        };
        if (HasWalkthrough)
        {
            rows.Add(ShortcutRow(Caps(keys, KeyCommand.WalkthroughNext), L.T(s => s.ReviewShortcutWalkthroughNext)));
            rows.Add(ShortcutRow(Caps(keys, KeyCommand.WalkthroughBack), L.T(s => s.ReviewShortcutWalkthroughBack)));
            rows.Add(ShortcutRow(Caps(keys, KeyCommand.WalkthroughAsk), L.T(s => s.ReviewShortcutWalkthroughAsk)));
        }

        rows.Add(ShortcutRow(Caps(keys, KeyCommand.ReviewToggleHelp), L.T(s => s.ReviewShortcutHelp)));
        rows.Add(ShortcutRow([new KeyGesture(KeyboardKey.Escape).Display], L.T(s => s.ReviewShortcutClose)));

        var card = new Box
        {
            Width = CardWidth,
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            BorderRadius = BorderRadiusStyle.All(10f),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Lg),
                    Children =
                    [
                        new Column
                        {
                            Gap = Spacing.Md,
                            CrossAxis = CrossAxisAlignment.Stretch,
                            Children = [.. rows],
                        },
                    ],
                },
            ],
            // Clicks inside the card must not bubble to the scrim (which would dismiss the overlay).
        }.WithController(input, () => new CardController());

        return new Box
        {
            ZIndex = OverlayZIndex,
            Background = Prop.Bind<uint>(() => 0xB0000000u),
            Children = [new Center { Child = card }],
        }.WithController(input, () => new ScrimController(OnClose));
    }

    // Every distinct cap a command answers to: Enter and its numpad twin read the same.
    private static string[] Caps(IKeyMap keys, KeyCommand command) =>
        keys.GesturesFor(command).Select(g => g.Display).Distinct().ToArray();

    // One shortcut row: the key cap(s) in a fixed leading column, the description filling the rest.
    private static IWidget ShortcutRow(string[] keys, Prop<string?> description) => new Row
    {
        Gap = Spacing.Md,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Box
            {
                Width = KeyColumnWidth,
                Children =
                [
                    new Row
                    {
                        Gap = Spacing.Xs,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children = keys.Select(IWidget (key) => new KeyCap { Value = key }).ToArray(),
                    },
                ],
            },
            new Grow
            {
                Child = new Text
                {
                    Value = description,
                    FontSize = FontSize.Body,
                    Color = Theme.Color(s => s.Palette.TextSecondary),
                    VAlign = TextAlignment.Center,
                },
            },
        ],
    };
}

// Dismisses the overlay on a click outside the card and blocks pointer input from reaching the
// surface behind the scrim (the standard modal-backdrop behavior).
internal sealed class ScrimController : KeyboardMouseController
{
    private readonly Action _onClose;
    private bool _armed;

    public ScrimController(Action onClose) => _onClose = onClose;

    public override void OnMouseExit(ref MouseExitEvent e) => _armed = false;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button == MouseButton.Left)
        {
            // Dismiss on a full click (press armed on the scrim + release), so a stray release —
            // e.g. the tail of a click that started elsewhere — can't close the overlay.
            if (e.State == InputState.Pressed) _armed = true;
            else if (e.State == InputState.Released && _armed)
            {
                _armed = false;
                _onClose();
            }
        }
        e.Consume();
    }

    public override void OnMouseWheelScrolled(ref MouseWheelScrolledEvent e) => e.Consume();
    public override void OnMouseMoved(ref MouseMoveEvent e) => e.Consume();
}

// Swallows clicks landing on the card so they don't bubble up to the scrim and dismiss the overlay.
internal sealed class CardController : KeyboardMouseController
{
    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        e.Consume();
    }
}
