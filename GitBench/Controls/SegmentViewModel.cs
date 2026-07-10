using ZGF.Observable;

namespace GitBench.Controls;

/// <summary>What a <see cref="Segment"/> needs of its model: whether it is the active choice, and how
/// to become it. Keeps the widget non-generic while the choice it drives is any enum.</summary>
internal interface ISegmentModel
{
    IReadable<bool> IsActive { get; }
    void Activate();
}

/// <summary>One choice in a segmented pill, bound to a shared <see cref="State{T}"/>: active while the
/// state holds <c>myValue</c>, and pressing it writes that value back.</summary>
internal sealed class SegmentViewModel<T> : ISegmentModel, IDisposable
{
    private readonly State<T> _value;
    private readonly T _myValue;
    private readonly Derived<bool> _isActive;

    public IReadable<bool> IsActive => _isActive;

    public SegmentViewModel(State<T> value, T myValue)
    {
        _value = value;
        _myValue = myValue;
        _isActive = new Derived<bool>(() => EqualityComparer<T>.Default.Equals(_value.Value, _myValue));
    }

    public void Activate() => _value.Value = _myValue;

    public void Dispose() => _isActive.Dispose();
}
