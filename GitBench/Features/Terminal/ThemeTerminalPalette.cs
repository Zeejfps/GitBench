using GitBench.Terminal.Vt;
using GitBench.Theming;
using ZGF.Gui;

namespace GitBench.Features.Terminal;

/// <summary>
/// Answers a program asking what the pane looks like, in the colours it is being drawn in.
/// </summary>
/// <remarks>
/// <para>
/// The answer follows the theme rather than being fixed when the pane opened, so a terminal held
/// open across a light/dark switch answers for the mode it is in now.
/// </para>
/// <para>
/// It is held as a snapshot taken on the subscription rather than read from the theme at the
/// question, because the two callers are on different threads: a colour query is answered while the
/// engine parses, on the UI thread, and a shell spawn reads the background off the UI thread. A
/// direct read registers an ambient dependency on whatever is being computed, which is not a thing
/// to do from a spawn.
/// </para>
/// <para>
/// Nothing is pushed to a running program when the theme changes, because no sequence exists to
/// push it with: one that asked once keeps the answer it was given, and only one that asks again
/// learns the surface moved. That is the same deal every terminal offers, and it is why the spawn
/// carries a hint in the environment as well — see <see cref="ShellCommand"/>.
/// </para>
/// </remarks>
internal sealed class ThemeTerminalPalette : ITerminalPalette, IDisposable
{
    readonly IDisposable _subscription;

    TerminalStyles _styles;

    public ThemeTerminalPalette(IThemeService<ThemeStyles> theme)
    {
        _styles = theme.Styles.Value.Terminal;
        _subscription = theme.Styles.Subscribe(s => _styles = s.Terminal);
    }

    public TerminalRgb Resolve(TerminalColorSlot slot)
    {
        var styles = _styles;

        return Rgb(slot switch
        {
            TerminalColorSlot.Foreground => styles.DefaultForeground,
            TerminalColorSlot.Background => styles.DefaultBackground,
            TerminalColorSlot.Cursor => styles.Cursor,
            _ => throw new InvalidOperationException($"Unknown colour slot {slot}."),
        });
    }

    public void Dispose() => _subscription.Dispose();

    static TerminalRgb Rgb(uint argb) => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}
