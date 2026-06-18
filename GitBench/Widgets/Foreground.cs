using ZGF.Gui;
using ZGF.Gui.Widgets;

namespace GitBench.Widgets;

/// <summary>
/// Establishes an ambient foreground (glyph/label) color for its subtree. Descendant content reads it
/// with <see cref="Color"/> — the styling mirror of <see cref="Theme.Color"/> — so the content stays
/// decoupled from whoever set the color.
/// </summary>
public sealed record Foreground : Widget
{
    public required Prop<uint> Value { get; init; }
    public required IWidget Child { get; init; }

    /// <summary>The ambient foreground color resolved from the build context; use as a Text Color.</summary>
    public static Prop<uint> Color =>
        Prop.Deferred(ctx => ctx.Require<Holder>().Value);

    protected override IWidget Build(Context ctx) => new Provide<Holder>
    {
        Value = new Holder(Value),
        Child = Child,
    };

    internal sealed record Holder(Prop<uint> Value);
}
