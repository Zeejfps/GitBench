using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Branches;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Infrastructure;
using GitBench.Localization;
using GitBench.Messages;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Operations;

/// <summary>
/// Modal shown when the user picks "Edit origin…" (or any remote) on a remote section
/// header. Mirrors Fork's "Remote" dialog: an editable remote name + repository URL, with
/// a scheme dropdown that rewrites the URL between SSH and HTTPS. Runs
/// <c>git remote rename</c> (when the name changed) then <c>git remote set-url</c>.
/// Leaving <see cref="RemoteName"/> null selects add-mode: seeds the remote name with the
/// conventional "origin" and runs <c>git remote add</c> on save instead of rename/set-url.
/// </summary>
internal sealed record EditRemoteDialog : Widget
{
    public required Repo Repo { get; init; }
    public string? RemoteName { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var repo = Repo;
        var existingName = RemoteName;
        var onClose = OnClose;
        var gitService = ctx.Require<IGitRemoteOperations>();
        var dispatcher = ctx.Require<IUiDispatcher>();
        var bus = ctx.Require<IMessageBus>();
        var isAdd = existingName is null;

        // Two-way states: user edits flow back automatically, and wholesale replacements (the
        // background `git remote get-url` load, or an SSH/HTTPS rewrite) are plain assignments
        // the binding reflects without a typed-edit feedback loop.
        var name = new State<string>(existingName ?? "origin");
        var url = new State<string>(string.Empty);
        var originalUrl = string.Empty;
        var scheme = new Derived<RemoteUrlScheme>(() => RemoteUrl.Detect(url.Value));

        var gate = new Derived<bool>(() =>
        {
            var newName = name.Value.Trim();
            var newUrl = url.Value.Trim();
            if (newName.Length == 0 || newUrl.Length == 0) return false;
            return newName != existingName || newUrl != originalUrl;
        });

        var save = AsyncCommand.ForOutcome(
            dispatcher,
            work: () => existingName is { } current
                ? gitService.EditRemote(repo, current, name.Value.Trim(), url.Value.Trim())
                : gitService.AddRemote(repo, name.Value.Trim(), url.Value.Trim()),
            onSuccess: () =>
            {
                bus.Broadcast(new RefsChangedMessage(repo.Id));
                onClose();
            },
            gate: gate);

        // Add-mode has no existing remote to read a URL from; leave the field blank.
        if (existingName is { } remoteToLoad)
        {
            Task.Run(() =>
            {
                var loaded = gitService.GetRemoteUrl(repo, remoteToLoad) ?? string.Empty;
                dispatcher.Post(() =>
                {
                    originalUrl = loaded;
                    url.Value = loaded;
                });
            });
        }

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            Title = isAdd ? s.OperationsRemoteTitleAdd : s.OperationsRemoteTitleEdit,
            OnClose = onClose,
            Width = DialogFrame.WidthWide,
            Action = (isAdd ? s.CommonAdd : s.CommonSave, DialogButtonRole.Primary),
            Command = save,
            Body =
            [
                new Text
                {
                    Value = isAdd ? s.OperationsRemoteDescAdd : s.OperationsRemoteDescEdit,
                    Color = Theme.Color(t => t.DialogBody.BodyText),
                },
                new LabeledInput
                {
                    Label = s.OperationsRemoteNameLabel,
                    Value = name,
                },
                new LabeledInput
                {
                    Label = s.CommonRepositoryUrl,
                    Value = url,
                    Accessory = new SchemeDropdown
                    {
                        Scheme = scheme,
                        OnSelect = picked => url.Value = RemoteUrl.Convert(url.Value, picked),
                    },
                },
            ],
        };
    }
}

/// <summary>
/// Compact SSH/HTTPS toggle shown to the right of the repository URL input. Mirrors
/// <see cref="RemoteDropdown"/>: a bordered button that pops a <see cref="RepoBarContextMenu"/>
/// with the two scheme choices and raises <see cref="SchemeSelected"/> on pick.
/// </summary>
internal sealed record SchemeDropdown : Widget
{
    /// <summary>Read source for the current scheme (derived from the URL).</summary>
    public required IReadable<RemoteUrlScheme> Scheme { get; init; }

    /// <summary>Invoked when the user picks a scheme; the owner rewrites the URL.</summary>
    public required Action<RemoteUrlScheme> OnSelect { get; init; }

    protected override IWidget Build(Context ctx) => new DropdownWidget
    {
        Width = 84,
        Height = Sizes.ControlHeight,
        Gap = Spacing.Xs,
        Children =
        [
            new Grow
            {
                Child = new Text
                {
                    VAlign = TextAlignment.Center,
                    Value = Prop.Bind<string?>(() => LabelFor(Scheme.Value)),
                    Color = Theme.Color(s => s.DialogFrame.TitleText),
                },
            },
        ],
    }.WithMenuController(rect => RepoBarContextMenu.Show(ctx, rect.BottomLeft, BuildItems()));

    private IReadOnlyList<RepoBarContextMenu.Item> BuildItems() =>
    [
        new(LabelFor(RemoteUrlScheme.Https), () => OnSelect(RemoteUrlScheme.Https)),
        new(LabelFor(RemoteUrlScheme.Ssh), () => OnSelect(RemoteUrlScheme.Ssh)),
    ];

    private static string LabelFor(RemoteUrlScheme scheme) => scheme switch
    {
        RemoteUrlScheme.Https => "HTTPS",
        RemoteUrlScheme.Ssh => "SSH",
        _ => "URL",
    };
}
