using GitBench.Controls;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Markdown.Rendering;
using GitBench.Features.Operations;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Markdown;

internal sealed record MarkdownDocumentView : Widget
{
    public required Prop<MarkdownDocument?> Document { get; init; }

    public Prop<string?> TopNotice { get; init; }

    public Prop<string?> BottomNotice { get; init; }

    protected override View CreateView(Context ctx)
    {
        var pane = new VerticalScrollPane { FillParent = true, StretchContent = true };
        pane.Children.Add(new FlexItem { Grow = 1, Child = Body().BuildView(ctx) });
        pane.UseController(ctx.Require<InputSystem>(), () => new VerticalScrollPaneWheelController(pane));

        var bar = ScrollBars.CreateVertical(ctx);
        bar.IsVisible = false;
        pane.ScrollPositionChanged += _ => bar.IsVisible = pane.Scale < 1f;

        var container = new ContainerView();
        container.Children.Add(new FlexRowView
        {
            CrossAxisAlignment = CrossAxisAlignment.Stretch,
            Children =
            {
                new FlexItem { Grow = 1, Shrink = 1, Child = pane },
                bar,
            },
        });
        container.Use(() => new VerticalScrollBarSyncController(pane, bar));
        return container;
    }

    private IWidget Body() => new MarkdownDocumentBody
    {
        Document = Document,
        TopNotice = TopNotice,
        BottomNotice = BottomNotice,
    };
}

internal sealed record MarkdownDocumentBody : Widget
{
    public required Prop<MarkdownDocument?> Document { get; init; }
    public Prop<string?> TopNotice { get; init; }
    public Prop<string?> BottomNotice { get; init; }

    protected override IWidget Build(Context ctx) => new Box
    {
        Background = Theme.Color(s => s.DiffView.PanelBackground),
        Children =
        [
            new Padding
            {
                Amount = new PaddingStyle
                {
                    Left = Spacing.Xl, Right = Spacing.Xl,
                    Top = Spacing.Lg, Bottom = Spacing.Xl,
                },
                Children =
                [
                    new Column
                    {
                        CrossAxis = CrossAxisAlignment.Stretch,
                        Gap = MarkdownWidget.BlockGap,
                        Children =
                        [
                            Notice(TopNotice),
                            new Switch<MarkdownDocument?>
                            {
                                Value = Document.ToReadable(ctx),
                                Case = doc => doc is null
                                    ? Empty.Widget
                                    : new MarkdownWidget { Document = doc },
                            },
                            Notice(BottomNotice),
                        ],
                    },
                ],
            },
        ],
    };

    private static IWidget Notice(Prop<string?> text) => new Text
    {
        Value = text,
        Visible = text.Select(t => !string.IsNullOrEmpty(t)),
        FontSize = FontSize.Caption,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.DiffContent.PlaceholderText),
    };
}
