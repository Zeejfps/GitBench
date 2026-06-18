using GitBench.Controls;
using GitBench.Widgets;
using ZGF.Gui;
using ZGF.Gui.Views;
using ZGF.Gui.Widgets;
using ZGF.Observable;

namespace GitBench.Features.StatusBar;

/// <summary>
/// Small icon-only button sized to fit inside the <see cref="StatusBarView"/>. The glyph is an
/// auto-tracked <see cref="Icon"/> so callers can swap it reactively (e.g. sun/moon for the theme
/// toggle), and <see cref="Rotation"/> can spin it (drive a <see cref="SpinnerAnimation"/> while a
/// background op runs). Live state (hover/press/enabled) lives on a
/// <see cref="ButtonState"/> exposed as the widget's <see cref="IInteractable"/> surface,
/// so the <em>parent</em> attaches a controller (<c>button.WithController&lt;KbmController&gt;()</c>)
/// and an optional tooltip (<c>button.WithTooltip("…")</c>), and a press runs <see cref="Command"/>.
/// </summary>
internal sealed record StatusBarIconButton : Widget<ButtonState>
{
    private const float BoxWidth = 22f;
    private const float BoxHeight = 18f;

    /// <summary>The action a press runs; its <see cref="ICommand.CanExecute"/> gates the button.</summary>
    public required ICommand Command { get; init; }

    /// <summary>Icon glyph; a constant or an auto-tracked binding (<c>Prop.Bind(() =&gt; …)</c>).</summary>
    public required Prop<string?> Icon { get; init; }

    /// <summary>Glyph angle (radians); drive from a spinner animation while an op runs.</summary>
    public Prop<float> Rotation { get; init; }

    protected override ButtonState CreateState(Context ctx) => new(Command);

    protected override IWidget Build(Context ctx, ButtonState state) => new Box
    {
        Width = BoxWidth,
        Height = BoxHeight,
        BorderRadius = BorderRadiusStyle.All(4),
        Background = Theme.Color(s => s.StatusBar.IconButtonBackground(state)),
        Children =
        [
            new Text
            {
                FontFamily = LucideIcons.FontFamily,
                FontSize = 13,
                HAlign = TextAlignment.Center,
                VAlign = TextAlignment.Center,
                Value = Icon,
                Rotation = Rotation,
                Color = Theme.Color(s => s.StatusBar.IconColor(state)),
            },
        ],
    };
}
