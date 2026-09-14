using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>
/// Where the reviewer asks the narrator about the step they are on, in the shape of the assistant
/// chat's composer: a field that grows with the question and the send button beside it, with a note
/// underneath while a selection in the diff will ride along. Enter sends, Shift+Enter breaks the
/// line, Escape gives the caret back; the walkthrough's Ask key hands it the caret from anywhere.
/// </summary>
internal sealed record WalkthroughAskField : Widget
{
    public const string InputId = "walkthrough-ask";
    public const string SendId = "walkthrough-ask-send";

    private const float FieldMinHeight = 0f;
    private const float FieldMaxHeight = 120f;

    public required ReviewWalkthroughStore Model { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var model = Model;
        var loc = ctx.Localization();

        var field = new GrowingDescriptionField(ctx, FieldMinHeight, FieldMaxHeight) { Id = InputId };
        field.Bind(loc.Strings, s => field.PlaceholderText = s.WalkthroughAskPlaceholder);

        var hasQuestion = new Derived<bool>(() => !string.IsNullOrWhiteSpace(field.TextValue.Value));

        void Send()
        {
            if (!hasQuestion.Value) return;
            model.Ask(field.Text.ToString());
            field.Clear();
        }

        field.OnSubmit = Send;
        field.OnEscape = field.EndEditing;
        field.Use(() =>
        {
            model.AskFocusRequested += field.BeginEditing;
            var subscriptions = new SubscriptionGroup();
            subscriptions.Add(() => model.AskFocusRequested -= field.BeginEditing);
            subscriptions.Add(field.EndEditing);
            subscriptions.Add(hasQuestion);
            return subscriptions;
        });

        return new Column
        {
            Gap = Spacing.Xs,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Row
                {
                    Gap = Spacing.Sm,
                    // End, not Center: the button stays on the last line as the field grows upward.
                    CrossAxis = CrossAxisAlignment.End,
                    Children =
                    [
                        new Grow { Child = new Raw { View = field } },
                        new ButtonWidget
                        {
                            Id = SendId,
                            Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                            ContentInset = ButtonStyle.Filled(static s => s.Palette.Accent).IconOnlyInset,
                            Command = new Command(Send, hasQuestion),
                            Children = [new ButtonIcon { Value = LucideIcons.Push }],
                        }
                        .WithTooltip(L.T(s => s.WalkthroughAskSend))
                        .WithController<KbmController>(),
                    ],
                },
                new Text
                {
                    Value = Prop.Bind<string?>(() => model.Selection.Value is { } quote
                        ? loc.Strings.Value.WalkthroughWithSelection(quote.Location)
                        : null),
                    Visible = Prop.Bind(() => model.Selection.Value != null),
                    FontSize = FontSize.Caption,
                    Overflow = TextOverflow.Ellipsis,
                    Color = Theme.Color(s => s.Palette.TextMuted),
                },
            ],
        };
    }
}
