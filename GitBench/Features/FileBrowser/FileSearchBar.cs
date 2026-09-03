using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.StatusBar;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Components.TextInput;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.FileBrowser;

/// <summary>Where the find bar sits over the file: floating in the top trailing corner of the text,
/// clear of the gutter and of the line the reader is most likely on.</summary>
/// <remarks>
/// The lift is not decoration. <c>DrawZIndex</c> sums a view's own <c>ZIndex</c> with every
/// ancestor's, and the body beside this one paints its rows, washes and hunk chrome from its base z
/// upwards — so a bar left at the default 0 lays out correctly, takes the caret, and is painted over
/// by the code it is floating above.
/// </remarks>
internal sealed record FileSearchBarPlacement : Widget
{
    // Above everything the diff body reaches, and far below the assistant overlay's 400: this floats
    // within one pane, not over the window.
    private const int Layer = 100;

    public required FileSearchViewModel Model { get; init; }

    protected override IWidget Build(Context ctx) => new Padding
    {
        ZIndex = Layer,
        Amount = new PaddingStyle { Top = Spacing.Sm, Right = Spacing.Md },
        Children =
        [
            new Column
            {
                CrossAxis = CrossAxisAlignment.End,
                Children = [new FileSearchBar { Model = Model }],
            },
        ],
    };
}

/// <summary>
/// Find in file: the query, how many it found and which one you are on, the two things that change
/// what counts as a hit, and the steps between them.
/// </summary>
/// <remarks>
/// The card owns the pointer (<see cref="SurfacePointerBlocker"/>): hit-testing only sees views
/// carrying a controller, so without it a wheel over the bar would scroll the file behind it.
/// </remarks>
internal sealed record FileSearchBar : Widget
{
    private const float FieldWidth = 168f;
    private const float FieldHeight = 22f;

    // The tally is right-aligned in a fixed column, so counting up from 1/29 to 29/29 does not walk
    // the buttons beside it sideways while the reader is aiming at them.
    private const float TallyWidth = 46f;

    /// <summary>The box every control in the bar draws in, toggles and steps alike.</summary>
    public const float ControlSize = 20f;

    public const float ControlGlyphSize = 13f;

    public required FileSearchViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var inputSystem = ctx.Require<InputSystem>();

        // Built here rather than taken from DialogFrame, because a dialog's field owns its own sunken
        // surface and inside a card this small that reads as a box drawn inside a box. The card is
        // the surface; the field contributes text and a caret to it.
        var field = new TextInputView(ctx.Canvas);
        field.BindThemed(ctx.Theme(), s =>
        {
            field.BackgroundColor = 0u;
            field.TextColor = s.TextInput.Text;
            field.CaretColor = s.TextInput.Caret;
            field.SelectionRectColor = s.TextInput.Selection;
            field.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        field.Bind(ctx.Localization().Strings, s => field.PlaceholderText = s.FileSearchTitle);
        field.SetText(model.Text.Value);

        var controller = new FileSearchInputController(field, inputSystem, ctx.Get<IClipboard>())
        {
            OnEscape = model.Close,
            OnStep = backwards => { if (backwards) model.Previous(); else model.Next(); },
        };
        field.UseController(inputSystem, controller);
        field.Bind(field.TextValue, model.SetText);

        // The field takes the caret the moment the bar appears, and takes it back — with the old
        // query selected, ready to be typed over — when the shortcut is pressed again with the bar
        // already open.
        field.Use(() =>
        {
            void Focus(bool selectAll)
            {
                controller.BeginEditing();
                if (selectAll) field.SelectAll();
            }

            Focus(true);
            var subscriptions = new SubscriptionGroup();
            model.RefocusRequested += Focus;
            subscriptions.Add(() => model.RefocusRequested -= Focus);
            subscriptions.Add(controller.EndEditing);
            return subscriptions;
        });

        return new Box
        {
            Background = Theme.Color(s => s.Palette.SurfaceRaised),
            BorderRadius = BorderRadiusStyle.All(Radius.Md),
            BorderSize = BorderSizeStyle.All(1),
            BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Palette.Border)),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle
                    {
                        Left = Spacing.Md, Right = Spacing.Sm, Top = Spacing.Sm, Bottom = Spacing.Sm,
                    },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            Gap = Spacing.Xs,
                            Children =
                            [
                                new Box
                                {
                                    Width = FieldWidth,
                                    Height = FieldHeight,
                                    Children = [new Raw { View = field }],
                                },
                                new Box
                                {
                                    Width = TallyWidth,
                                    Children =
                                    [
                                        new Text
                                        {
                                            Value = Prop.Bind<string?>(() => Tally(ctx, model)),
                                            Color = Theme.Color(s => s.Palette.TextMuted),
                                            HAlign = TextAlignment.End,
                                        },
                                    ],
                                },
                                Toggle(
                                    LucideIcons.CaseSensitive, L.T(s => s.FileSearchMatchCase),
                                    model.MatchCase, Then(model, model.ToggleMatchCase)),
                                Toggle(
                                    LucideIcons.WholeWord, L.T(s => s.FileSearchWholeWord),
                                    model.WholeWord, Then(model, model.ToggleWholeWord)),
                                // Marks where the query ends and moving through its results begins.
                                new SeparatorSpacer(),
                                Step(
                                    LucideIcons.ChevronUp, L.T(s => s.FileSearchPrevious),
                                    Then(model, model.Previous)),
                                Step(
                                    LucideIcons.ChevronDown, L.T(s => s.FileSearchNext),
                                    Then(model, model.Next)),
                                Step(LucideIcons.X, L.T(s => s.FileSearchClose), model.Close),
                            ],
                        },
                    ],
                },
            ],
        }
        .WithController(inputSystem, static () => new SurfacePointerBlocker());
    }

    // Clicking any control in the bar takes the caret off the field, so every one of them hands it
    // straight back. Close is the exception: it is the one control whose whole point is that the
    // field is going away.
    private static Action Then(FileSearchViewModel model, Action run) => () =>
    {
        run();
        model.FocusField();
    };

    // Like Step, the controller is attached here rather than inside the widget: a Widget<ButtonState>
    // supplies the state and the parent chooses the input modality, so a toggle that builds its own
    // chrome and nothing else is inert until someone does this.
    private static IWidget Toggle(
        string icon, Prop<string?> tooltip, IReadable<bool> active, Action toggle) =>
        new FileSearchOptionToggle { Icon = icon, Active = active, OnToggle = toggle }
            .WithTooltip(tooltip)
            .WithController<KbmController>();

    private static IWidget Step(string icon, Prop<string?> tooltip, Action run) =>
        new StatusBarIconButton
        {
            Icon = icon,
            Command = new Command(run),
            BoxWidth = ControlSize,
            BoxHeight = ControlSize,
            IconSize = ControlGlyphSize,
        }
        .WithTooltip(tooltip)
        .WithController<KbmController>();

    /// <summary>Where the reader is in the hits, or why there is nowhere to be. Blank while nothing
    /// has been typed: a count of zero before a query is a statement about nothing.</summary>
    private static string Tally(Context ctx, FileSearchViewModel model)
    {
        var strings = ctx.Localization().Strings.Value;
        var hits = model.Hits.Value;
        if (model.Text.Value.Length == 0) return string.Empty;
        if (hits.Count == 0) return strings.FileSearchNoMatches;

        // A capped scan counted what it reached and stopped, so its total is a floor.
        var total = hits.Capped ? hits.Count + "+" : hits.Count.ToString();
        return strings.FileSearchMatches(hits.Ordinal, total);
    }
}

