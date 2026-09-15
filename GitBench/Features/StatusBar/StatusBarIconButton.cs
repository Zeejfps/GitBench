using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

internal sealed record StatusBarIconButton : IWidget<ButtonState>
{
    public float BoxWidth { get; init; } = 22f;
    public float BoxHeight { get; init; } = 18f;
    public float IconSize { get; init; } = 13f;
    public required ICommand Command { get; init; }
    public required Prop<string?> Icon { get; init; }
    public Prop<float> Rotation { get; init; }
    public Prop<bool> Visible { get; init; }

    private IconButtonWidget? _inner;

    private IconButtonWidget Inner => _inner ??= new IconButtonWidget
    {
        Command = Command,
        Icon = Icon,
        IconSize = IconSize,
        Rotation = Rotation,
        Visible = Visible,
        Width = BoxWidth,
        Height = BoxHeight,
        Surface = s => Theme.Color(t => t.StatusBar.IconButtonBackground(s)),
        Foreground = s => Theme.Color(t => t.StatusBar.IconColor(s)),
    };

    public ButtonState State => Inner.State;

    public View BuildView(Context ctx) => Inner.BuildView(ctx);
}
