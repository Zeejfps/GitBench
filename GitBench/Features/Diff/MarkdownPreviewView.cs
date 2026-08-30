using GitBench.Controls;
using GitBench.Features.Markdown.Parsing;
using GitBench.Features.Operations;
using GitBench.Features.Markdown.Rendering;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.VerticalScrollBar;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Diff;

internal sealed record MarkdownPreviewView : Widget
{
    protected override View CreateView(Context ctx)
    {
        var pane = new VerticalScrollPane { FillParent = true, StretchContent = true };
        pane.Children.Add(new FlexItem { Grow = 1, Child = new MarkdownPreviewBody().BuildView(ctx) });
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
}

internal sealed record MarkdownPreviewBody : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DiffViewModel>();
        var loc = ctx.Localization();

        DiffRenderState.Markdown? Current() => vm.RenderState.Value as DiffRenderState.Markdown;

        return new Box
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
                                Notice(
                                    Prop.Bind<string?>(() => loc.Strings.Value.DiffImagePreviousVersion),
                                    Prop.Bind(() => Current() is { IsOldSide: true })),
                                new Switch<MarkdownDocument?>
                                {
                                    Value = new Derived<MarkdownDocument?>(() => Current()?.Document),
                                    Case = doc => doc is null
                                        ? Empty.Widget
                                        : new MarkdownWidget { Document = doc },
                                },
                                Notice(
                                    Prop.Bind<string?>(() =>
                                        loc.Strings.Value.DiffFileTruncated(DiffOptions.TruncationLineCap)),
                                    Prop.Bind(() => Current() is { Truncated: true })),
                            ],
                        },
                    ],
                },
            ],
        };
    }

    private static IWidget Notice(Prop<string?> text, Prop<bool> visible) => new Text
    {
        Value = text,
        Visible = visible,
        FontSize = FontSize.Caption,
        VAlign = TextAlignment.Center,
        Color = Theme.Color(s => s.DiffContent.PlaceholderText),
    };
}