/// <summary>
/// One of the two switches that change what counts as a hit: accent-tinted on its own chip while on,
/// so it reads as a setting that stays rather than a button that did something.
/// </summary>
/// <remarks>
/// Deliberately the same box and glyph size as the step buttons beside it. A bare glyph next to
/// chipped ones sits at a different weight and off their baseline, which is most of what makes a row
/// of small controls look unfinished.
/// </remarks>
internal sealed record FileSearchOptionToggle : Widget<ButtonState>
{
    public required string Icon { get; init; }
    public required IReadable<bool> Active { get; init; }
    public required Action OnToggle { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(new Command(OnToggle));

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        Width = FileSearchBar.ControlSize,
        Height = FileSearchBar.ControlSize,
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        // On, it keeps the wash whether or not the pointer is over it; off, the wash is the hover.
        Background = Theme.Color(s => Active.Value || state.Hovered.Value
            ? s.Palette.SurfaceHoverStrong
            : 0u),
        Children =
        [
            new Text
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = FileSearchBar.ControlGlyphSize,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Value = Icon,
                Color = Theme.Color(s => Active.Value
                    ? s.Palette.Accent
                    : state.Hovered.Value ? s.Palette.TextPrimary : s.Palette.TextMuted),
            },
        ],
    };
}

/// <summary>
/// The find field's keys: Escape closes the bar, Enter steps to the next hit and Shift+Enter to the
/// one before it. Everything else is ordinary text editing.
/// </summary>
/// <remarks>
/// <see cref="OnFocusLostCore"/> ends the edit session, so clicking into the file behind the bar
/// stops the field intercepting keys while leaving the bar and its highlighting standing.
/// </remarks>
internal sealed class FileSearchInputController : BaseTextInputKbmController
{
    private readonly TextInputView _input;

    public Action? OnEscape { get; set; }

    /// <summary>Called with true for a backwards step.</summary>
    public Action<bool>? OnStep { get; set; }

    public FileSearchInputController(TextInputView input, InputSystem inputSystem, IClipboard? clipboard)
        : base(input, inputSystem, clipboard)
    {
        _input = input;
    }

    protected override void OnKeyboardKeyPressed(ref KeyboardKeyEvent e)
    {
        switch (e.Key)
        {
            case KeyboardKey.Escape:
                e.Consume();
                OnEscape?.Invoke();
                return;
            case KeyboardKey.Enter or KeyboardKey.NumpadEnter:
                e.Consume();
                OnStep?.Invoke((e.Modifiers & InputModifiers.Shift) != 0);
                return;
        }

        base.OnKeyboardKeyPressed(ref e);
    }

    protected override void OnFocusLostCore() => _input.StopEditing();
}
