using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GitBench.Pty.Tests;

/// <summary>
/// Which console host a Windows session runs on. The session binds to the ConPTY shipped beside the
/// app, and that launcher falls back to the inbox conhost without a word when the OpenConsole it
/// expects is not where it looks — a packaging slip that would bring back the inbox host's
/// mid-frame cursor leaks with nothing failing.
/// </summary>
[Collection(PtyTestCollection.Name)]
public class ConPtyHostTests
{
    [WindowsPtyFact]
    public void Start_HostsTheChildInTheBundledOpenConsoleOnWindows()
    {
        using var work = new TempDirectory();

        using var session = PtyChild.Start(PtyChild.SitsSilently(work));
        var output = new PtyOutputReader(session);

        Assert.True(
            output.WaitFor(PtyChild.Ready, PtyChild.Patience),
            $"The child never came up. Terminal showed:\n{output.Describe()}");

        Assert.Contains(
            ChildProcesses.Of(Environment.ProcessId),
            name => name.Equals("OpenConsole.exe", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The image names of every live direct child of a process, through the tool-help snapshot.</summary>
[SupportedOSPlatform("windows")]
static class ChildProcesses
{
    const uint SnapshotProcesses = 0x2;
    const uint MaxPath = 260;

    public static IReadOnlyList<string> Of(int parentId)
    {
        var names = new List<string>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            throw new InvalidOperationException("CreateToolhelp32Snapshot failed.");

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry)) return names;

            do
            {
                if (entry.ParentProcessId == (uint)parentId)
                    names.Add(entry.ExeFile);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return names;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = (int)MaxPath)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
