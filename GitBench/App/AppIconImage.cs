using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The canvas image id of the app icon, set by startup once the asset is loaded. Observable because
/// the root content mounts before startup gets to load the image — a logo built early swaps from the
/// glyph fallback when the id lands. Stays null on load failure.
/// </summary>
internal sealed class AppIconImage
{
    public State<string?> Id { get; } = new(null);
}
