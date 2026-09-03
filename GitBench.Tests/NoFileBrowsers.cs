using GitBench.Features.FileBrowser;
using ZGF.Observable;

namespace GitBench.Tests;

/// <summary>A file-browser store with no repository open, for tests that build the app's keybind
/// table without ever entering the Files mode.</summary>
internal sealed class NoFileBrowsers : IFileBrowserStore
{
    public IReadable<FileBrowserViewModel?> Active { get; } = new State<FileBrowserViewModel?>(null);
}
