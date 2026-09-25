using GitBench.Controls;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>A spoken turn: who said it, then the text, wrapped and selectable.</summary>
internal sealed record TranscriptMessageRow : Widget
{
    public required IReadable<string> Text { get; init; }
    public required Prop<string?> Label { get; init; }
    public required Func<ThemeStyles, uint> LabelColor { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var text = Text;

        return new Column
        {
            Gap = Spacing.Hair,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new Text
                {
                    Value = Label,
                    FontSize = FontSize.Caption,
                    Weight = FontWeight.Bold,
                    Color = Theme.Color(LabelColor),
                },
                new TranscriptBodyText
                {
                    Value = Prop.Bind(() => text.Value),
                    Color = Theme.Color(s => s.Palette.TextPrimary),
                },
            ],
        };
    }
}

/// <summary>
/// The model's turn: who answered, and the answer itself rendered as the markdown it was written in.
/// </summary>
/// <remarks>
/// The rendering is draggable like any other prose — the renderer's selection layer covers the whole
/// answer — and what that copies is what the reader sees, without the '#' and the asterisks. The copy
/// button beside the caption is the other half, alongside the code block's own copy for one block of
/// it: it takes the whole answer as markdown source, which is what pastes usefully into an editor, an
/// issue or a commit message.
/// </remarks>
internal sealed record TranscriptReplyRow : Widget
{
    public required IReadable<string> Text { get; init; }

    /// <summary>Who is speaking; the assistant unless told otherwise.</summary>
    public Prop<string?> Speaker { get; init; } = L.T(s => s.AssistantTitle);

    protected override IWidget Build(Context ctx)
    {
        var text = Text;

        return new Column
        {
            Gap = Spacing.Hair,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new TranscriptReplyHeader { GetText = () => text.Value, Speaker = Speaker },
                new TranscriptMarkdownBody { Text = text },
            ],
        };
    }
}

/// <summary>The reply's caption line: who is speaking, and the copy that takes what they said.</summary>
internal sealed record TranscriptReplyHeader : Widget
{
    public required Func<string> GetText { get; init; }

    public Prop<string?> Speaker { get; init; } = L.T(s => s.AssistantTitle);

    protected override IWidget Build(Context ctx) => new Row
    {
        CrossAxis = CrossAxisAlignment.Center,
        MainAxis = MainAxisAlignment.SpaceBetween,
        Children =
        [
            new Text
            {
                Value = Speaker,
                FontSize = FontSize.Caption,
                Weight = FontWeight.Bold,
                Color = Theme.Color(s => s.Palette.Accent),
            },
            new CopyIconButton { Label = static s => s.CommonCopy, GetText = GetText },
        ],
    };
}

/// <summary>
/// The body of a transcript entry, as text the reader can select part of and copy.
/// </summary>
/// <remarks>
/// A read-only field rather than a label: selection rendering, the clipboard and caret navigation
/// already live in the text input, and it suppresses every path back to the buffer, so the streamed
/// value it shows stays the only thing that writes it. It carries no chrome of its own — no
/// background, no caret, no placeholder — so it reads as the paragraph it replaced.
/// </remarks>
internal sealed record TranscriptBodyText : Widget
{
    private const uint TransparentBackground = 0x00000000;

    public required Prop<string> Value { get; init; }
    public required Prop<uint> Color { get; init; }

    /// <summary>Fits the body's width to its text rather than to the space it is given.</summary>
    public bool SizesToText { get; init; }

    protected override IWidget Build(Context ctx) =>
        new TextInput
        {
            ReadOnly = true,
            SizesToText = SizesToText,
            Wrap = TextWrap.Wrap,
            Value = Value,
            Background = TransparentBackground,
            FontSize = FontSize.Body,
            VAlign = TextAlignment.Start,
            Color = Color,
            SelectionColor = Theme.Color(s => s.TextInput.Selection),
        };
}

/// How a notice reads: a turn that failed, one the model declined, or something about the exchange
/// worth knowing that is neither.
internal enum TranscriptNoticeTone
{
    Error,
    Refusal,
    Advisory,
}

/// <summary>
/// A failed, declined or noteworthy turn, inline in the transcript. A turn that did not work out is
/// part of the conversation, not a modal interruption of it.
/// </summary>
internal sealed record TranscriptNoticeRow : Widget
{
    public required IReadable<string> Text { get; init; }

    public TranscriptNoticeTone Tone { get; init; } = TranscriptNoticeTone.Error;

    protected override IWidget Build(Context ctx)
    {
        var text = Text;
        var tone = Tone;
        var loc = ctx.Localization();

        return new Box
        {
            Background = Theme.Color(s => tone == TranscriptNoticeTone.Advisory
                ? s.Palette.SurfaceMuted
                : s.Status.DangerLineBg),
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Children =
            [
                new Padding
                {
                    Amount = PaddingStyle.All(Spacing.Sm),
                    Children =
                    [
                        new TranscriptBodyText
                        {
                            // A decline arrives with an optional explanation, so the sentence around
                            // it is supplied here.
                            Value = Prop.Bind(() =>
                            {
                                var body = text.Value;
                                if (tone != TranscriptNoticeTone.Refusal) return body;
                                var declined = loc.Strings.Value.AssistantRefused;
                                return body.Length == 0 ? declined : declined + " " + body;
                            }),
                            Color = Theme.Color(s => tone == TranscriptNoticeTone.Advisory
                                ? s.Status.Warning
                                : s.Status.DangerText),
                        },
                    ],
                },
            ],
        };
    }
}
