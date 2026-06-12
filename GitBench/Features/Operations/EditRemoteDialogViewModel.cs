using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Messages;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// View model for <see cref="EditRemoteDialog"/>. Owns the editable remote name + URL as
/// two-way <see cref="State{T}"/>; the dialog binds inputs straight to them with
/// <c>BindTwoWay</c>, so user edits flow back automatically and wholesale replacements
/// (the background <c>git remote get-url</c> load, or a <see cref="SetScheme"/> SSH/HTTPS
/// rewrite) are just assignments to <see cref="Url"/> that the binding reflects without a
/// typed-edit feedback loop. <see cref="Save"/> runs <c>git remote rename</c> (only when
/// the name changed) followed by <c>git remote set-url</c>.
/// </summary>
internal sealed class EditRemoteDialogViewModel : IDialogViewModel
{
    private readonly State<string> _name;
    private readonly State<string> _url;
    private string _originalUrl = string.Empty;

    public State<string> Name => _name;
    public State<string> Url => _url;
    public IReadable<RemoteUrlScheme> Scheme { get; }
    public AsyncCommand Save { get; }

    public event Action? CloseRequested;

    public EditRemoteDialogViewModel(
        EditRemoteRequest request,
        IGitService gitService,
        IUiDispatcher dispatcher,
        IMessageBus bus)
    {
        _name = new State<string>(request.RemoteName);
        _url = new State<string>(string.Empty);
        Scheme = new Derived<RemoteUrlScheme>(() => RemoteUrl.Detect(_url.Value));

        var gate = new Derived<bool>(() =>
        {
            var name = _name.Value.Trim();
            var url = _url.Value.Trim();
            if (name.Length == 0 || url.Length == 0) return false;
            return name != request.RemoteName || url != _originalUrl;
        });

        Save = AsyncCommand.ForOutcome(
            dispatcher,
            work: () =>
            {
                var outcome = request.IsAdd
                    ? gitService.AddRemote(request.Repo, _name.Value.Trim(), _url.Value.Trim())
                    : gitService.EditRemote(request.Repo, request.RemoteName, _name.Value.Trim(), _url.Value.Trim());
                return outcome;
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                CloseRequested?.Invoke();
            },
            gate: gate);

        // Add-mode has no existing remote to read a URL from; leave the field blank.
        if (!request.IsAdd)
        {
            Task.Run(() =>
            {
                var url = gitService.GetRemoteUrl(request.Repo, request.RemoteName) ?? string.Empty;
                dispatcher.Post(() =>
                {
                    _originalUrl = url;
                    _url.Value = url;
                });
            });
        }
    }

    public void SetScheme(RemoteUrlScheme scheme)
    {
        // A plain assignment — BindTwoWay reflects the rewrite into the input without the
        // edit being mistaken for user typing.
        _url.Value = RemoteUrl.Convert(_url.Value, scheme);
    }

    public void Dispose() { }
}

public readonly record struct EditRemoteRequest(Repo Repo, string RemoteName, bool IsAdd = false);
