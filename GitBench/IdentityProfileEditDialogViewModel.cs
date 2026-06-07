using ZGF.Observable;

namespace GitBench;

// Backs IdentityProfileEditDialog: add a new identity profile, or edit an existing one. There is
// no git work, so the action's off-thread step is a no-op and the actual list mutation runs in
// onSuccess (the UI thread) — ObservableList is not safe to mutate from a background thread.
internal sealed class IdentityProfileEditDialogViewModel : IDisposable
{
    public State<string> DisplayName { get; }
    public State<string> AuthorName { get; }
    public State<string> AuthorEmail { get; }
    public State<string> SshKeyPath { get; }
    public State<string> MatchHost { get; }
    public State<string> MatchOwner { get; }

    public AsyncCommand Save { get; }

    public event Action? CloseRequested;

    public IdentityProfileEditDialogViewModel(
        IdentityProfile? existing,
        IdentityProfileService profiles,
        IUiDispatcher dispatcher)
    {
        var rule = existing?.Match is { Count: > 0 } m ? m[0] : null;
        DisplayName = new State<string>(existing?.DisplayName ?? string.Empty);
        AuthorName = new State<string>(existing?.UserName ?? string.Empty);
        AuthorEmail = new State<string>(existing?.UserEmail ?? string.Empty);
        SshKeyPath = new State<string>(existing?.SshKeyPath ?? string.Empty);
        MatchHost = new State<string>(rule?.Host ?? string.Empty);
        MatchOwner = new State<string>(rule?.Owner ?? string.Empty);

        var gate = new Derived<bool>(() =>
            DisplayName.Value.Trim().Length > 0
            && AuthorName.Value.Trim().Length > 0
            && AuthorEmail.Value.Trim().Length > 0);

        Save = new AsyncCommand(
            dispatcher,
            work: () => null,
            onSuccess: () =>
            {
                var host = MatchHost.Value.Trim();
                var owner = MatchOwner.Value.Trim();
                var match = host.Length > 0
                    ? new List<IdentityMatchRule> { new(host, owner.Length > 0 ? owner : null) }
                    : new List<IdentityMatchRule>();

                var sshKey = SshKeyPath.Value.Trim();
                var profile = new IdentityProfile(
                    existing?.Id ?? Guid.NewGuid(),
                    DisplayName.Value.Trim(),
                    AuthorName.Value.Trim(),
                    AuthorEmail.Value.Trim(),
                    sshKey.Length > 0 ? sshKey : null,
                    existing?.SigningKey,
                    existing?.SigningKeyFormat,
                    Match: match);

                if (existing != null) profiles.Update(profile);
                else profiles.Add(profile);
                CloseRequested?.Invoke();
            },
            gate: gate);
    }

    public void Dispose() { }
}
