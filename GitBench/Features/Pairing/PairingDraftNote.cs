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
/// at the place it goes, with the names it uses that nothing declares yet — or, once accepted, in
/// the file for the user to change before Next.
/// </summary>
internal sealed record PairingDraftNote : Widget
{
    public const string NeedsId = "pairing-draft-needs";

    public required PairingStore Store { get; init; }
    public required StopDraft Draft { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var store = Store;
        var draft = Draft;
        return new Switch<bool>
        {
            Value = new Derived<bool>(() => store.Stop.Value?.DraftState is DraftState.Taken),
            Case = accepted => accepted
                ? Caption(L.T(s => s.PairingDraftAccepted))
                : new Column
                {
                    Gap = Spacing.Xs,
                    CrossAxis = CrossAxisAlignment.Stretch,
                    Children = [Offered(ctx, draft), Needs(ctx, store)],
                },
        };
    }

    // What the code calls that nothing declares yet, as a language server answered it.
    private static IWidget Needs(Context ctx, PairingStore store)
    {
        var loc = ctx.Localization();
        return new Text
        {
            Id = NeedsId,
            Value = Prop.Bind<string?>(() => store.DraftNeeds.Value.Count == 0
                ? null
                : loc.Strings.Value.PairingDraftNeeds(string.Join(", ", store.DraftNeeds.Value))),
            Visible = Prop.Bind(() => store.DraftNeeds.Value.Count > 0),
            FontSize = FontSize.Caption,
            FontFamily = MonoFonts.Regular,
            Wrap = TextWrap.Wrap,
            Color = Theme.Color(s => s.Status.Warning),
        };
    }

    private static IWidget Offered(Context ctx, StopDraft draft)
    {
        var loc = ctx.Localization();
        return draft.Place switch
        {
            DraftPlace.Replace replace => Caption(Prop.Bind<string?>(() =>
                loc.Strings.Value.PairingDraftReplaces(replace.Lines.From, replace.Lines.To))),
            DraftPlace.InsertAfter insert => Caption(Prop.Bind<string?>(() =>
                loc.Strings.Value.PairingDraftInserts(insert.Line.Value))),
            _ => throw new ArgumentOutOfRangeException(nameof(draft), draft.Place, "Unknown draft place."),
        };
    }

    private static IWidget Caption(Prop<string?> text) => new Text
    {
        Value = text,
        FontSize = FontSize.Caption,
        Wrap = TextWrap.Wrap,
        Color = Theme.Color(s => s.Palette.TextSecondary),
    };
}
