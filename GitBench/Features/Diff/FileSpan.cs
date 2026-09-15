namespace GitBench.Features.Diff;

/// <summary>A half-open stretch of one line, in the file's own raw columns rather than tab-expanded
/// ones: a fact about the file, which whatever draws it converts for the painter.</summary>
internal readonly record struct FileSpan(FileLine Line, RawColumn Start, RawColumn End);
