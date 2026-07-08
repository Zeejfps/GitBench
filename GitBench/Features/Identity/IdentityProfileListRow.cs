using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Identity;

// One selectable row in the profile manager's left list: the profile's display name over its author
// email, highlighted through the shared RowSelection tokens when it is the active edit target, with a
// trash button (revealed on hover or while selected) that deletes just this profile.
internal sealed record IdentityProfileListRow : Widget
{
    private const float RowHeight = 44f;

    protected override IWidget Build(Context ctx)
    {
        var profile = ctx.Require<IdentityProfile>();
        var vm = ctx.Require<IdentityProfileManagerDialogViewModel>();
        var theme = ctx.Theme();
        var hovered = new State<bool>(false);

        bool Selected() => vm.SelectedId.Value == profile.Id;

        var deleteButton = new IconButtonWidget
        {
            Icon = LucideIcons.Trash,
            IconSize = 13f,
            Width = 22,
            Height = 22,
            Command = new Command(() => vm.RequestDelete(profile.Id)),
            Surface = st => Theme.Color(t => t.HeaderActionButton.Surface(st)),
            Foreground = st => Prop.Bind(() => Selected()
                ? theme.Styles.Value.RowSelection.Text
                : theme.Styles.Value.HeaderActionButton.Icon(st)),
            Visible = Prop.Bind(() => hovered.Value || Selected()),
        }
            .WithTooltip(L.T(s => s.IdentityManageDelete))
            .WithController<KbmController>();

        var root = new Box
        {
            Height = RowHeight,
            BorderRadius = BorderRadiusStyle.All(Radius.Sm),
            Background = Prop.Bind(() =>
            {
                var rs = theme.Styles.Value.RowSelection;
                return Selected() ? rs.Fill : hovered.Value ? rs.FillHover : 0u;
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
                                        CrossAxis = CrossAxisAlignment.Start,
                                        Children =
                                        [
                                            new Text
                                            {
                                                Value = profile.DisplayName,
                                                Overflow = TextOverflow.Ellipsis,
                                                Color = Prop.Bind(() => Selected()
                                                    ? theme.Styles.Value.RowSelection.Text
                                                    : theme.Styles.Value.Palette.TextPrimary),
                                            },
                                            new Text
                                            {
                                                Value = profile.UserEmail,
                                                FontSize = FontSize.Caption,
                                                Overflow = TextOverflow.Ellipsis,
                                                Color = Prop.Bind(() => Selected()
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
            _ => new RowController(hovered, () => vm.Select(profile.Id)));
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
