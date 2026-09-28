namespace GitBench.Features.Editor;

/// <summary>Runs of lines someone pointed the reader at in one file, numbered in order.</summary>
internal sealed record EditorSpotlights(string Path, IReadOnlyList<EditorSpotlight> Runs);

/// <summary>One lit run of lines, and what it is.</summary>
internal sealed record EditorSpotlight(LineSpan Lines, string? Note);
