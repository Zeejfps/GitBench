namespace GitGui;

internal sealed record StashDialogState(
    string Message,
    bool KeepStaged,
    IReadOnlyList<StashFileRow> Files,
    IReadOnlySet<string> CheckedPaths)
{
    public static StashDialogState Initial { get; } = new(
        Message: string.Empty,
        KeepStaged: false,
        Files: Array.Empty<StashFileRow>(),
        CheckedPaths: EmptyCheckedPaths);

    private static readonly IReadOnlySet<string> EmptyCheckedPaths = new HashSet<string>();
}
