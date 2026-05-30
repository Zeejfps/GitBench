namespace GitGui;

internal sealed record DiscardChangesState(
    IReadOnlyList<DiscardFileRow> Files,
    IReadOnlySet<string> CheckedPaths)
{
    public static DiscardChangesState Initial { get; } = new(
        Files: Array.Empty<DiscardFileRow>(),
        CheckedPaths: EmptyCheckedPaths);

    private static readonly IReadOnlySet<string> EmptyCheckedPaths = new HashSet<string>();
}
