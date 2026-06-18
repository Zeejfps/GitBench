using GitBench.Controls;
using GitBench.Controls.Dialogs;
using GitBench.Features.Repos;
using GitBench.Git;
using GitBench.Messages;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Branches;

internal sealed record PublishBranchDialog : Widget
{
    public required Repo Repo { get; init; }
    public required string LocalBranch { get; init; }
    public required Action OnClose { get; init; }

    protected override IWidget Build(Context ctx)
    {
        var vm = new PublishBranchDialogViewModel(
            new PublishBranchRequest(Repo, LocalBranch),
            ctx.Require<IGitService>(),
            ctx.Require<IUiDispatcher>(),
            ctx.Require<IMessageBus>());

        var remoteDropdown = new RemoteDropdown(ctx);
        remoteDropdown.BindTwoWay(remoteDropdown.SelectedState, vm.SelectedRemote);
        remoteDropdown.Bind(vm.Remotes, remoteDropdown.SetOptions);

        return new Dialog
        {
            Title = "Publish branch",
            OnClose = OnClose,
            Width = DialogFrame.WidthWide,
            Action = ("Publish", DialogButtonRole.Primary),
            Command = vm.Publish,
            Error = vm.ErrorMessage,
            ConfirmKeys = true,
            ViewModel = vm,
            Body =
            [
                new Text
                {
                    Value = "First push — choose a remote and set the upstream",
                    HAlign = TextAlignment.Center,
                    VAlign = TextAlignment.Center,
                    Color = Theme.Color(s => s.DialogBody.RowTextMissing),
                },
                new LabeledRow { Label = "Branch:", Value = BranchChip(LocalBranch) },
                new LabeledRow { Label = "To:", Value = new Raw { View = remoteDropdown } },
                new Checkbox
                {
                    Label = "Track this remote branch (set upstream)",
                    Value = vm.SetUpstream,
                    Height = 24,
                }.WithController<KbmController>(),
            ],
        };
    }

    private static IWidget BranchChip(string name) => new Row
    {
        Gap = 6,
        CrossAxis = CrossAxisAlignment.Center,
        Children =
        [
            new Text
            {
                Value = LucideIcons.Branch,
                FontFamily = LucideIcons.FontFamily,
                FontSize = 14,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogBody.BodyText),
            },
            new Text
            {
                Value = name,
                VAlign = TextAlignment.Center,
                Color = Theme.Color(s => s.DialogFrame.TitleText),
            },
        ],
    };
}

internal readonly record struct PublishBranchRequest(Repo Repo, string LocalBranch);

internal sealed class RemoteDropdown : HoverableButton
{
    private readonly Context _ctx;
    private readonly TextView _chevron;
    private IReadOnlyList<string> _options = Array.Empty<string>();

    public State<string> SelectedState { get; } = new(string.Empty);
    public string Selected => SelectedState.Value;

    public RemoteDropdown(Context ctx) : base(ctx)
    {
        _ctx = ctx;
        Height = 30;
        var theme = ctx.Theme();

        var icon = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.Branch,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 14,
            VerticalTextAlignment = TextAlignment.Center,
        };
        icon.BindTextColor(() => theme.Styles.Value.DialogBody.BodyText);

        var labelView = new TextView(ctx.Canvas)
        {
            VerticalTextAlignment = TextAlignment.Center,
        };
        labelView.BindText(() => string.IsNullOrEmpty(SelectedState.Value) ? "(no remotes)" : SelectedState.Value);
        labelView.BindTextColor(() => string.IsNullOrEmpty(SelectedState.Value)
            ? theme.Styles.Value.DialogBody.RowTextMissing
            : theme.Styles.Value.DialogFrame.TitleText);

        _chevron = new TextView(ctx.Canvas)
        {
            Text = LucideIcons.ChevronDown,
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16,
        };
        _chevron.BindTextColor(() => theme.Styles.Value.DialogBody.RowText);

        var row = new FlexRowView
        {
            Gap = 6,
            CrossAxisAlignment = CrossAxisAlignment.Center,
            Children =
            {
                icon,
                new FlexItem { Grow = 1, Child = labelView },
                _chevron,
            },
        };

        var background = new RectView
        {
            BorderSize = BorderSizeStyle.All(1),
            BorderRadius = BorderRadiusStyle.All(3),
            Children =
            {
                new PaddingView
                {
                    Padding = new PaddingStyle { Left = 8, Right = 8, Top = 4, Bottom = 4 },
                    Children = { row },
                },
            },
        };
        BorderedButtonChrome.Bind(background, theme, IsHovered);
        SetBackground(background);
    }

    public void SetOptions(IReadOnlyList<string> options)
    {
        _options = options;
        if (options.Count == 0)
        {
            IsEnabled.Value = false;
            _chevron.Text = string.Empty;
            return;
        }
        IsEnabled.Value = true;
        _chevron.Text = options.Count > 1 ? LucideIcons.ChevronDown : string.Empty;
    }

    protected override void OnClicked()
    {
        if (_options.Count <= 1) return;
        var ctx = _ctx;
        var items = new List<RepoBarContextMenu.Item>(_options.Count);
        foreach (var opt in _options)
        {
            var captured = opt;
            items.Add(new RepoBarContextMenu.Item(
                captured,
                () => SelectedState.Value = captured));
        }
        RepoBarContextMenu.Show(ctx, Position.BottomLeft, items);
    }
}
