using GitBench.Infrastructure;
using ZGF.Observable;

namespace GitBench.Features.Identity;

// Backs IdentityProfileManagerDialog: a master-detail editor over the global identity profiles. The
// left list selects a profile; the right pane edits it through the shared field set. As with the
// service's other mutations there is no git work, so everything runs on the UI thread (ObservableList
// is not safe to mutate off it). Add creates a real profile immediately (with a default name) and
// selects it; edits persist on Save (offered whenever the form is named and differs from the stored
// profile) and auto-save when the selection changes, so navigating the list never silently drops them.
// Deleting always routes through an inline confirmation.
internal sealed class IdentityProfileManagerDialogViewModel : IDialogViewModel
{
    private readonly IdentityProfileService _profiles;
    private readonly string _newProfileName;

    // Bumped after a save so the Save gate re-checks dirtiness against the now-updated profile (the
    // fields don't change on save, so nothing else would prompt it to disable itself again).
    private readonly State<int> _revision = new(0);

    public ObservableList<IdentityProfile> Profiles => _profiles.Profiles;

    // The profile being edited, or null only when there are no profiles at all. Rows highlight off this.
    public State<Guid?> SelectedId { get; } = new(null);

    // The profile a row's trash button is asking to delete; drives the inline confirmation over the
    // editor pane. Null when nothing is pending.
    public State<Guid?> PendingDelete { get; } = new(null);
    public IReadable<bool> HasPendingDelete { get; }
    public IReadable<string> PendingDeleteName { get; }

    public State<string> DisplayName { get; } = new(string.Empty);
    public State<string> AuthorName { get; } = new(string.Empty);
    public State<string> AuthorEmail { get; } = new(string.Empty);
    public State<string> SshKeyPath { get; } = new(string.Empty);
    public State<string> MatchHost { get; } = new(string.Empty);
    public State<string> MatchOwner { get; } = new(string.Empty);

    public AsyncCommand Save { get; }
    public Command Add { get; }

    public event Action? CloseRequested;

    public IdentityProfileManagerDialogViewModel(
        Guid? initialProfileId,
        IdentityProfileService profiles,
        IUiDispatcher dispatcher,
        string newProfileName)
    {
        _profiles = profiles;
        _newProfileName = newProfileName;

        HasPendingDelete = new Derived<bool>(() => PendingDelete.Value != null);
        PendingDeleteName = new Derived<string>(() =>
            PendingDelete.Value is { } id && _profiles.Find(id) is { } p ? p.DisplayName : string.Empty);

        var gate = new Derived<bool>(() =>
        {
            _ = _revision.Value; // re-evaluate after a save lands
            return CurrentEditIsSavable();
        });
        Save = new AsyncCommand(dispatcher, work: () => null, onSuccess: DoSave, gate: gate);
        Add = new Command(DoAdd);

        // Open on the repo's active profile, falling back to the first profile (or nothing when the
        // list is empty — Add is then the way in).
        var initial = initialProfileId is { } wanted && _profiles.Find(wanted) != null
            ? wanted
            : _profiles.Profiles.Count > 0 ? _profiles.Profiles[0].Id : (Guid?)null;
        SelectedId.Value = initial;
        LoadFields(initial is { } id0 ? _profiles.Find(id0) : null);
    }

    public void Select(Guid id)
    {
        PendingDelete.Value = null;
        if (SelectedId.Value == id) return;
        AutoSaveCurrent();
        SelectedId.Value = id;
        LoadFields(_profiles.Find(id));
    }

    private void DoAdd()
    {
        PendingDelete.Value = null;
        AutoSaveCurrent();
        var created = new IdentityProfile(Guid.NewGuid(), _newProfileName, string.Empty, string.Empty);
        _profiles.Add(created);
        SelectedId.Value = created.Id;
        LoadFields(created);
    }

    public void RequestDelete(Guid id) => PendingDelete.Value = id;

    public void CancelDelete() => PendingDelete.Value = null;

    public void ConfirmDelete()
    {
        if (PendingDelete.Value is not { } id) return;
        PendingDelete.Value = null;
        DeleteProfile(id);
    }

    // When the deleted profile was the one being edited, the editor moves to the next profile, or
    // clears when the list empties.
    private void DeleteProfile(Guid id)
    {
        var wasEditing = SelectedId.Value == id;
        _profiles.Remove(id);
        if (!wasEditing) return;

        var next = _profiles.Profiles.Count > 0 ? _profiles.Profiles[0].Id : (Guid?)null;
        SelectedId.Value = next;
        LoadFields(next is { } n ? _profiles.Find(n) : null);
    }

    private void DoSave()
    {
        if (SelectedId.Value is { } id && _profiles.Find(id) is { } existing)
        {
            _profiles.Update(BuildFromFields(existing));
            _revision.Value++;
        }
    }

    // Persists the currently-open profile before navigating away, so simply clicking another row
    // doesn't discard edits. Skips anything not savable (unchanged, or a blank name).
    private void AutoSaveCurrent()
    {
        if (!CurrentEditIsSavable()) return;
        if (SelectedId.Value is not { } id || _profiles.Find(id) is not { } existing) return;
        _profiles.Update(BuildFromFields(existing));
        _revision.Value++;
    }

    private IdentityProfile BuildFromFields(IdentityProfile? existing) =>
        IdentityProfileEditing.Build(
            existing,
            DisplayName.Value,
            AuthorName.Value,
            AuthorEmail.Value,
            SshKeyPath.Value,
            MatchHost.Value,
            MatchOwner.Value);

    // Save is offered whenever the form has a name and differs from the stored profile — a plain dirty
    // check, so editing any field (not just the last required one) lights the button. Reads every field
    // up front so the gate subscribes to all of them; a `||` short-circuit would otherwise drop later
    // fields' subscriptions and leave Save stale. Field comparison (not record equality) because the
    // profile's Match is a List that never compares equal by reference.
    private bool CurrentEditIsSavable()
    {
        var name = DisplayName.Value.Trim();
        var author = AuthorName.Value.Trim();
        var email = AuthorEmail.Value.Trim();
        var ssh = SshKeyPath.Value.Trim();
        var host = MatchHost.Value.Trim();
        var owner = MatchOwner.Value.Trim();

        if (name.Length == 0) return false;
        if (SelectedId.Value is not { } id || _profiles.Find(id) is not { } p) return false;

        var rule = p.Match is { Count: > 0 } m ? m[0] : null;
        return name != p.DisplayName
            || author != p.UserName
            || email != p.UserEmail
            || (ssh.Length > 0 ? ssh : null) != p.SshKeyPath
            || host != (rule?.Host ?? string.Empty)
            || owner != (rule?.Owner ?? string.Empty);
    }

    private void LoadFields(IdentityProfile? p)
    {
        var rule = p?.Match is { Count: > 0 } m ? m[0] : null;
        DisplayName.Value = p?.DisplayName ?? string.Empty;
        AuthorName.Value = p?.UserName ?? string.Empty;
        AuthorEmail.Value = p?.UserEmail ?? string.Empty;
        SshKeyPath.Value = p?.SshKeyPath ?? string.Empty;
        MatchHost.Value = rule?.Host ?? string.Empty;
        MatchOwner.Value = rule?.Owner ?? string.Empty;
    }

    public void Dispose()
    {
        CloseRequested = null;
    }
}
