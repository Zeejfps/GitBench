namespace GitBench.Features.Diff;

/// <summary>A half-open stretch of one line, in the file's own columns — what a definition link
/// draws itself over.</summary>
internal readonly record struct FileSpan(FileLine Line, RawColumn Start, RawColumn End);
