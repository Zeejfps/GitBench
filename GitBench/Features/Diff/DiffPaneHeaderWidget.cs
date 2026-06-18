using GitBench.Controls;
using GitBench.Features.StatusBar;
using GitBench.Theming;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Desktop.Input;
using ZGF.Gui.Desktop.Widgets;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.Diff;

/// <summary>
/// Header strip for the embedded diff panes (Local Changes, Commit Details): a collapse chevron and
/// "Diff View" title, an LFS badge, and full-file / open-in-window buttons. Clicking the bar toggles
/// the pane's collapse (<see cref="DiffViewModel.IsCollapsed"/>); the nested buttons consume their own
/// clicks. Resolves its <see cref="DiffViewModel"/> from context, so the host provides it with
/// <c>Provide&lt;DiffViewModel&gt;</c> and reads collapse back off the same VM.
/// </summary>
internal sealed record DiffPaneHeaderWidget : Widget
{
    // Height of the always-visible header strip. The host pins the collapsed pane to exactly this
    // height, keeping the chevron clickable.
    public const float HeaderHeight = 24f;

    protected override IWidget Build(Context ctx)
    {
        var vm = ctx.Require<DiffViewModel>();
        var theme = ctx.Theme();
        var hovered = new State<bool>(false);

        return new KbmInput
        {
            // Bubble phase only: the nested buttons consume their clicks first, and clicks on the
            // bar's empty area still bubble up here to toggle collapse.
            Phases = EventPhaseFilter.Bubble,
            OnClick = vm.ToggleCollapse,
            OnHoverEnter = () => hovered.Value = true,
            OnHoverExit = () => hovered.Value = false,
            Child = new Box
            {
                Height = HeaderHeight,
                BorderSize = new BorderSizeStyle { Top = 1, Bottom = 1 },
                Background = Prop.Bind(() => hovered.Value
                    ? theme.Styles.Value.DiffView.HeaderBackgroundHover
                    : theme.Styles.Value.DiffView.HeaderBackgroundIdle),
                BorderColor = theme.Styles.Bind(s => new BorderColorStyle
                {
                    Top = s.DiffView.HeaderBorderTop,
                    Bottom = s.DiffView.HeaderBorderBottom,
                }),
                Children =
                [
                    new Padding
                    {
                        Amount = new PaddingStyle { Left = 8, Right = 6 },
                        Children =
                        [
                            new Row
                            {
                                Gap = 6f,
                                CrossAxis = CrossAxisAlignment.Center,
                                Children =
                                [
                                    new Text
                                    {
                                        FontFamily = LucideIcons.FontFamily,
                                        FontSize = 12f,
                                        Width = 16f,
                                        HAlign = TextAlignment.Center,
                                        VAlign = TextAlignment.Center,
                                        Value = vm.IsCollapsed.Bind(string? (c) =>
                                            c ? LucideIcons.ChevronUp : LucideIcons.ChevronDown),
                                        Color = HeaderTextColor(theme, hovered),
                                    },
                                    new Grow
                                    {
                                        Child = new Text
                                        {
                                            Value = "Diff View",
                                            FontSize = 12f,
                                            VAlign = TextAlignment.Center,
                                            Color = HeaderTextColor(theme, hovered),
                                        },
                                    },
                                    new LfsBadgeWidget { Status = Prop.Bind(vm.LfsStatus) },
                                    FullFileToggleButton(vm),
                                    OpenInWindowButton(vm),
                                ],
                            },
                        ],
                    },
                ],
            },
        };
    }

    // The chevron and title share the bar's hover tint.
    private static Prop<uint> HeaderTextColor(IThemeService<ThemeStyles> theme, IReadable<bool> hovered) =>
        Prop.Bind(() => hovered.Value
            ? theme.Styles.Value.DiffView.HeaderTitleHover
            : theme.Styles.Value.DiffView.HeaderTitleIdle);

    // Toggles the diff body between the normal diff and the after-side full file. Tinted while active
    // so it reads as an engaged toggle, not a one-shot action.
    private static IWidget FullFileToggleButton(DiffViewModel vm) =>
        new IconButton
        {
            Width = 16f,
            Icon = LucideIcons.FileText,
            Command = new Command(vm.ToggleFullFile),
            Foreground = s => Theme.Color(t => vm.Mode.Value == DiffViewMode.FullFile
                ? t.DiffView.HeaderToggleActive
                : t.DiffView.HeaderButtonColor(s)),
        }.WithTooltip("Toggle full file")
            .WithController<KbmController>();

    private static IWidget OpenInWindowButton(DiffViewModel vm) =>
        new IconButton
        {
            Width = 16f,
            Icon = LucideIcons.ExternalLink,
            Command = new Command(vm.RequestOpenInWindow),
            Foreground = s => Theme.Color(t => t.DiffView.HeaderButtonColor(s)),
        }.WithTooltip("Open in new window")
            .WithController<KbmController>();
}
