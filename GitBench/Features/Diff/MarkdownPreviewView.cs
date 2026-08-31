using GitBench.Features.Markdown;
using GitBench.Features.Markdown.Parsing;
using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Diff;

internal sealed record MarkdownPreviewView : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var props = MarkdownPreviewProps.From(ctx);
        return new MarkdownDocumentView
        {
            Document = props.Document,
            TopNotice = props.TopNotice,
            BottomNotice = props.BottomNotice,
        };
    }
}

internal sealed record MarkdownPreviewBody : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var props = MarkdownPreviewProps.From(ctx);
        return new MarkdownDocumentBody
        {
            Document = props.Document,
            TopNotice = props.TopNotice,
            BottomNotice = props.BottomNotice,
        };
    }
}

file sealed record MarkdownPreviewProps(
    Prop<MarkdownDocument?> Document, Prop<string?> TopNotice, Prop<string?> BottomNotice)
{
    public static MarkdownPreviewProps From(Context ctx)
    {
        var vm = ctx.Require<DiffViewModel>();
        var loc = ctx.Localization();

        DiffRenderState.Markdown? Current() => vm.RenderState.Value as DiffRenderState.Markdown;

        return new MarkdownPreviewProps(
            Prop.Bind<MarkdownDocument?>(() => Current()?.Document),
            Prop.Bind<string?>(() =>
                Current() is { IsOldSide: true } ? loc.Strings.Value.DiffImagePreviousVersion : null),
            Prop.Bind<string?>(() =>
                Current() is { Truncated: true }
                    ? loc.Strings.Value.DiffFileTruncated(DiffOptions.TruncationLineCap)
                    : null));
    }
}
