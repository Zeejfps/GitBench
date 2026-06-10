namespace GitBench.Features.Diff;

// One popped-out diff window. Owns a live, pinned DiffViewModel (independent of the main
// window's file selection, but still refreshing on working-tree changes) plus the window
// title. Created and owned by DiffWindowsViewModel; the view layer (DiffWindowsPresenter)
// binds a DiffView to <see cref="Diff"/>.
internal sealed class DiffWindowViewModel : IDisposable
{
    public string Title { get; }
    public DiffViewModel Diff { get; }

    public DiffWindowViewModel(string title, DiffViewModel diff)
    {
        Title = title;
        Diff = diff;
    }

    public void Dispose() => Diff.Dispose();
}
