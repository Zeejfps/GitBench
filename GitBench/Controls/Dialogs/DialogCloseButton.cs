using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

public sealed record DialogCloseButton : Widget
{
    public required Action OnClose { get; init; }
    public Prop<string?> Tooltip { get; init; } = "Close";

    protected override IWidget Build(Context ctx) => new IconButtonWidget
    {
        Command = new Command(OnClose),
        Icon = LucideIcons.X,
        Width = DialogFrame.CloseButtonSize,
        Height = DialogFrame.CloseButtonSize,
        Surface = s => Theme.Color(t => s.Hovered.Value ? t.DialogIconButton.BackgroundHover : t.DialogIconButton.BackgroundIdle),
        Foreground = s => Theme.Color(t => s.Hovered.Value ? t.DialogIconButton.TextHover : t.DialogIconButton.TextIdle),
    }.WithTooltip(Tooltip).WithController<KbmController>();
}
