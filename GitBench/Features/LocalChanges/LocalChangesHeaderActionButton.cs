using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Desktop.Controllers;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.LocalChanges;

internal sealed record LocalChangesHeaderActionButton : Widget
{
    private const float ButtonSize = 22f;
    private const float IconSize = 13f;

    /// <summary>Icon glyph; a constant or an auto-tracked binding (<c>Prop.Bind(() =&gt; …)</c>).</summary>
    public required Prop<string?> Icon { get; init; }

    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public ICommand? Command { get; init; }

    public Prop<string?> Tooltip { get; init; }

    protected override IWidget Build(Context ctx) => new IconButtonWidget
    {
        Command = Command,
        Icon = Icon,
        IconSize = IconSize,
        Width = ButtonSize,
        Height = ButtonSize,
        CornerRadius = BorderRadiusStyle.All(Radius.Sm),
        Surface = s => Theme.Color(t => t.HeaderActionButton.Surface(s)),
        Foreground = s => Theme.Color(t => t.HeaderActionButton.Icon(s)),
    }.WithTooltip(Tooltip).WithController<KbmController>();
}
