using ZGF.Observable;

namespace GitGui;

internal sealed class ModeSwitcherViewModel : IDisposable
{
    public SegmentViewModel HistorySegment { get; }
    public SegmentViewModel LocalChangesSegment { get; }

    public ModeSwitcherViewModel(State<MainViewMode> mode)
    {
        HistorySegment = new SegmentViewModel(mode, MainViewMode.History);
        LocalChangesSegment = new SegmentViewModel(mode, MainViewMode.LocalChanges);
    }

    public void Dispose()
    {
        HistorySegment.Dispose();
        LocalChangesSegment.Dispose();
    }
}
