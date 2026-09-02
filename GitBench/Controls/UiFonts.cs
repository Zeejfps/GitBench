namespace GitBench.Controls;

/// <summary>
/// The proportional faces the app draws its own chrome with, beyond the canvas's default.
/// </summary>
/// <remarks>
/// Italic is a family swap rather than a synthetic slant — a canvas resolves a family to one font
/// file, and a sheared upright is not the same shapes as a drawn italic — so the face has a name of
/// its own here. It is registered once at startup and drawn by anything that means "this is
/// provisional": markdown emphasis, and a file browser tab the reader is only previewing.
/// </remarks>
internal static class UiFonts
{
    public const string Italic = "inter-italic";
}
