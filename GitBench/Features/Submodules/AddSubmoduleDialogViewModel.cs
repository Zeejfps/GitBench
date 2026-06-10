using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Submodules;

internal sealed class AddSubmoduleDialogViewModel : IDisposable
{
    public State<string> Url { get; } = new(string.Empty);
    public State<string> Path { get; } = new(string.Empty);
    public State<string> Branch { get; } = new(string.Empty);
    public State<bool> Force { get; } = new(false);

    /// <summary>Live refname validation for the optional track-branch field. Blank stays neutral
    /// (the field is optional); a typed-but-invalid name reports an error. See <see cref="RefNameRules"/>.</summary>
    public IReadable<FieldStatus?> BranchStatus { get; }

    public AsyncCommand Add { get; }

    public event Action? CloseRequested;

    public AddSubmoduleDialogViewModel(
        AddSubmoduleViewRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        var primaryId = request.Primary.Id;

        // Track branch is optional, so blank is valid; a non-blank name must be a legal refname.
        BranchStatus = new Derived<FieldStatus?>(() => RefNameRules.Validate(Branch.Value.Trim(), "Branch"));
        var gate = new Derived<bool>(() =>
            Url.Value.Trim().Length > 0 && Path.Value.Trim().Length > 0
            && RefNameRules.Validate(Branch.Value.Trim(), "Branch") is null);

        Add = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var url = Url.Value.Trim();
                var path = Path.Value.Trim();
                var branch = Branch.Value.Trim();
                var force = Force.Value;
                var req = new SubmoduleAddRequest(
                    Url: url,
                    Path: path,
                    Branch: branch.Length > 0 ? branch : null,
                    Force: force);
                var outcome = gitService.AddSubmodule(request.Primary, req);
                return outcome is GitOutcome.Failed failed ? failed.Message : null;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new SubmodulesChangedMessage(primaryId));
                bus.Broadcast(new WorkingTreeChangedMessage(primaryId));
                CloseRequested?.Invoke();
            },
            gate: gate);
    }

    public void Dispose() { }
}

internal readonly record struct AddSubmoduleViewRequest(Repo Primary);
