namespace GitBench.Infrastructure;

// A result hierarchy whose failure case can be constructed from a bare message — lets
// generic infrastructure (RunOutcome) fold thread-level errors into the type's own
// Failed case instead of carrying a second error channel.
public interface IOutcome<TSelf> where TSelf : IOutcome<TSelf>
{
    static abstract TSelf Fail(string message);

    // The message to surface when this outcome should be treated as a failure by generic
    // infrastructure (AsyncCommand, RunOutcome callers that only care about pass/fail);
    // null for every non-failure case. Concrete handling should pattern-match instead.
    string? FailureMessage { get; }
}
