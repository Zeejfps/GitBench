using GitBench.Controls;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

public sealed record DialogCloseButton : Widget
{
    public required Action OnClose { get; init; }
    public Prop<string?> Tooltip { get; init; }

    protected override IWidget Build(Context ctx) => new IconButtonWidget
    {
        Command = new Command(OnClose),
        Icon = LucideIcons.X,
        Width = DialogFrame.CloseButtonSize,
        Height = DialogFrame.CloseButtonSize,
        Surface = s => Theme.Color(t => t.DialogIconButton.Surface(s)),
        Foreground = s => Theme.Color(t => t.DialogIconButton.Foreground(s)),
    }.WithTooltip(Tooltip.IsSet ? Tooltip : L.T(s => s.CommonClose)).WithController<KbmController>();
}
