using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

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

    protected override IWidget Build(Context ctx)
    {
        var input = ctx.Require<InputSystem>();

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
                            Children =
                            [
                                new Text
                                {
                                    Value = L.T(s => s.ReviewShortcutsTitle),
                                    FontSize = FontSize.Heading,
                                    Color = Theme.Color(s => s.Palette.TextPrimary),
                                },
                                ShortcutRow(["[", "]"], L.T(s => s.ReviewShortcutIncrementNav)),
                                ShortcutRow(["j", "k"], L.T(s => s.ReviewShortcutFileNav)),
                                ShortcutRow(["Space"], L.T(s => s.ReviewShortcutPrimary)),
                                ShortcutRow(["v"], L.T(s => s.ReviewShortcutToggleViewed)),
                                ShortcutRow(["n"], L.T(s => s.ReviewShortcutNextUnreviewed)),
                                ShortcutRow(["?"], L.T(s => s.ReviewShortcutHelp)),
                                ShortcutRow(["Esc"], L.T(s => s.ReviewShortcutClose)),
                            ],
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
                        Children = keys.Select(KeyCap).ToArray(),
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

    // A "kbd" cap: a small sunken, bordered pill around the key glyph.
    private static IWidget KeyCap(string key) => new Box
    {
        Background = Theme.Color(s => s.Palette.SurfaceSunken),
        BorderSize = BorderSizeStyle.All(1),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.BorderSubtle)),
        BorderRadius = BorderRadiusStyle.All(4f),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle { Left = Spacing.Sm, Right = Spacing.Sm, Top = 2, Bottom = 2 },
                Children =
                [
                    new Text
                    {
                        Value = key,
                        FontSize = FontSize.Caption,
                        Color = Theme.Color(s => s.Palette.TextSecondary),
                        VAlign = TextAlignment.Center,
                        HAlign = TextAlignment.Center,
                    },
                ],
            },
        ],
    };
}

// Dismisses the overlay on a click outside the card and blocks pointer input from reaching the
// surface behind the scrim (the standard modal-backdrop behavior).
internal sealed class ScrimController : KeyboardMouseController
{
    private readonly Action _onClose;

    public ScrimController(Action onClose) => _onClose = onClose;

    public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
    {
        if (e.Phase != EventPhase.Bubbling) return;
        if (e.Button == MouseButton.Left && e.State == InputState.Released) _onClose();
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
