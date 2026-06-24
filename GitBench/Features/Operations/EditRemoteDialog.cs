using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Branches;
using GitBench.Features.Repos;
using GitBench.Git;
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
        var isAdd = RemoteName is null;
        var request = isAdd
            ? new EditRemoteRequest(Repo, "origin", IsAdd: true)
            : new EditRemoteRequest(Repo, RemoteName!);

        var vm = new EditRemoteDialogViewModel(
            request,
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var s = ctx.Localization().Strings.Value;
        return new Dialog
        {
            ViewModel = vm,
            Title = isAdd ? s.OperationsRemoteTitleAdd : s.OperationsRemoteTitleEdit,
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = (isAdd ? s.CommonAdd : s.CommonSave, DialogButtonRole.Primary),
            Command = vm.Save,
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
                    Value = vm.Name,
                },
                new LabeledInput
                {
                    Label = s.CommonRepositoryUrl,
                    Value = vm.Url,
                    Accessory = new SchemeDropdown { Scheme = vm.Scheme, OnSelect = vm.SetScheme },
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
