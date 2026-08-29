namespace GitBench.Pty.Tests;

/// <summary>
/// The lowest file descriptor the process could open right now, which is where a leaked one shows up:
/// <c>open</c> hands back the lowest unused number, so a session that forgets to close its master or
/// the parent's copy of the slave pushes this up and never lets it back down.
/// </summary>
/// <remarks>
/// A number rather than a listing of <c>/dev/fd</c> or <c>/proc/self/fd</c>, because those two are
/// spelled differently on the two Unixes and the number asks the same question portably. Windows
/// handle values do not work this way, which is why the tests that use it are Unix-gated.
/// </remarks>
static class OpenDescriptors
{
    public static int Lowest()
    {
        var probe = Path.Combine(Path.GetTempPath(), "gitbench-pty-fd-" + Guid.NewGuid().ToString("N"));

        try
        {
            using var stream = File.Create(probe);
            return (int)stream.SafeFileHandle.DangerousGetHandle();
        }
        finally
        {
            File.Delete(probe);
        }
    }
}
