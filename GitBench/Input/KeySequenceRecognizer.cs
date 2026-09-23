using ZGF.Gui.Desktop.Input;

namespace GitBench.Input;

/// <summary>
/// The one place a trigger spanning several key events is recognised, in front of whatever has
/// focus: it sees every key before the focused editor, terminal or field does. It holds the state
/// between those events and runs the command a completed trigger is bound to. A double tap claims
/// no key, since the modifier still has to work as one.
/// </summary>
internal sealed class KeySequenceRecognizer : IInputFilter
{
    private readonly IKeyMap _keys;
    private readonly Action<KeyCommand> _run;
    private readonly DoubleTapDetector _doubleTaps;

    public KeySequenceRecognizer(IKeyMap keys, TimeProvider time, Action<KeyCommand> run)
    {
        _keys = keys;
        _run = run;
        _doubleTaps = new DoubleTapDetector(time);
    }

    public void OnKey(ref KeyboardKeyEvent e)
    {
        if (_doubleTaps.OnKey(in e) is not { } modifier) return;

        foreach (var command in Enum.GetValues<KeyCommand>())
            if (_keys.MatchesDoubleTap(command, modifier))
                _run(command);
    }

    public void OnMouseButton(in MouseButtonEvent e) => _doubleTaps.OnMouseButton(in e);

    public void OnWindowFocusLost() => _doubleTaps.Reset();
}
