namespace GitBench.Features.FileBrowser;

internal abstract record PreviewFocus
{
    private PreviewFocus() { }

    public static readonly PreviewFocus Nothing = new None();

    public sealed record None : PreviewFocus;

    public sealed record Row(string RowKey) : PreviewFocus;

    public sealed record Detached(string AbsolutePath) : PreviewFocus;
}

internal abstract record FileBrowserPlace
{
    private FileBrowserPlace() { }

    public sealed record Row(string RowKey, int Line) : FileBrowserPlace;

    public sealed record Detached(string AbsolutePath, int Line) : FileBrowserPlace;
}
