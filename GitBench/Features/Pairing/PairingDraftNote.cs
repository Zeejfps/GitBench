using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Pairing;

/// <summary>
/// Where the agent's code for the open stop is, on the stop card: in the editor, as suggested lines
/// at the place it goes — or, for a file still to be created, here.
/// </summary>
internal sealed record PairingDraftNote : Widget
{
    public required StopDraft Draft { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        return Draft.Place switch
        {
            DraftPlace.Replace replace => Caption(Prop.Bind<string?>(() =>
                loc.Strings.Value.PairingDraftReplaces(replace.Lines.From, replace.Lines.To))),
            DraftPlace.InsertAfter insert => Caption(Prop.Bind<string?>(() =>
                loc.Strings.Value.PairingDraftInserts(insert.Line.Value))),
            DraftPlace.NewFile => new Column
            {
                Gap = Spacing.Xs,
                CrossAxis = CrossAxisAlignment.Stretch,
                Children = [Caption(L.T(s => s.PairingDraftNewFile)), Code(Draft.Code)],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(Draft), Draft.Place, "Unknown draft place."),
        };
    }

    private static IWidget Caption(Prop<string?> text) => new Text
    {
        Value = text,
        FontSize = FontSize.Caption,
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
                        Value = code,
                        FontSize = FontSize.Caption,
                        FontFamily = MonoFonts.Regular,
                        Wrap = TextWrap.Wrap,
                        Color = Theme.Color(s => s.Palette.TextBody),
                    },
                ],
            },
        ],
    };
}
