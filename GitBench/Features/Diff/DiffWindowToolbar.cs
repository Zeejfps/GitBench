using GitBench.Controls;
using GitBench.Features.StatusBar;
using GitBench.Git;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Bindings;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Views;
using ZGF.Observable;

namespace GitBench.Features.Diff;

/// <summary>
/// Toolbar for the pop-out diff window: the file path on the left, an LFS badge, and a
/// side-aware whole-file Stage/Unstage button on the right (hidden for commit-side diffs).
/// Per-hunk staging stays inline in the <see cref="DiffView"/> body. This replaces the
/// embedded panes' collapse header, which makes no sense in a standalone window.
/// </summary>
internal sealed class DiffWindowToolbar : ContainerView
{
    public const float ToolbarHeight = 28f;

    private readonly State<DiffSide?> _side = new(null);
    private readonly State<bool> _fullFileActive = new(false);
    private readonly State<LfsBadge> _lfsStatus = new(LfsBadge.None);
    private readonly TextView _title;

    private DiffViewModel? _vm;
    private Action? _onStageFile;
    private Action? _onUnstageFile;
    private Action? _onToggleFullFile;

    public DiffWindowToolbar(Context ctx)
    {
        var input = ctx.Require<InputSystem>();
        var theme = ctx.Theme();

        _title = new TextView(ctx.Canvas)
        {
            FontSize = 12f,
            VerticalTextAlignment = TextAlignment.Center,
        };
        _title.BindThemedTextColor(theme, s => s.DiffView.HeaderTitleIdle);

        var bar = new RectView
        {
            Height = ToolbarHeight,
            BorderSize = new BorderSizeStyle { Bottom = 1 },
            Children =
            {
                new PaddingView
                {
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
                                new LfsBadgeWidget { Status = _lfsStatus }.BuildView(ctx),
                                BuildFullFileToggleButton(ctx, input, theme),
                                BuildStageButton(ctx, input, theme),
                            },
                        },
                    },
                },
            },
        };
        bar.BindThemedBackgroundColor(theme, s => s.DiffView.HeaderBackgroundIdle);
        bar.BindThemedBorderColor(theme, s => new BorderColorStyle { Bottom = s.DiffView.HeaderBorderBottom });

        AddChildToSelf(bar);
    }

    public string Title
    {
        set => _title.Text = value;
    }

    public void Bind(DiffViewModel vm)
    {
        _onStageFile = vm.StageFile;
        _onUnstageFile = vm.UnstageFile;
        _onToggleFullFile = vm.ToggleFullFile;
        if (ReferenceEquals(_vm, vm)) return;
        _vm = vm;
        this.Bind(vm.LfsStatus, _lfsStatus.Set);
        this.Bind(vm.CurrentSide, s => _side.Value = s);
        this.Bind(vm.Mode, m => _fullFileActive.Value = m == DiffViewMode.FullFile);
    }

    // Mirrors the embedded header's full-file toggle. The pop-out window opens in Diff mode
    // (fresh VM), so this starts inactive.
    private View BuildFullFileToggleButton(Context ctx, InputSystem input, IThemeService<ThemeStyles> theme)
    {
        var hovered = new State<bool>(false);

        var icon = new TextView(ctx.Canvas)
        {
            FontFamily = LucideIcons.FontFamily,
            FontSize = 12f,
            Text = LucideIcons.FileText,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            Width = 16f,
        };
        icon.BindTextColor(() => _fullFileActive.Value
            ? theme.Styles.Value.DiffView.HeaderToggleActive
            : hovered.Value
                ? theme.Styles.Value.DiffView.HeaderTitleHover
                : theme.Styles.Value.DiffView.HeaderTitleIdle);

        var btn = new RectView { Children = { icon } };
        btn.UseController(input, () => new HoverableButtonController(
            () => _onToggleFullFile?.Invoke(),
            h => hovered.Value = h));
        return btn;
    }

    // Side-aware file-level action: "Stage" for unstaged changes, "Unstage" for staged, hidden
    // for commit-side (history) diffs, which aren't stageable.
    private View BuildStageButton(Context ctx, InputSystem input, IThemeService<ThemeStyles> theme)
    {
        var hovered = new State<bool>(false);

        var label = new TextView(ctx.Canvas)
        {
            FontSize = 11f,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        label.BindText(_side, s => s == DiffSide.Staged ? "Unstage" : "Stage");
        label.BindTextColor(() => hovered.Value
            ? theme.Styles.Value.DiffView.HeaderTitleHover
            : theme.Styles.Value.DiffView.HeaderTitleIdle);

        var btn = new PaddingView
        {
            Height = 18f,
            Padding = new PaddingStyle { Left = 8, Right = 8 },
            Children = { label },
        };
        btn.BindIsVisible(_side, s => s is DiffSide.Unstaged or DiffSide.Staged);
        btn.UseController(input, () => new HoverableButtonController(
            () =>
            {
                if (_side.Value == DiffSide.Staged) _onUnstageFile?.Invoke();
                else _onStageFile?.Invoke();
            },
            h => hovered.Value = h));
        return btn;
    }
}
