using GitBench.Controls;
using ZGF.Observable;

namespace GitBench.App;

internal sealed class ModeSwitcherViewModel : IDisposable
{
    public SegmentViewModel<MainViewMode> HistorySegment { get; }
    public SegmentViewModel<MainViewMode> LocalChangesSegment { get; }
    public SegmentViewModel<MainViewMode> TerminalSegment { get; }
    public SegmentViewModel<MainViewMode> FilesSegment { get; }

    public ModeSwitcherViewModel(State<MainViewMode> mode)
    {
        HistorySegment = new SegmentViewModel<MainViewMode>(mode, MainViewMode.History);
        LocalChangesSegment = new SegmentViewModel<MainViewMode>(mode, MainViewMode.LocalChanges);
        TerminalSegment = new SegmentViewModel<MainViewMode>(mode, MainViewMode.Terminal);
        FilesSegment = new SegmentViewModel<MainViewMode>(mode, MainViewMode.Files);
    }

    public void Dispose()
    {
        HistorySegment.Dispose();
        LocalChangesSegment.Dispose();
        TerminalSegment.Dispose();
        FilesSegment.Dispose();
    }
}
