using GitBench.Controls.Dialogs;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Platform;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Modal shown from the "Add Repository" menu's Clone entry. Collects a remote URL, a parent
/// directory (with a Browse button), and the subfolder name, then runs <c>git clone</c> and
/// opens the result. See <see cref="CloneRepoDialogViewModel"/>.
/// </summary>
internal sealed record CloneRepoDialog : Widget
{
    public required Action OnClose { get; init; }

    protected override View CreateView(Context ctx)
    {
        var vm = new CloneRepoDialogViewModel(
            ctx.Require<IGitService>(),
            ctx.Require<IRepoRegistry>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        // No fixed Width — DialogButton sizes to its label (it carries its own 16px horizontal
        // padding), so pinning a width clips "Browse…".
        var browseButton = new DialogButton(ctx, "Browse…", () =>
        {
            var shell = ctx.Get<IPlatformShell>();
            var picked = shell?.PickFolder("Choose where to clone");
            if (!string.IsNullOrEmpty(picked))
                vm.ParentDir.Value = picked;
        })
        {
            Height = 28,
        };

        var view = new Dialog
        {
            Title = "Clone repository",
            OnClose = OnClose,
            Action = ("Clone", DialogButtonRole.Primary),
            Command = vm.Clone,
            Body =
            [
                new LabeledInput
                {
                    Label = "Repository URL",
                    Value = vm.Url,
                    Placeholder = "https://github.com/user/repo.git",
                },
                new LabeledInput
                {
                    Label = "Clone into",
                    Value = vm.ParentDir,
                    Hint = "Parent folder. The repository is cloned into a new subfolder here.",
                    Accessory = new Raw { View = browseButton },
                },
                new LabeledInput
                {
                    Label = "Folder name",
                    Value = vm.FolderName,
                },
            ],
        }.BuildView(ctx);

        view.UseViewModel(() => vm, v => v.CloseRequested += OnClose);
        return view;
    }
}
