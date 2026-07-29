using GitBench.Controls;
using GitBench.Localization;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Components.Controls;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Assistant;

/// <summary>
/// One transcript entry, rendered for whichever kind it is. Every entry fades in as it lands, so
/// streamed output arrives rather than pops.
/// </summary>
internal sealed record TranscriptRow : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var row = ctx.Require<AssistantRow>();

        IWidget content = row.Kind switch
        {
            AssistantRowKind.User => new TranscriptMessageRow
            {
                Row = row,
                Label = L.T(s => s.AssistantYou),
                LabelColor = static s => s.Palette.TextSecondary,
            },
            AssistantRowKind.Reply => new TranscriptReplyRow { Row = row },
            AssistantRowKind.Tool => row.Group is { } group
                ? new ToolGroupRow { Group = group }
                : new ToolCallRow { Row = row },
            AssistantRowKind.Approval => new ToolApprovalCard { Row = row },
            AssistantRowKind.Refusal => new TranscriptNoticeRow { Row = row, Tone = TranscriptNoticeTone.Refusal },
            AssistantRowKind.Notice => new TranscriptNoticeRow { Row = row, Tone = TranscriptNoticeTone.Advisory },
            _ => new TranscriptNoticeRow { Row = row },
        };

        return new FadeIn { Child = content };
    }
}

/// <summary>A spoken turn: who said it, then the text, wrapped and selectable.</summary>
internal sealed record TranscriptMessageRow : Widget
{
    public required AssistantRow Row { get; init; }
    public required Prop<string?> Label { get; init; }
    public required Func<ThemeStyles, uint> LabelColor { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var row = Row;

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
                    Value = Prop.Bind(() => row.Text.Value),
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
    public required AssistantRow Row { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var row = Row;

        return new Column
        {
            Gap = Spacing.Hair,
            CrossAxis = CrossAxisAlignment.Stretch,
            Children =
            [
                new TranscriptReplyHeader { GetText = () => row.Text.Value },
                new TranscriptMarkdownBody { Text = row.Text },
            ],
        };
    }
}

/// <summary>The reply's caption line: who is speaking, and the copy that takes what they said.</summary>
internal sealed record TranscriptReplyHeader : Widget
{
    public required Func<string> GetText { get; init; }

    protected override IWidget Build(Context ctx) => new Row
    {
        CrossAxis = CrossAxisAlignment.Center,
        MainAxis = MainAxisAlignment.SpaceBetween,
        Children =
        [
            new Text
            {
                Value = L.T(s => s.AssistantTitle),
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

    protected override IWidget Build(Context ctx) =>
        new TextInput
        {
            ReadOnly = true,
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
    public required AssistantRow Row { get; init; }

    public TranscriptNoticeTone Tone { get; init; } = TranscriptNoticeTone.Error;

    protected override IWidget Build(Context ctx)
    {
        var row = Row;
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
                                var text = row.Text.Value;
                                if (tone != TranscriptNoticeTone.Refusal) return text;
                                var declined = loc.Strings.Value.AssistantRefused;
                                return text.Length == 0 ? declined : declined + " " + text;
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
