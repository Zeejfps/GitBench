using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Pairing;

/// <summary>What an edit the user is asked about would change, as diff lines in a block framed like
/// a reply's code block.</summary>
internal sealed record EditPreviewBlock : Widget
{
    public required IReadOnlyList<EditPreviewLine> Lines { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.Markdown.CodeBlockBackground),
        BorderSize = BorderSizeStyle.All(1),
        BorderColor = Theme.BorderColor(s => BorderColorStyle.All(s.Markdown.CodeBlockBorder)),
        BorderRadius = BorderRadiusStyle.All(Radius.Md),
        Children =
        [
            new Padding
            {
                Amount = PaddingStyle.All(Spacing.Sm),
                Children =
                [
                    new Column
                    {
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Children = [.. Lines.Select(line => (IWidget)new EditPreviewRow { Line = line })],
                    },
                ],
            },
        ],
    };
}

internal sealed record EditPreviewRow : Widget
{
    public required EditPreviewLine Line { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var loc = ctx.Localization();
        return Line switch
        {
            EditPreviewLine.File file => Label(file.Path, Theme.Color(s => s.Palette.TextSecondary), FontWeight.Bold),
            EditPreviewLine.Elided elided => Label(
                Prop.Bind<string?>(() => loc.Strings.Value.PairingApprovalMoreLines(elided.Lines)),
                Theme.Color(s => s.Palette.TextMuted),
                FontWeight.Normal),
            EditPreviewLine.Context context => Diff(" ", context.Text, default, Theme.Color(s => s.DiffContent.LineContextGlyph)),
            EditPreviewLine.Removed removed => Diff(
                "-", removed.Text, Theme.Color(s => s.DiffContent.LineRemovedBackground), Theme.Color(s => s.DiffContent.LineRemovedGlyph)),
            EditPreviewLine.Added added => Diff(
                "+", added.Text, Theme.Color(s => s.DiffContent.LineAddedBackground), Theme.Color(s => s.DiffContent.LineAddedGlyph)),
            _ => throw new ArgumentOutOfRangeException(nameof(Line), Line, "Unknown preview line."),
        };
    }

    private static IWidget Label(Prop<string?> text, Prop<uint> color, FontWeight weight) => new Padding
    {
        Amount = new PaddingStyle { Left = Spacing.Xs, Top = Spacing.Hair, Bottom = Spacing.Hair },
        Children =
        [
            new Text
            {
                Value = text,
                FontSize = FontSize.Caption,
                FontFamily = MonoFonts.Regular,
                Weight = weight,
                Wrap = TextWrap.Wrap,
                Color = color,
            },
        ],
    };

    private static IWidget Diff(string glyph, string text, Prop<uint> background, Prop<uint> glyphColor) => new Box
    {
        Background = background,
        Children =
        [
            new Row
            {
                Gap = Spacing.Xs,
                Children =
                [
                    new Text
                    {
                        Value = glyph,
                        FontSize = FontSize.Caption,
                        FontFamily = MonoFonts.Regular,
                        Color = glyphColor,
                    },
                    new Grow
                    {
                        Child = new Text
                        {
                            Value = text,
                            FontSize = FontSize.Caption,
                            FontFamily = MonoFonts.Regular,
                            Wrap = TextWrap.Wrap,
                            Color = Theme.Color(s => s.DiffContent.LineText),
                        },
                    },
                ],
            },
        ],
    };
}
