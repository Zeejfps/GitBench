using ZGF.Fonts;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// A path the way an editor's header shows one: the folder dimmed and given up first, from its
/// start, so the file name stays whole for as long as there is room for it. <see cref="Name"/> may
/// carry more than the name (a line, a symbol) — it is cut from its end only once the folder is gone.
/// </summary>
public sealed record PathText : Widget
{
    /// <summary>Everything up to and including the last separator.</summary>
    public required Prop<string?> Directory { get; init; }
    public required Prop<string?> Name { get; init; }
    public Prop<uint> DirectoryColor { get; init; }
    public Prop<uint> NameColor { get; init; }
    public Prop<float> FontSize { get; init; }
    public Prop<string> FontFamily { get; init; }

    /// <summary>Shown on hover — the whole path, where the label may have lost some of it.</summary>
    public Prop<string?> Tooltip { get; init; }

    public static (string Directory, string Name) Split(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? (string.Empty, path) : (path[..(slash + 1)], path[(slash + 1)..]);
    }

    protected override View CreateView(Context ctx)
    {
        var view = new PathTextView(
            new Text
            {
                Value = Directory,
                Color = DirectoryColor,
                FontSize = FontSize,
                FontFamily = FontFamily,
                Overflow = TextOverflow.EllipsisStart,
                BaseDir = BidiDirection.Ltr,
                VAlign = TextAlignment.Center,
            }.BuildView(ctx),
            new Text
            {
                Value = Name,
                Color = NameColor,
                FontSize = FontSize,
                FontFamily = FontFamily,
                Overflow = TextOverflow.Ellipsis,
                BaseDir = BidiDirection.Ltr,
                VAlign = TextAlignment.Center,
            }.BuildView(ctx));

        if (Tooltip.IsSet)
        {
            var hovered = new State<bool>(false);
            view.UseController(ctx.Require<InputSystem>(), () => new HoverController(hovered));
            view.Use(() => new Tooltip(view, ctx, Tooltip.ToReadable(ctx), hovered));
        }
        return view;
    }

    private sealed class HoverController(State<bool> hovered) : KeyboardMouseController
    {
        public override void OnMouseEnter(ref MouseEnterEvent e) => hovered.Value = true;

        public override void OnMouseExit(ref MouseExitEvent e) => hovered.Value = false;
    }

    // The name takes what it needs first; the folder gets what is left. Left to right even in an RTL
    // layout: a path reads one way.
    private sealed class PathTextView : View
    {
        private readonly View _directory;
        private readonly View _name;

        public PathTextView(View directory, View name)
        {
            _directory = directory;
            _name = name;
            AddChildToSelf(directory);
            AddChildToSelf(name);
        }

        protected override float MeasureWidthIntrinsic() =>
            Width.IsSet ? Width : _directory.MeasureWidth() + _name.MeasureWidth();

        protected override float MeasureHeightIntrinsic(float availableWidth) =>
            Height.IsSet ? Height : MathF.Max(_directory.MeasureHeight(), _name.MeasureHeight());

        protected override void OnLayoutChildren()
        {
            var pos = Position;
            var nameWidth = MathF.Min(_name.MeasureWidth(), pos.Width);
            var directoryWidth = MathF.Max(0f, MathF.Min(_directory.MeasureWidth(), pos.Width - nameWidth));
            Place(_directory, pos.Left, directoryWidth);
            Place(_name, pos.Left + directoryWidth, nameWidth);
        }

        private void Place(View child, float left, float width)
        {
            child.LeftConstraint = left;
            child.BottomConstraint = Position.Bottom;
            child.WidthConstraint = width;
            child.HeightConstraint = Position.Height;
            child.LayoutSelf();
        }
    }
}
