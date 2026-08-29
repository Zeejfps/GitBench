using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GitBench.Pty.Tests;

/// <summary>
/// Whether a process this test did not start is still around.
/// </summary>
/// <remarks>
/// <para>
/// Signal 0 rather than <see cref="Process.GetProcessById(int)"/>: the processes these tests ask
/// about are orphans a session left behind, and a reaped orphan is reported inconsistently by the
/// managed API while <c>kill(pid, 0)</c> answers exactly.
/// </para>
/// <para>
/// <c>DllImport</c> rather than the <c>LibraryImport</c> the production code uses, because the
/// generated marshalling stub requires unsafe code and this is the only P/Invoke in the test project:
/// turning unsafe blocks on project-wide to save one declaration is the worse trade, and none of this
/// is published ahead of time.
/// </para>
/// <para>
/// It carries no <c>SupportedOSPlatform</c> even though libc is Unix's. The annotation would be
/// honest but useless: the analyser cannot see that <c>UnixPtyFact</c> is the guard, so it would
/// report every call site as unguarded, and silencing that needs either a suppression or an
/// unreachable runtime check in a test that already cannot run anywhere else.
/// </para>
/// </remarks>
static class UnixProcess
{
    public static bool IsAlive(int pid) => Kill(pid, 0) == 0;

    /// <summary>True once the process is gone, false if the deadline passes first.</summary>
    public static bool WaitForExit(int pid, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < timeout)
        {
            if (!IsAlive(pid))
                return true;

            Thread.Sleep(50);
        }

        return !IsAlive(pid);
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    static extern int Kill(int pid, int signal);
}
