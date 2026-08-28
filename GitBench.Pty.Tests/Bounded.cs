using System.Runtime.ExceptionServices;

namespace GitBench.Pty.Tests;

/// <summary>
/// Runs a call that is allowed to fail but never allowed to hang. Every blocking call a
/// pseudo-terminal test makes goes through here or through a timed wait — a test that spawns a real
/// process must not be able to wedge the suite.
/// </summary>
static class Bounded
{
    public static void Run(string what, TimeSpan timeout, Action action)
    {
        var thrown = Execute(what, timeout, action);
        if (thrown is not null)
            ExceptionDispatchInfo.Capture(thrown).Throw();
    }

    /// <summary>The exception the call threw, or null if it returned.</summary>
    public static Exception? Catch(string what, TimeSpan timeout, Action action) => Execute(what, timeout, action);

    static Exception? Execute(string what, TimeSpan timeout, Action action)
    {
        Exception? thrown = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                thrown = ex;
            }
        })
        {
            IsBackground = true,
            Name = "pty-test-bounded",
        };

        thread.Start();

        if (!thread.Join(timeout))
            Assert.Fail($"{what} did not complete within {timeout.TotalSeconds:0}s.");

        return thrown;
    }
}
