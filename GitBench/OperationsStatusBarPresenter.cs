using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

internal sealed class OperationsStatusBarPresenter : IViewBehavior
{
    private const int FinishedLingerMs = 3000;

    private readonly OperationsStatusBar _bar;
    private readonly Dictionary<Guid, OperationState> _ops = new();
    private Guid? _expandedOpId;
    private IUiDispatcher? _dispatcher;

    public OperationsStatusBarPresenter(OperationsStatusBar bar)
    {
        _bar = bar;
    }

    public void AttachToContext(View view, Context context)
    {
        _dispatcher = context.Get<IUiDispatcher>();
        var bus = context.Get<IMessageBus>();
        if (bus is null) return;

        bus.Subscribe<OperationStartedMessage>(OnStarted);
        bus.Subscribe<OperationProgressMessage>(OnProgress);
        bus.Subscribe<OperationFinishedMessage>(OnFinished);
    }

    public void DetachFromContext(View view, Context context)
    {
    }

    private void OnStarted(OperationStartedMessage m)
    {
        if (_ops.ContainsKey(m.OpId)) return;

        var state = new OperationState
        {
            OpId = m.OpId,
            Label = m.Label,
            StartedAt = DateTime.UtcNow,
        };
        state.Row = new OperationRow(m.Label, m.Icon, () => ToggleLog(m.OpId));
        _ops[m.OpId] = state;
        _bar.AddRow(state.Row);
    }

    private void OnProgress(OperationProgressMessage m)
    {
        if (!_ops.TryGetValue(m.OpId, out var state)) return;

        state.Log.Add(m.RawLine);
        if (state.Log.Count > 500)
            state.Log.RemoveRange(0, state.Log.Count - 500);

        if (!string.IsNullOrEmpty(m.Phase)) state.Phase = m.Phase;
        if (m.Percent.HasValue) state.Percent = m.Percent.Value;

        if (state.Phase != null) state.Row.Phase = state.Phase;
        state.Row.Percent = state.Percent;
        state.Row.Elapsed = FormatElapsed(DateTime.UtcNow - state.StartedAt);

        if (_expandedOpId == m.OpId) _bar.UpdateLog(state.Log);
    }

    private void OnFinished(OperationFinishedMessage m)
    {
        if (!_ops.TryGetValue(m.OpId, out var state)) return;

        state.Finished = true;
        state.Row.Elapsed = FormatElapsed(DateTime.UtcNow - state.StartedAt);
        if (m.Success) state.Row.MarkSuccess();
        else state.Row.MarkFailure(m.ErrorMessage);

        var dispatcher = _dispatcher;
        var opId = m.OpId;
        Task.Run(async () =>
        {
            await Task.Delay(FinishedLingerMs).ConfigureAwait(false);
            dispatcher?.Post(() => Remove(opId));
        });
    }

    private void Remove(Guid opId)
    {
        if (!_ops.Remove(opId, out var state)) return;
        if (_expandedOpId == opId)
        {
            _expandedOpId = null;
            _bar.HideLog();
        }
        _bar.RemoveRow(state.Row);
    }

    private void ToggleLog(Guid opId)
    {
        if (!_ops.TryGetValue(opId, out var state)) return;
        if (_expandedOpId == opId)
        {
            _expandedOpId = null;
            _bar.HideLog();
            return;
        }
        _expandedOpId = opId;
        _bar.ShowLog(state.Log);
    }

    private static string FormatElapsed(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60) return $"{(int)ts.TotalSeconds}s";
        if (ts.TotalMinutes < 60) return $"{(int)ts.TotalMinutes}m{ts.Seconds:D2}s";
        return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
    }

    private sealed class OperationState
    {
        public Guid OpId;
        public string Label = string.Empty;
        public DateTime StartedAt;
        public string? Phase;
        public float Percent;
        public List<string> Log = new();
        public bool Finished;
        public OperationRow Row = null!;
    }
}
