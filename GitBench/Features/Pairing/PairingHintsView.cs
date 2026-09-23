using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// What the agent has shown at the open stop above intent, on the stop card: the lines it lit up,
/// and the shape or draft it drew into the editor — the draft with Take this draft, which asks
/// once more before it types anything.
/// </summary>
internal sealed record PairingHintsView : Widget
{
    public const string TakeDraftId = "pairing-take-draft";
    public const string ConfirmDraftId = "pairing-take-draft-confirm";

    public required PairingStore Store { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        return new Switch<IReadOnlyList<PairingHint>>
        {
            Value = store.Hints,
            Case = hints => hints.Count == 0
                ? Empty.Widget
                : new Column
                {
                    Gap = Spacing.Sm,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children = [.. hints.Select(hint => Row(ctx, store, hint))],
                },
        };
    }

    private static IWidget Row(Context ctx, PairingStore store, PairingHint hint)
    {
        var loc = ctx.Localization();
        return hint switch
        {
            PairingHint.Location location => Caption(Prop.Bind<string?>(() => loc.Strings.Value.PairingHintLines(
                string.Join(", ", location.Lines.Select(l => l.From == l.To ? $"{l.From}" : $"{l.From}–{l.To}"))))),
            PairingHint.Shape shape => new Column
            {
                Gap = Spacing.Xs,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [Caption(L.T(s => s.PairingHintShape)), Code(shape.Code)],
            },
            PairingHint.Draft draft => new Column
            {
                Gap = Spacing.Xs,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [Caption(L.T(s => s.PairingHintDraft)), Code(draft.Code), new TakeDraftButton { Store = store }],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(hint), hint, "Unknown hint."),
        };
    }

    private static IWidget Caption(Prop<string?> text) => new Text
    {
        Value = text,
        FontSize = FontSize.Caption,
        Weight = FontWeight.Bold,
        Wrap = TextWrap.Wrap,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };

    private static IWidget Code(string code) => new Box
    {
        Background = Theme.Color(s => s.Palette.Surface),
        BorderRadius = BorderRadiusStyle.All(Radius.Sm),
        Children =
        [
            new Padding
            {
                Amount = PaddingStyle.All(Spacing.Sm),
                Children =
                [
                    new Text
                    {
                        Value = code.TrimEnd(),
                        FontSize = FontSize.Caption,
                        FontFamily = MonoFonts.Regular,
                        Wrap = TextWrap.Wrap,
                        Color = Theme.Color(s => s.Palette.TextBody),
                    },
                ],
            },
        ],
    };

    /// <summary>Take this draft, then Insert to confirm: the one way agent code reaches the file.</summary>
    private sealed record TakeDraftButton : Widget
    {
        public required PairingStore Store { get; init; }

        protected override IWidget Build(Context ctx)
        {
            var store = Store;
            var confirming = new State<bool>(false);
            return new Switch<bool>
            {
                Value = confirming,
                Case = asking => asking
                    ? new Row
                    {
                        Gap = Spacing.Sm,
                        CrossAxis = CrossAxisAlignment.Center,
                        Children =
                        [
                            new Text
                            {
                                Value = L.T(s => s.PairingTakeDraftConfirm),
                                FontSize = FontSize.Caption,
                                Wrap = TextWrap.Wrap,
                                Color = Theme.Color(s => s.Palette.TextBody),
                            },
                            new ButtonWidget
                            {
                                Id = ConfirmDraftId,
                                Style = ButtonStyle.Filled(static s => s.Palette.Accent),
                                Command = new Command(() =>
                                {
                                    confirming.Value = false;
                                    store.TakeDraft();
                                }),
                                Children = [new ButtonLabel { Value = L.T(s => s.PairingTakeDraftInsert) }],
                            }.WithController<KbmController>(),
                            new ButtonWidget
                            {
                                Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                Command = new Command(() => confirming.Value = false),
                                Children = [new ButtonLabel { Value = L.T(s => s.CommonCancel) }],
                            }.WithController<KbmController>(),
                        ],
                    }
                    : new Row
                    {
                        Children =
                        [
                            new ButtonWidget
                            {
                                Id = TakeDraftId,
                                Style = ButtonStyle.Outline(static s => s.Palette.TextBody),
                                Command = new Command(() => confirming.Value = true),
                                Children = [new ButtonLabel { Value = L.T(s => s.PairingTakeDraft) }],
                            }.WithController<KbmController>(),
                        ],
                    },
            };
        }
    }
}
