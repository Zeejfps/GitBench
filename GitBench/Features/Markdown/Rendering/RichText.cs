using GitBench.Platform;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Widget wrapper over <see cref="RichTextView"/> — the app-code API for run-styled text, the
/// rich-text sibling of <see cref="Text"/>. <c>CreateView</c> builds the view against the
/// window's canvas, applies the props, and — when the context has an <see cref="IPlatformShell"/>
/// — attaches a <see cref="LinkController"/> so links get the hand cursor, hover recolor, and
/// click-to-open for free (no shell registered, e.g. a bare preview, simply means inert links).
/// A context inside a <see cref="MarkdownSelectionLayer"/> additionally enrols the view as one of
/// that surface's text leaves; outside one there is simply no selection.
/// Style inputs come from the theme at the call site, not from constants here.
/// </summary>
internal sealed record RichText : Widget
{
    /// <summary>The styled runs: a constant, an observable, a projection, or a compute
    /// (see <see cref="Prop{T}"/>). Assign a new list to change content.</summary>
    public Prop<IReadOnlyList<RichTextRun>> Runs { get; init; }

    /// <summary>Background of the inline-code chip behind code runs.</summary>
    public Prop<uint> CodeChipBackground { get; init; }

    /// <summary>Text color a hovered link's segments switch to.</summary>
    public Prop<uint> LinkHoverColor { get; init; }

    /// <summary>Highlight behind characters a drag has selected. Unset means no highlight, which
    /// is what text outside a selectable markdown surface wants.</summary>
    public Prop<uint> SelectionBackground { get; init; }

    protected override View CreateView(Context ctx)
    {
        var view = new RichTextView(ctx.Canvas);
        Runs.Apply(ctx, view, static (v, runs) => v.Runs = runs);
        CodeChipBackground.Apply(ctx, view, static (v, color) => v.CodeChipBackground = color);
        LinkHoverColor.Apply(ctx, view, static (v, color) => v.LinkHoverColor = color);
        SelectionBackground.Apply(ctx, view, static (v, color) => v.SelectionBackground = color);

        // Registration is what a selection is anchored to: a leaf that unmounts because its block
        // was re-parsed takes any selection ending in it with it.
        if (ctx.Get<IMarkdownSelectionScope>() is { } selection)
            view.Use(() => selection.Register(view));

        // IPlatformShell is an interface, so Get returns null (not a transient) when no shell is
        // registered — links then render styled but inert.
        if (ctx.Get<IPlatformShell>() is { } shell)
            view.UseController(ctx.Require<InputSystem>(), () => new LinkController(view, shell));

        return view;
    }
}
