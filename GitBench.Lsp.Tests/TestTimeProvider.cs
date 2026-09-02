namespace GitBench.Lsp.Tests;

/// <summary>
/// A clock that only moves when a test says so. Every deadline in these tests is expressed as an
/// advance, so nothing here depends on how loaded the machine is.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_lock) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward and fires everything that comes due, in due order.</summary>
    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        while (true)
        {
            FakeTimer? next = null;
            lock (_lock)
            {
                foreach (var timer in _timers)
                {
                    if (timer.DueAt is not { } due || due > target) continue;
                    if (next is null || due < next.DueAt) next = timer;
                }

                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.DueAt!.Value;
            }

            next.Fire();
        }
    }

    private void Forget(FakeTimer timer)
    {
        lock (_lock) _timers.Remove(timer);
    }

    private sealed class FakeTimer(TestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = _period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + _period;
            callback(state);
        }

        public void Dispose() => owner.Forget(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
