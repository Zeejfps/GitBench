using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Theming;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Shown when a pull fails because the local branch and its upstream have diverged and git
/// refuses to pick a reconcile strategy on its own ("Need to specify how to reconcile divergent
/// branches"). Lets the user choose merge / rebase / fast-forward-only and reruns the pull with
/// that flag, so the divergence is resolved in-app rather than dropping the raw git hint.
/// </summary>
internal sealed class ReconcilePullDialog : ContainerView, IBind<ReconcilePullDialogViewModel>
{
    private readonly Action _onClose;
    private readonly CheckboxView _mergeMode;
    private readonly CheckboxView _rebaseMode;
    private readonly CheckboxView _ffOnlyMode;
    private readonly DialogShell _shell;

    public ReconcilePullDialog(Repo repo, Action onClose)
    {
        _onClose = onClose;

        var prompt = new TextView(CompatUi.Canvas)
        {
            Text = $"'{repo.DisplayName}' and its upstream have both moved on. " +
                   "Choose how to reconcile them, then pull.",
            TextWrap = TextWrap.Wrap,
        };
        prompt.BindThemedTextColor(s => s.DialogBody.BodyText);

        var modeLabel = DialogFrame.Label("Strategy");

        _mergeMode = new CheckboxView("Merge (--no-rebase)") { Height = 22 };
        _rebaseMode = new CheckboxView("Rebase (--rebase)") { Height = 22 };
        _ffOnlyMode = new CheckboxView("Fast-forward only (--ff-only)") { Height = 22 };

        var hint = DialogFrame.Hint(
            "Merge or rebase may stop on conflicts — the Operation banner will offer Abort. " +
            "Fast-forward only refuses unless your branch hasn't moved.",
            TextWrap.Wrap);

        _shell = new DialogShell("Reconcile divergent branches", onClose)
        {
            BodyGap = 10,
            Action = ("Pull", DialogButtonRole.Primary),
            Body =
            {
                prompt,
                modeLabel,
                _mergeMode,
                _rebaseMode,
                _ffOnlyMode,
                hint,
            },
        };
        AddChildToSelf(_shell.View);

        _shell.AttachConfirmKeys(this);

        this.UseViewModel(
            ctx => new ReconcilePullDialogViewModel(
                repo,
                ctx.Require<IGitService>(),
                ctx.Require<IUiDispatcher>(),
                ctx.Require<IMessageBus>()),
            Bind);
    }

    public void Bind(ReconcilePullDialogViewModel vm)
    {
        vm.CloseRequested += _onClose;

        _shell.BindCommand(vm.Pull, vm.Error);

        vm.Strategy.Subscribe(m =>
        {
            _mergeMode.IsChecked.Value = m == PullStrategy.Merge;
            _rebaseMode.IsChecked.Value = m == PullStrategy.Rebase;
            _ffOnlyMode.IsChecked.Value = m == PullStrategy.FastForwardOnly;
        });
        _mergeMode.IsChecked.Changed += b => SelectStrategy(vm, PullStrategy.Merge, b);
        _rebaseMode.IsChecked.Changed += b => SelectStrategy(vm, PullStrategy.Rebase, b);
        _ffOnlyMode.IsChecked.Changed += b => SelectStrategy(vm, PullStrategy.FastForwardOnly, b);
    }

    private void SelectStrategy(ReconcilePullDialogViewModel vm, PullStrategy strategy, bool isCheckedNow)
    {
        if (!isCheckedNow)
        {
            if (vm.Strategy.Value == strategy)
                CheckboxFor(strategy).IsChecked.Value = true;
            return;
        }
        if (vm.Strategy.Value == strategy) return;
        vm.Strategy.Value = strategy;
    }

    private CheckboxView CheckboxFor(PullStrategy strategy) => strategy switch
    {
        PullStrategy.Rebase => _rebaseMode,
        PullStrategy.FastForwardOnly => _ffOnlyMode,
        _ => _mergeMode,
    };
}
