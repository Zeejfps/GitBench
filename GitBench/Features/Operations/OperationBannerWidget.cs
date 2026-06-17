using GitBench.Controls;
using GitBench.Git;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;

namespace GitBench.Features.Operations;

/// <summary>
/// Banner shown above the main content while the repo is mid-operation (merge / rebase /
/// cherry-pick / revert / bisect / am) or has unmerged paths from a stash-apply conflict.
/// Offers Continue where the operation supports it, and Abort.
/// </summary>
internal sealed record OperationBannerWidget : Widget
{
    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<OperationBannerViewModel>();

        return new Show
        {
            When = vm.IsActive,
            Then = () => Banner(vm),
        }.BindVm(vm);
    }

    private static IWidget Banner(OperationBannerViewModel vm) => new Box
    {
        Background = Theme.Color(s => s.Banner.Background),
        BorderColor = Theme.BorderColor(s => new BorderColorStyle { Bottom = s.Banner.Border }),
        BorderSize = new BorderSizeStyle { Bottom = 1 },
        Padding = new PaddingStyle { Left = 12, Right = 12, Top = 6, Bottom = 6 },
        Children =
        [
            new Row
            {
                Gap = 4,
                CrossAxis = CrossAxisAlignment.Center,
                Children =
                [
                    new Grow
                    {
                        Child = new Text
                        {
                            Value = Prop.Bind<string?>(() => vm.IsBusy.Value
                                ? BusyMessageFor(vm.State.Value)
                                : MessageFor(vm.State.Value)),
                            VAlign = TextAlignment.Center,
                            Wrap = TextWrap.Wrap,
                            Color = Theme.Color(s => s.Banner.Text),
                        },
                    },
                    new Show
                    {
                        When = vm.IsBusy,
                        Then = () => Spinner(vm),
                        Else = () => Actions(vm),
                    },
                ],
            },
        ],
    };

    // Spinner existence is gated on the memoized IsBusy bool, so it survives every busy frame;
    // the per-frame rotation flows in as a Prop, updating the live view without rebuilding it.
    private static IWidget Spinner(OperationBannerViewModel vm) => new Text
    {
        Value = LucideIcons.Loader,
        FontFamily = LucideIcons.FontFamily,
        FontSize = 16,
        VAlign = TextAlignment.Center,
        HAlign = TextAlignment.Center,
        Width = 20,
        Color = Theme.Color(s => s.Banner.Text),
        Rotation = Prop.Bind(vm.BusyRotation),
    };

    private static IWidget Actions(OperationBannerViewModel vm) => new Row
    {
        Gap = 4,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new ActionButton
            {
                Icon = LucideIcons.ChevronsRight,
                Tooltip = "Continue",
                Background = 0xFF4E8B3D,
                Command = vm.Continue,
                Visible = Prop.Bind(() => SupportsContinue(vm.State.Value)),
            },
            new ActionButton
            {
                Icon = LucideIcons.X,
                Tooltip = "Abort",
                Background = 0xFFB3514B,
                Command = vm.Abort,
            },
        ],
    };

    private static bool SupportsContinue(RepoOperationState state) => state switch
    {
        // Merge finishes through the commit box (commit creates the merge commit), so no
        // Continue button — unlike rebase/cherry-pick/revert, which advance via --continue.
        RepoOperationState.Rebase => true,
        RepoOperationState.CherryPick => true,
        RepoOperationState.Revert => true,
        RepoOperationState.ApplyMailbox => true,
        _ => false,
    };

    private static string BusyMessageFor(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => "Continuing merge…",
        RepoOperationState.Rebase => "Continuing rebase…",
        RepoOperationState.CherryPick => "Continuing cherry-pick…",
        RepoOperationState.Revert => "Continuing revert…",
        RepoOperationState.ApplyMailbox => "Continuing patch apply…",
        _ => "Working…",
    };

    private static string MessageFor(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge =>
            "Merge in progress — resolve conflicts, then commit to finish the merge, or abort.",
        RepoOperationState.Rebase =>
            "Rebase in progress — working directory contains unmerged files. Resolve conflicts and continue, or abort.",
        RepoOperationState.CherryPick =>
            "Cherry-pick in progress — working directory contains unmerged files. Resolve conflicts and commit, or abort.",
        RepoOperationState.Revert =>
            "Revert in progress — working directory contains unmerged files. Resolve conflicts and commit, or abort.",
        RepoOperationState.ApplyMailbox =>
            "Patch apply in progress — working directory contains unmerged files. Resolve conflicts and continue, or abort.",
        RepoOperationState.Bisect =>
            "Bisect in progress. Use the terminal to mark commits good/bad, or reset.",
        RepoOperationState.UnmergedPaths =>
            "Working directory contains unresolved conflicts. Resolve them and stage the files to clear this state, or reset.",
        _ => string.Empty,
    };
}
