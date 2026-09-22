using GitBench.App;
using GitBench.Input;
using ZGF.Gui.Desktop.Input;
using ZGF.KeyboardModule;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>Steps the editor font size from the keyboard, over any code body.</summary>
internal sealed class EditorZoomKeys
{
    private readonly IKeyMap _keys;
    private readonly IWritable<EditorFontSize> _size;

    public EditorZoomKeys(IKeyMap keys, IWritable<EditorFontSize> size)
    {
        _keys = keys;
        _size = size;
    }

    /// <summary>True when the key was a zoom command, whether or not the size could still move.</summary>
    public bool TryHandle(KeyboardKey key, InputModifiers modifiers)
    {
        if (_keys.Matches(KeyCommand.EditorZoomIn, key, modifiers))
        {
            _size.Value = _size.Value.Larger;
            return true;
        }

        if (_keys.Matches(KeyCommand.EditorZoomOut, key, modifiers))
        {
            _size.Value = _size.Value.Smaller;
            return true;
        }

        return false;
    }
}
