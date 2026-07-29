using GitBench.Localization;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Makes the markdown under it one selectable document: the text leaves it contains answer to a
/// single <see cref="MarkdownSelectionScope"/>, and a drag runs across them and stops at this
/// surface's edges. Composed by every markdown surface, so a selection spanning two of them — two
/// assistant replies — is structurally impossible rather than checked for.
/// </summary>
internal sealed record MarkdownSelectionLayer : Widget
{
    public required IWidget Child { get; init; }

    protected override View CreateView(Context ctx)
    {
        var scope = new MarkdownSelectionScope();
        var surface = new Context(ctx);
        surface.AddService<IMarkdownSelectionScope>(scope);

        var view = Child.BuildView(surface);
        var input = ctx.Require<InputSystem>();
        view.UseController(input, () => new MarkdownSelectionController(
            scope, view, ctx, input, ctx.Get<IFrameTicker>(),
            ctx.Get<IClipboard>(), ctx.Get<ILocalizationService>()));
        return view;
    }
}
