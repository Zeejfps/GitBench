namespace GitBench.Infrastructure;

// A result hierarchy whose failure case can be constructed from a bare message — lets
// generic infrastructure (RunOutcome) fold thread-level errors into the type's own
// Failed case instead of carrying a second error channel.
public interface IOutcome<TSelf> where TSelf : IOutcome<TSelf>
{
    static abstract TSelf Fail(string message);
}
