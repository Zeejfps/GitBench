using ZGF.Observable;

namespace GitBench.App;

/// <summary>
/// The canvas image id of the assistant's dino mark, set by startup once the asset is loaded.
/// Observable because the root content mounts before startup gets to load the image — a mark built
/// early swaps from the glyph fallback when the id lands. Stays null on load failure.
/// </summary>
internal sealed class AssistantMarkImage
{
    public State<string?> Id { get; } = new(null);
}
