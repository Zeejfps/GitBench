namespace GitBench.Features.Markdown.Rendering;

/// <summary>
/// Font families the markdown renderer draws with. <see cref="ItalicFamily"/> names the true
/// italic face (Inter Italic) used for emphasis — italic is a family swap, not a synthetic
/// slant. Bold-italic is this family plus the existing <c>FontWeight.Bold</c> synthetic
/// embolden, so no separate bold-italic face is embedded.
/// </summary>
internal static class MarkdownFonts
{
    public const string ItalicFamily = "inter-italic";
}
