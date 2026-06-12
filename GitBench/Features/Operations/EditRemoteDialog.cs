using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Branches;
using GitBench.Features.Repos;
using GitBench.Git;
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

    protected override View CreateView(Context ctx)
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

        var schemeDropdown = new SchemeDropdown(ctx);
        schemeDropdown.Bind(vm.Scheme, schemeDropdown.SetScheme);
        schemeDropdown.SchemeSelected += vm.SetScheme;

        var view = new Dialog
        {
            Title = isAdd ? "Add Remote" : "Remote",
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = (isAdd ? "Add" : "Save", DialogButtonRole.Primary),
            Command = vm.Save,
            Body =
            [
                new ThemedText
                {
                    Value = isAdd ? "Add a new remote repository" : "Edit URL of the remote repository",
                    Color = s => s.DialogBody.BodyText,
                },
                new LabeledInput
                {
                    Label = "Remote",
                    Value = vm.Name,
                },
                new LabeledInput
                {
                    Label = "Repository URL",
                    Value = vm.Url,
                    Accessory = new Raw { View = schemeDropdown },
                },
            ],
        }.BuildView(ctx);

        view.UseViewModel(() => vm, v => v.CloseRequested += OnClose);
        return view;
    }
}

/// <summary>
/// Compact SSH/HTTPS toggle shown to the right of the repository URL input. Mirrors
/// <see cref="RemoteDropdown"/>: a bordered button that pops a <see cref="RepoBarContextMenu"/>
/// with the two scheme choices and raises <see cref="SchemeSelected"/> on pick.
/// </summary>
internal sealed class SchemeDropdown : HoverableButton
{
    private readonly Context _ctx;
    private readonly TextView _labelView;
    private RemoteUrlScheme _scheme = RemoteUrlScheme.Other;

    public event Action<RemoteUrlScheme>? SchemeSelected;

    public SchemeDropdown(Context ctx) : base(ctx)
    {
        _ctx = ctx;
        var theme = ctx.Theme();
        Width = 84;
        Height = 28;

        _labelView = new TextView(ctx.Canvas)
        {
            Text = LabelFor(_scheme),
            VerticalTextAlignment = TextAlignment.Center,
        };
        _labelView.BindTextColor(() => theme.Styles.Value.DialogFrame.TitleText);

        var chevron = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.ChevronDown,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 14,
        };
        chevron.BindTextColor(() => theme.Styles.Value.DialogBody.RowText);

        var row = new FlexRowView
        {
            Gap = 4,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                new FlexItem { Grow = 1, Child = _labelView },
                chevron,
            },
        };

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Padding = new PaddingStyle { Left = 8, Right = 6, Top = 4, Bottom = 4 },
            Children = { row },
        };
        BorderedButtonChrome.Bind(background, theme, IsHovered);
        SetBackground(background);
    }

    public void SetScheme(RemoteUrlScheme scheme)
    {
        _scheme = scheme;
        _labelView.Text = LabelFor(scheme);
    }

    protected override void OnClicked()
    {
        var items = new List<RepoBarContextMenu.Item>
        {
            new(LabelFor(RemoteUrlScheme.Https), () => SchemeSelected?.Invoke(RemoteUrlScheme.Https)),
            new(LabelFor(RemoteUrlScheme.Ssh), () => SchemeSelected?.Invoke(RemoteUrlScheme.Ssh)),
        };
        RepoBarContextMenu.Show(_ctx, Position.BottomLeft, items);
    }

    private static string LabelFor(RemoteUrlScheme scheme) => scheme switch
    {
        RemoteUrlScheme.Https => "HTTPS",
        RemoteUrlScheme.Ssh => "SSH",
        _ => "URL",
    };
}
