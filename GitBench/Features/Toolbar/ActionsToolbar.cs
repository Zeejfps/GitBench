using GitBench.App;
using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Toolbar;

internal sealed record ActionsToolbar : Widget
{
    private const float ToolbarHeight = 44f;
    private const int HorizontalPadding = 8;
    private const float WithinClusterGap = 2f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<ActionsToolbarViewModel>();
        var styles = ctx.Theme().Styles;

        return new Box
        {
            Height = ToolbarHeight,
            Background = styles.Bind(s => s.ActionsToolbar.Background),
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            BorderColor = styles.Bind(s => new BorderColorStyle { Bottom = s.ActionsToolbar.BorderBottom }),
            Children =
            [
                new Padding
                {
                    Amount = new PaddingStyle { Left = HorizontalPadding, Right = HorizontalPadding },
                    Children =
                    [
                        new Row
                        {
                            Gap = WithinClusterGap,
                            CrossAxis = CrossAxisAlignment.Center,
                            Children =
                            [
                                new ModeSwitcherView(),
                                new SeparatorSpacer(),
                                new ButtonWidget
                                {
                                    Command = vm.Fetch,
                                    Children =
                                    [
                                        new ButtonIcon
                                        {
                                            Value = vm.IsFetching.Bind(string? (f) => f ? LucideIcons.Loader : LucideIcons.Fetch),
                                            Rotation = Prop.Bind(vm.FetchRotation),
                                        },
                                        new ButtonLabel { Value = vm.IsFetching.Bind(string? (f) => f ? "Fetching" : "Fetch") },
                                    ],
                                }.WithController<KbmController>(),
                                new ButtonWidget
                                {
                                    Command = vm.Pull,
                                    Children =
                                    [
                                        new ButtonIcon
                                        {
                                            Value = vm.IsPulling.Bind(string? (p) => p ? LucideIcons.Loader : LucideIcons.Pull),
                                            Rotation = Prop.Bind(vm.PullRotation),
                                            Badge = Prop.Bind(vm.PullBadge),
                                            BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeBehind),
                                        },
                                        new ButtonLabel { Value = vm.IsPulling.Bind(string? (p) => p ? "Pulling" : "Pull") },
                                    ],
                                }.WithController<KbmController>(),
                                new ButtonWidget
                                {
                                    Command = vm.Push,
                                    Children =
                                    [
                                        new ButtonIcon
                                        {
                                            Value = vm.IsPushing.Bind(string? (p) => p ? LucideIcons.Loader : LucideIcons.Push),
                                            Rotation = Prop.Bind(vm.PushRotation),
                                            Badge = Prop.Bind(vm.PushBadge),
                                            BadgeColor = Theme.Color(s => s.ActionsToolbar.BadgeAhead),
                                        },
                                        new ButtonLabel { Value = vm.IsPushing.Bind(string? (p) => p ? "Pushing" : "Push") },
                                    ],
                                }.WithController<KbmController>(),
                                new SeparatorSpacer(),
                                new ButtonWidget
                                {
                                    Command = vm.Stash,
                                    Children =
                                    [
                                        new ButtonIcon { Value = LucideIcons.Stash },
                                        new ButtonLabel { Value = "Stash" },
                                    ],
                                }.WithController<KbmController>(),
                                new ButtonWidget
                                {
                                    Command = vm.Branch,
                                    Children =
                                    [
                                        new ButtonIcon { Value = LucideIcons.Branch },
                                        new ButtonLabel { Value = "Branch" },
                                    ],
                                }.WithController<KbmController>(),
                                new Spacer(),
                                new ButtonWidget
                                {
                                    Command = vm.OpenFolder,
                                    ContentInset = ButtonStyle.Plain.IconOnlyInset,
                                    Children = [new ButtonIcon { Value = LucideIcons.FolderOpen }],
                                }.WithTooltip("Open in file explorer")
                                    .WithController<KbmController>(),
                                new ButtonWidget
                                {
                                    Command = vm.OpenTerminal,
                                    ContentInset = ButtonStyle.Plain.IconOnlyInset,
                                    Children = [new ButtonIcon { Value = LucideIcons.SquareTerminal }],
                                }.WithTooltip("Open in terminal")
                                    .WithController<KbmController>(),
                            ],
                        },
                    ],
                },
            ],
        }.BindVm(vm);
    }
}
