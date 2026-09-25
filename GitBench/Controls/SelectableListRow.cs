using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>
/// One selectable row in a manager's list: a title over a caption, highlighted through the shared
/// RowSelection tokens while selected, with a trash button (revealed on hover or while selected)
/// that deletes just this entry.
/// </summary>
internal sealed record SelectableListRow : Widget
{
    private const float RowHeight = 44f;

    public required string Title { get; init; }
    public required string Caption { get; init; }

    /// <summary>Read inside bindings, so it may read the state that decides the selection.</summary>
    public required Func<bool> IsSelected { get; init; }

    public required Action OnSelect { get; init; }
    public required Command Delete { get; init; }
    public required Prop<string?> DeleteTooltip { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var theme = ctx.Theme();
        var hovered = new State<bool>(false);
        var selected = IsSelected;

        var deleteButton = new IconButtonWidget
        {
            Icon = LucideIcons.Trash,
            IconSize = 13f,
            Width = 22,
            Height = 22,
            Command = Delete,
            Surface = st => Theme.Color(t => t.HeaderActionButton.Surface(st)),
            Foreground = st => Prop.Bind(() => selected()
                ? theme.Styles.Value.RowSelection.Text
                : theme.Styles.Value.HeaderActionButton.Icon(st)),
            Visible = Prop.Bind(() => hovered.Value || selected()),
        }
            .WithTooltip(DeleteTooltip)
            .WithController<KbmController>();

        var root = new Box
        {
            Height = RowHeight,
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Background = Prop.Bind(() =>
            {
                var rs = theme.Styles.Value.RowSelection;
                return selected() ? rs.Fill : hovered.Value ? rs.FillHover : 0u;
            }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = Spacing.Md, Right = Spacing.Xs },
                    Children =
                    [
                        new Row
                        {
                            Gap = Spacing.Xs,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new Grow
                                {
                                    Child = new Column
                                    {
                                        Gap = Spacing.Hair,
                                        MainAxis = MainAxisAlignment.Center,
                                        CrossAxis = CrossAxisAlignment.Stretch,
                                        Children =
                                        [
                                            new Text
                                            {
                                                Value = Title,
                                                Wrap = TextWrap.NoWrap,
                                                Overflow = TextOverflow.Ellipsis,
                                                Color = Prop.Bind(() => selected()
                                                    ? theme.Styles.Value.RowSelection.Text
                                                    : theme.Styles.Value.Palette.TextPrimary),
                                            },
                                            new Text
                                            {
                                                Value = Caption,
                                                FontSize = FontSize.Caption,
                                                Wrap = TextWrap.NoWrap,
                                                Overflow = TextOverflow.Ellipsis,
                                                Color = Prop.Bind(() => selected()
                                                    ? theme.Styles.Value.RowSelection.Text
                                                    : theme.Styles.Value.Palette.TextSecondary),
                                            },
                                        ],
                                    },
                                },
                                deleteButton,
                            ],
                        },
                    ],
                },
            ],
        };

        return root.WithController(
            ctx.Require<InputSystem>(),
            _ => new RowController(hovered, OnSelect));
    }

    private sealed class RowController : KeyboardMouseController
    {
        private readonly State<bool> _hovered;
        private readonly Action _onClick;

        public RowController(State<bool> hovered, Action onClick)
        {
            _hovered = hovered;
            _onClick = onClick;
        }

        public override void OnMouseEnter(ref MouseEnterEvent e) => _hovered.Value = true;

        public override void OnMouseExit(ref MouseExitEvent e) => _hovered.Value = false;

        public override void OnMouseButtonStateChanged(ref MouseButtonEvent e)
        {
            if (e.Phase != EventPhase.Bubbling || e.IsConsumed) return;
            if (e.Button != MouseButton.Left || e.State != InputState.Pressed) return;
            _onClick();
            e.Consume();
        }
    }
}
