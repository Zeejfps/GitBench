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

/// <summary>
/// Find-a-file, at the top of the tree rail: what is being looked for, how many files answered to
/// it, and the way out. Mounted only while the finder is open, so opening it is what hands the
/// caret over.
/// </summary>
/// <remarks>
/// A band across the rail rather than a card floating over it, unlike the find bar over a file: the
/// list underneath is this field's answer, not something it is standing in front of.
/// </remarks>
internal sealed record FileFinderField : Widget
{
    private const float FieldHeight = 24f;
    private const float BandHeight = 34f;
    private const float ControlSize = 20f;

    public required FileBrowserViewModel Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var browser = Model;
        var finder = browser.Finder;
        var inputSystem = ctx.Require<InputSystem>();

        var field = new TextInputView(ctx.Canvas);
        field.BindThemed(ctx.Theme(), s =>
        {
            field.BackgroundColor = 0u;
            field.TextColor = s.TextInput.Text;
            field.CaretColor = s.TextInput.Caret;
            field.SelectionRectColor = s.TextInput.Selection;
            field.PlaceholderTextColor = s.TextInput.PlaceholderText;
        });
        field.Bind(ctx.Localization().Strings, s => field.PlaceholderText = s.FileFinderTitle);
        field.SetText(finder.Text.Value);

        var controller = new FileFinderInputController(field, inputSystem, ctx.Get<IClipboard>())
        {
            OnEscape = finder.Close,
            OnActivate = browser.ActivateBestMatch,
            OnMove = browser.MoveCursor,
        };
        field.UseController(inputSystem, controller);
        field.Bind(field.TextValue, finder.SetText);

        field.Use(() =>
        {
            void Focus()
            {
                controller.BeginEditing();
                field.SelectAll();
            }

            Focus();
            var subscriptions = new SubscriptionGroup();
            finder.RefocusRequested += Focus;
            subscriptions.Add(() => finder.RefocusRequested -= Focus);
            subscriptions.Add(controller.EndEditing);
            return subscriptions;
        });

        return new Box
        {
            Height = BandHeight,
            Background = Theme.Color(s => s.Palette.Surface),
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Palette.Border }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Sm },
                    Children =
                    [
                        new Row
                        {
                            CrossAxis = CrossAxisAlignment.Center,
                            Gap = Spacing.Xs,
                            Children =
                            [
                                new Grow
                                {
                                    Child = new Box
                                    {
                                        Height = FieldHeight,
                                        Children = [new Raw { View = field }],
                                    },
                                },
                                new Text
                                {
                                    Value = Prop.Bind<string?>(() => Tally(ctx, finder)),
                                    Color = Theme.Color(s => s.Palette.TextMuted),
                                    HAlign = TextAlignment.End,
                                },
                                new StatusBarIconButton
                                {
                                    Icon = LucideIcons.X,
                                    Command = new Command(finder.Close),
                                    BoxWidth = ControlSize,
                                    BoxHeight = ControlSize,
                                }
                                .WithTooltip(L.T(s => s.FileSearchClose))
                                .WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>How many files answered, or that none did. Blank until something has been typed: a
    /// count of zero before there is a query is a statement about nothing.</summary>
    private static string Tally(Context ctx, FileFinderViewModel finder)
    {
        if (finder.Text.Value.Trim().Length == 0) return string.Empty;

        var results = finder.Results.Value;
        if (results.Paths.Count == 0) return ctx.Localization().Strings.Value.FileSearchNoMatches;

        // A ranking that stopped at the cap counted what it reached, so its total is a floor.
        return results.Truncated ? results.Paths.Count + "+" : results.Paths.Count.ToString();
    }
}

/// <summary>
/// The find field's keys: Escape closes it, the arrows walk the results underneath without the
/// caret leaving the field, and Enter opens whichever one they landed on.
/// </summary>
/// <remarks>
/// The arrows are the point of forwarding anything at all. A reader types two letters and then wants
/// the third result — reaching it by clicking, or by tabbing focus into the list, would make every
/// find a two-handed gesture.
/// </remarks>
internal sealed class FileFinderInputController : BaseTextInputKbmController
{
    private readonly TextInputView _input;

    public Action? OnEscape { get; set; }

    public Action? OnActivate { get; set; }

    /// <summary>Called with the number of rows to move by, negative for upwards.</summary>
    public Action<int>? OnMove { get; set; }

    public FileFinderInputController(TextInputView input, InputSystem inputSystem, IClipboard? clipboard)
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
                OnActivate?.Invoke();
                return;
            case KeyboardKey.DownArrow:
                e.Consume();
                OnMove?.Invoke(1);
                return;
            case KeyboardKey.UpArrow:
                e.Consume();
                OnMove?.Invoke(-1);
                return;
        }

        base.OnKeyboardKeyPressed(ref e);
    }

    protected override void OnFocusLostCore() => _input.StopEditing();
}
