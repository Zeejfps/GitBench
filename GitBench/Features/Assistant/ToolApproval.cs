using System.Text.Json;
using GitBench.Features.Assistant.Tools;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Assistant;

/// <summary>
/// Asked before a tool that changes the repository runs. The turn stays suspended until it answers.
/// </summary>
internal interface IToolApprovalGate
{
    Task<bool> RequestAsync(string toolName, JsonElement arguments, CancellationToken ct);
}

/// <summary>How a question was settled. Pending is the only state with an answer still to give.</summary>
internal enum ToolApprovalOutcome
{
    Pending,
    Approved,
    Denied,
    Cancelled,
}

/// <summary>
/// One write waiting on the user: which tool, the arguments it would run with, and the answer.
/// </summary>
/// <remarks>
/// It lives in the session's transcript rather than in the view, so closing the overlay mid-turn and
/// opening it again finds the same question still waiting. Every path out of Pending is a one-way
/// door: the first answer wins, and a turn that ends first withdraws the question instead of leaving
/// a card with live buttons behind it.
/// </remarks>
internal sealed class PendingToolApproval : IDisposable
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly State<ToolApprovalOutcome> _outcome = new(ToolApprovalOutcome.Pending);
    private readonly Derived<bool> _isPending;

    public PendingToolApproval(string toolName, string arguments)
    {
        ToolName = toolName;
        Arguments = arguments;
        _isPending = new Derived<bool>(() => _outcome.Value == ToolApprovalOutcome.Pending);
        Approve = new Command(() => Answer(ToolApprovalOutcome.Approved), _isPending);
        Deny = new Command(() => Answer(ToolApprovalOutcome.Denied), _isPending);
    }

    public string ToolName { get; }

    /// <summary>The arguments as they will be passed, verbatim — what is approved is the actual
    /// values, not a description of them.</summary>
    public string Arguments { get; }

    public IReadable<ToolApprovalOutcome> Outcome => _outcome;

    public IReadable<bool> IsPending => _isPending;

    public ICommand Approve { get; }

    public ICommand Deny { get; }

    /// <summary>Suspends the caller until the question is answered. A cancelled turn throws, the
    /// same as any other await inside it.</summary>
    public Task<bool> WaitAsync(CancellationToken ct) => _answer.Task.WaitAsync(ct);

    /// <summary>Withdraws the question — the turn ended before it was answered.</summary>
    public void Cancel() => Answer(ToolApprovalOutcome.Cancelled);

    private void Answer(ToolApprovalOutcome outcome)
    {
        if (_outcome.Value != ToolApprovalOutcome.Pending) return;
        _outcome.Value = outcome;
        _answer.TrySetResult(outcome == ToolApprovalOutcome.Approved);
    }

    public void Dispose()
    {
        Cancel();
        _isPending.Dispose();
        _outcome.Dispose();
    }
}

/// <summary>
/// The gate a running turn waits on, marshalled onto the UI thread: the question is raised where the
/// transcript lives, and the loop — running on a worker — carries on the moment it is answered.
/// </summary>
internal sealed class ToolApprovalQueue : IToolApprovalGate
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<PendingToolApproval> _onRequested;

    public ToolApprovalQueue(IUiDispatcher dispatcher, Action<PendingToolApproval> onRequested)
    {
        _dispatcher = dispatcher;
        _onRequested = onRequested;
    }

    public Task<bool> RequestAsync(string toolName, JsonElement arguments, CancellationToken ct)
    {
        var pending = new PendingToolApproval(toolName, ToolJson.Describe(arguments));
        _dispatcher.Post(() => _onRequested(pending));
        return pending.WaitAsync(ct);
    }
}
