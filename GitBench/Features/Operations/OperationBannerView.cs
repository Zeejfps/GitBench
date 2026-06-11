using GitBench.Controls;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Banner shown above the main content area while the repo is mid-operation
/// (merge / rebase / cherry-pick / revert / bisect / am) or has unmerged paths from a
/// stash-apply conflict. Self-managing: toggles <see cref="View.IsVisible"/> based on
/// state, so the view is skipped by layout (no residual gap) when there's nothing to show.
/// </summary>
internal sealed record OperationBannerView : Widget
{
    protected override View CreateView(Context ctx)
    {
        var vm = new OperationStateBannerViewModel(
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IFrameTicker>(),
            ctx.Require<IMessageBus>());

        var theme = ctx.Theme();

        var text = new TextView(ctx.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        text.BindTextColor(() => theme.Styles.Value.Banner.Text);

        var continueButton = new ActionButton
        {
            Icon = LucideIcons.ChevronsRight,
            Tooltip = "Continue",
            Background = 0xFF4E8B3D,
            Command = vm.Continue,
        }.BuildView(ctx);

        var abortButton = new ActionButton
        {
            Icon = LucideIcons.X,
            Tooltip = "Abort",
            Background = 0xFFB3514B,
            Command = vm.Abort,
        }.BuildView(ctx);

        var spinnerIcon = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.Loader,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 16,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 20,
        };
        spinnerIcon.BindTextColor(() => theme.Styles.Value.Banner.Text);

        var textItem = new FlexItem { Grow = 1, Child = text };

        var row = new FlexRowView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { textItem, abortButton },
        };

        var banner = new RectView
        {
            IsVisible = false,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle
            {
                Left = 12,
                Right = 12,
                Top = 6,
                Bottom = 6,
            },
            Children = { row },
        };
        banner.BindBackgroundColor(() => theme.Styles.Value.Banner.Background);
        banner.BindBorderColor(() => new BorderColorStyle { Bottom = theme.Styles.Value.Banner.Border });

        var currentState = RepoOperationState.None;
        var isBusy = false;

        void Render()
        {
            row.Children.Clear();
            row.Children.Add(textItem);
            if (isBusy)
            {
                text.Text = BusyMessageFor(currentState);
                row.Children.Add(spinnerIcon);
                return;
            }
            text.Text = MessageFor(currentState);
            if (SupportsContinue(currentState)) row.Children.Add(continueButton);
            row.Children.Add(abortButton);
        }

        banner.Bind(vm.State, state =>
        {
            currentState = state;
            if (state == RepoOperationState.None)
            {
                isBusy = false;
                spinnerIcon.Rotation = 0f;
                banner.IsVisible = false;
                return;
            }
            Render();
            banner.IsVisible = true;
        });

        banner.Bind(vm.IsBusy, busy =>
        {
            isBusy = busy;
            if (!busy) spinnerIcon.Rotation = 0f;
            if (currentState != RepoOperationState.None) Render();
        });

        banner.Bind(vm.BusyRotation, r => spinnerIcon.Rotation = r);

        banner.UseViewModel(() => vm, _ => { });
        return banner;
    }

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
