using ZGF.Observable;

namespace GitGui;

internal sealed class SegmentViewModel : IDisposable
{
    private readonly State<MainViewMode> _mode;
    private readonly MainViewMode _myMode;
    private readonly Derived<bool> _isActive;

    public IReadable<bool> IsActive => _isActive;

    public SegmentViewModel(State<MainViewMode> mode, MainViewMode myMode)
    {
        _mode = mode;
        _myMode = myMode;
        _isActive = new Derived<bool>(() => _mode.Value == _myMode);
    }

    public void Activate() => _mode.Value = _myMode;

    public void Dispose() => _isActive.Dispose();
}