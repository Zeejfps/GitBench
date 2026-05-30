using ZGF.Observable;

namespace GitGui;

/// <summary>
/// View model for <see cref="EditRemoteDialog"/>. Owns the editable remote name + URL as
/// observable state; the dialog binds inputs to them and pushes user edits back through
/// <see cref="SetName"/> / <see cref="SetUrl"/>. The current URL is loaded in the
/// background on construction (a quick <c>git remote get-url</c>) and surfaced via
/// <see cref="UrlReplaced"/> so the view can fill the input without a typed-edit feedback
/// loop. <see cref="SetScheme"/> rewrites the URL between SSH/HTTPS and raises the same
/// event. <see cref="Save"/> runs <c>git remote rename</c> (only when the name changed)
/// followed by <c>git remote set-url</c>.
/// </summary>
internal sealed class EditRemoteDialogViewModel : IDisposable
{
    private readonly State<string> _name;
    private readonly State<string> _url;
    private string _originalUrl = string.Empty;

    public IReadable<string> Name => _name;
    public IReadable<string> Url => _url;
    public IReadable<RemoteUrlScheme> Scheme { get; }
    public AsyncCommand Save { get; }

    public event Action? CloseRequested;

    // Raised when the URL is replaced wholesale (initial load or a scheme switch) rather
    // than by the user typing — the view replaces the input text only in response to this,
    // so typing never fights the caret with a re-render.
    public event Action<string>? UrlReplaced;

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

        Save = new AsyncCommand(
            dispatcher,
            work: () =>
            {
                var outcome = gitService.EditRemote(
                    request.Repo, request.RemoteName, _name.Value.Trim(), _url.Value.Trim());
                return outcome.Success ? null : (outcome.ErrorMessage ?? "Failed to edit remote.");
            },
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(request.Repo.Id));
                CloseRequested?.Invoke();
            },
            gate: gate);

        Task.Run(() =>
        {
            var url = gitService.GetRemoteUrl(request.Repo, request.RemoteName) ?? string.Empty;
            dispatcher.Post(() =>
            {
                _originalUrl = url;
                _url.Value = url;
                UrlReplaced?.Invoke(url);
            });
        });
    }

    public void SetName(string value) => _name.Value = value;

    public void SetUrl(string value) => _url.Value = value;

    public void SetScheme(RemoteUrlScheme scheme)
    {
        var converted = RemoteUrl.Convert(_url.Value, scheme);
        if (converted == _url.Value) return;
        _url.Value = converted;
        UrlReplaced?.Invoke(converted);
    }

    public void Dispose() { }
}

public readonly record struct EditRemoteRequest(Repo Repo, string RemoteName);
