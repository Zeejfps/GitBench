using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;

namespace GitGui;

/// <summary>
/// Banner shown above the main content area while the repo is mid-operation
/// (merge / rebase / cherry-pick / revert / bisect / am) or has unmerged paths from a
/// stash-apply conflict. Self-managing: toggles <see cref="View.IsVisible"/> based on
/// state, so the view is skipped by layout (no residual gap) when there's nothing to show.
/// </summary>
internal sealed class OperationBannerView : MultiChildView, IBind<OperationStateBannerViewModel>
{
    private readonly TextView _text;
    private readonly ActionButton _abortButton;
    private readonly ActionButton _continueButton;
    private readonly TextView _spinnerIcon;
    private readonly FlexItem _textItem;
    private readonly FlexRowView _row;

    private RepoOperationState _currentState = RepoOperationState.None;
    private bool _isBusy;

    public OperationBannerView()
    {
        IsVisible = false;

        _text = new TextView
        {
            VerticalTextAlignment = TextAlignment.Center,
            TextWrap = TextWrap.Wrap,
        };
        _text.BindThemedTextColor(s => s.Banner.Text);

        _continueButton = new ActionButton(
            LucideIcons.ChevronsRight,
            tooltip: "Continue",
            backgroundColor: 0xFF4E8B3D);

        _abortButton = new ActionButton(
            LucideIcons.X,
            tooltip: "Abort",
            backgroundColor: 0xFFB3514B);

        _spinnerIcon = new TextView
        {
            Text = LucideIcons.Loader,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 16,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 20,
        };
        _spinnerIcon.BindThemedTextColor(s => s.Banner.Text);

        _textItem = new FlexItem { Grow = 1, Child = _text };

        _row = new FlexRowView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children = { _textItem, _abortButton },
        };

        var banner = new RectView
        {
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle
            {
                Left = 12,
                Right = 12,
                Top = 6,
                Bottom = 6,
            },
            Children = { _row },
        };
        banner.BindThemedBackgroundColor(s => s.Banner.Background);
        banner.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.Banner.Border });
        AddChildToSelf(banner);

        this.UseViewModel(this);
    }

    public void Bind(OperationStateBannerViewModel vm)
    {
        _abortButton.BindCommand(vm.Abort);
        _continueButton.BindCommand(vm.Continue);
        vm.State.Subscribe(SetState);
        vm.IsBusy.Subscribe(SetIsBusy);
        vm.BusyRotation.Subscribe(r => _spinnerIcon.Rotation = r);
    }

    private void SetState(RepoOperationState state)
    {
        _currentState = state;
        if (state == RepoOperationState.None)
        {
            _isBusy = false;
            _spinnerIcon.Rotation = 0f;
            IsVisible = false;
            return;
        }
        Render();
        IsVisible = true;
    }

    private void SetIsBusy(bool busy)
    {
        _isBusy = busy;
        if (!busy) _spinnerIcon.Rotation = 0f;
        if (_currentState != RepoOperationState.None) Render();
    }

    private void Render()
    {
        _row.Children.Clear();
        _row.Children.Add(_textItem);
        if (_isBusy)
        {
            _text.Text = BusyMessageFor(_currentState);
            _row.Children.Add(_spinnerIcon);
            return;
        }
        _text.Text = MessageFor(_currentState);
        if (SupportsContinue(_currentState)) _row.Children.Add(_continueButton);
        _row.Children.Add(_abortButton);
    }

    private static bool SupportsContinue(RepoOperationState state) => state switch
    {
        RepoOperationState.Merge => true,
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
            "Merge in progress — working directory contains unmerged files. Resolve conflicts and commit, or abort.",
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
