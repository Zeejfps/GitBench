using GitBench.App;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Which edge a resize drag is pulling. Named in inline terms rather than left/right: a leading grip
/// is the left one in a left-to-right layout and the right one in a mirrored layout, so the same zone
/// means the same gesture either way.
/// </summary>
internal enum AssistantGrip
{
    Leading,
    Trailing,
    Top,
    Bottom,
    TopLeading,
    TopTrailing,
    BottomLeading,
    BottomTrailing,
}

/// <summary>Where the panel sits and how big it is, in inline coordinates measured from its host's
/// top leading corner.</summary>
internal readonly record struct AssistantPanelRect(float X, float Y, float Width, float Height);

/// <summary>
/// The assistant panel's size and position: shared by the grips that resize it, the header that moves
/// it, and the frame that places it, and remembered across runs the way a window's geometry is.
/// </summary>
/// <remarks>
/// The stored spot is never trusted on its own. It is resolved against the space the panel actually
/// has, so a placement saved on a large window comes back inside a small one instead of off-screen,
/// and a window resized under an open panel pulls it back into view rather than losing it.
/// </remarks>
internal sealed class AssistantPanelPlacement
{
    /// <summary>Small enough to be worth shrinking to, large enough that the composer and a few
    /// transcript lines are still usable.</summary>
    public const float MinWidth = 300f;
    public const float MinHeight = 260f;

    /// <summary>The width of the resize band around the panel. It sits outside the panel's own
    /// surface, so a grip never covers the transcript's scroll bar.</summary>
    public const float GripSize = 6f;

    // Where an unplaced panel rests, measured in from the host's top trailing corner.
    private const float RestMargin = 12f;

    private readonly PreferencesService _preferences;
    private readonly State<AssistantPanelRect> _rect;

    private float _width;
    private float _height;
    private float? _x;
    private float? _y;
    private float _hostWidth;
    private float _hostHeight;

    public AssistantPanelPlacement(PreferencesService preferences)
    {
        _preferences = preferences;
        var stored = preferences.Current;
        _width = stored.AssistantPanelWidth;
        _height = stored.AssistantPanelHeight;
        _x = stored.AssistantPanelX;
        _y = stored.AssistantPanelY;
        _rect = new State<AssistantPanelRect>(Resolve());
    }

    /// <summary>The placement to draw at right now: the stored one, clamped into the current host.</summary>
    public IReadable<AssistantPanelRect> Rect => _rect;

    /// <summary>Reports the space the panel floats in. Re-clamps rather than rewriting what is
    /// stored, so widening the window again returns the panel to where the user left it.</summary>
    public void SetHost(float width, float height)
    {
        if (Near(width, _hostWidth) && Near(height, _hostHeight)) return;
        _hostWidth = width;
        _hostHeight = height;
        _rect.Value = Resolve();
    }

    /// <summary>Drags the whole panel. Deltas are inline and downward-positive — the header
    /// controller converts the pointer's own axes before calling.</summary>
    public void Move(float dx, float dy)
    {
        var rect = _rect.Value;
        Apply(rect.X + dx, rect.Y + dy, rect.Width, rect.Height);
    }

    /// <summary>Drags one edge or corner, taking the same inline downward-positive deltas as
    /// <see cref="Move"/>. The opposite edge stays put, so a leading drag moves the panel as it
    /// resizes it.</summary>
    public void Resize(AssistantGrip grip, float dx, float dy)
    {
        var rect = _rect.Value;
        var left = rect.X;
        var top = rect.Y;
        var right = rect.X + rect.Width;
        var bottom = rect.Y + rect.Height;

        if (grip is AssistantGrip.Leading or AssistantGrip.TopLeading or AssistantGrip.BottomLeading)
            left = Clamp(left + dx, GripSize, right - MinWidth);
        if (grip is AssistantGrip.Trailing or AssistantGrip.TopTrailing or AssistantGrip.BottomTrailing)
            right = Clamp(right + dx, left + MinWidth, _hostWidth - GripSize);
        if (grip is AssistantGrip.Top or AssistantGrip.TopLeading or AssistantGrip.TopTrailing)
            top = Clamp(top + dy, GripSize, bottom - MinHeight);
        if (grip is AssistantGrip.Bottom or AssistantGrip.BottomLeading or AssistantGrip.BottomTrailing)
            bottom = Clamp(bottom + dy, top + MinHeight, _hostHeight - GripSize);

        Apply(left, top, right - left, bottom - top);
    }

    private void Apply(float x, float y, float width, float height)
    {
        _x = x;
        _y = y;
        _width = width;
        _height = height;

        var resolved = Resolve();
        _rect.Value = resolved;
        _x = resolved.X;
        _y = resolved.Y;
        _width = resolved.Width;
        _height = resolved.Height;
        _preferences.SetAssistantPanelPlacement(resolved.X, resolved.Y, resolved.Width, resolved.Height);
    }

    private AssistantPanelRect Resolve()
    {
        // Before the first layout there is nothing to clamp against; the stored values stand until
        // the frame reports the room it has.
        if (_hostWidth <= 0f || _hostHeight <= 0f)
            return new AssistantPanelRect(_x ?? 0f, _y ?? 0f, _width, _height);

        var width = Clamp(_width, MinWidth, _hostWidth - 2f * GripSize);
        var height = Clamp(_height, MinHeight, _hostHeight - 2f * GripSize);
        var x = Clamp(_x ?? _hostWidth - width - RestMargin, GripSize, _hostWidth - width - GripSize);
        var y = Clamp(_y ?? RestMargin, GripSize, _hostHeight - height - GripSize);
        return new AssistantPanelRect(x, y, width, height);
    }

    // A host too small for the minimum leaves max below min; the floor wins rather than throwing.
    private static float Clamp(float value, float min, float max) =>
        max <= min ? min : Math.Clamp(value, min, max);

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.5f;
}
