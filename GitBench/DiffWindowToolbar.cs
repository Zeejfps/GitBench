using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitGui;

/// <summary>
/// Toolbar for the pop-out diff window: the file path on the left, an LFS badge, and a
/// side-aware whole-file Stage/Unstage button on the right (hidden for commit-side diffs).
/// Per-hunk staging stays inline in the <see cref="DiffView"/> body. This replaces the
/// embedded panes' collapse header, which makes no sense in a standalone window.
/// </summary>
internal sealed class DiffWindowToolbar : MultiChildView, IBind<DiffViewModel>
{
    public const float ToolbarHeight = 28f;

    private readonly State<DiffSide?> _side = new(null);
    private readonly State<bool> _fullFileActive = new(false);
    private readonly LfsBadgeView _lfsBadge = new();
    private readonly TextView _title;

    private Action? _onStageFile;
    private Action? _onUnstageFile;
    private Action? _onToggleFullFile;

    public DiffWindowToolbar(string title)
    {
        _title = new TextView
        {
            Text = title,
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _title.BindThemedTextColor(s => s.DiffView.HeaderTitleIdle);

        var bar = new RectView
        {
            Height = ToolbarHeight,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Padding = new PaddingStyle { Left = 10, Right = 8 },
            Children =
            {
                new FlexRowView
                {
                    Gap = 8f,
                    CrossAxisAlignment = CrossAxisAlignment.Center,
                    Children =
                    {
                        new FlexItem { Grow = 1, Child = _title },
                        _lfsBadge,
                        BuildFullFileToggleButton(),
                        BuildStageButton(),
                    },
                },
            },
        };
        bar.BindThemedBackgroundColor(s => s.DiffView.HeaderBackgroundIdle);
        bar.BindThemedBorderColor(s => new BorderColorStyle { Bottom = s.DiffView.HeaderBorderBottom });

        AddChildToSelf(bar);
    }

    public void Bind(DiffViewModel vm)
    {
        vm.LfsStatus.Subscribe(_lfsBadge.SetStatus);
        vm.CurrentSide.Subscribe(s => _side.Value = s);
        _onStageFile = vm.StageFile;
        _onUnstageFile = vm.UnstageFile;
        _onToggleFullFile = vm.ToggleFullFile;
        vm.Mode.Subscribe(m => _fullFileActive.Value = m == DiffViewMode.FullFile);
    }

    // Mirrors the embedded header's full-file toggle. The pop-out window opens in Diff mode
    // (fresh VM), so this starts inactive.
    private View BuildFullFileToggleButton()
    {
        var hovered = new State<bool>(false);

        var icon = new TextView
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            Text = LucideIcons.FileText,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16f,
        };
        icon.BindThemedTextColor(s => _fullFileActive.Value
            ? s.DiffView.HeaderToggleActive
            : hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var btn = new RectView { Children = { icon } };
        btn.UseController(_ => new HoverableButtonController(
            () => _onToggleFullFile?.Invoke(),
            h => hovered.Value = h));
        return btn;
    }

    // Side-aware file-level action: "Stage" for unstaged changes, "Unstage" for staged, hidden
    // for commit-side (history) diffs, which aren't stageable.
    private View BuildStageButton()
    {
        var hovered = new State<bool>(false);

        var label = new TextView
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        label.BindText(_side, s => s == DiffSide.Staged ? "Unstage" : "Stage");
        label.BindThemedTextColor(s => hovered.Value ? s.DiffView.HeaderTitleHover : s.DiffView.HeaderTitleIdle);

        var btn = new RectView
        {
            Height = 18f,
            Padding = new PaddingStyle { Left = 8, Right = 8 },
            Children = { label },
        };
        btn.BindIsVisible(_side, s => s is DiffSide.Unstaged or DiffSide.Staged);
        btn.UseController(_ => new HoverableButtonController(
            () =>
            {
                if (_side.Value == DiffSide.Staged) _onUnstageFile?.Invoke();
                else _onStageFile?.Invoke();
            },
            h => hovered.Value = h));
        return btn;
    }
}
