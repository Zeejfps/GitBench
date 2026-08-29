using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace GitBench.Pty.Platforms.Unix;

/// <summary>
/// A pseudo-terminal session backed by a POSIX pseudo-terminal and a session-leading posix_spawn.
/// </summary>
/// <remarks>
/// <para>
/// <c>posix_openpt</c> rather than <c>openpty</c>, which is the one everybody reaches for first:
/// openpty lives in libutil, spelled <c>libutil.dylib</c> on macOS and <c>libutil.so.1</c> or libc
/// itself on Linux depending on the glibc version, while the four calls used here are POSIX and in
/// libc on both. <c>ptsname</c> has no reentrant form on macOS and answers out of static storage, so
/// the walk from the master's open to the slave's is serialised across sessions; without that, two
/// overlapping spawns share one answer and the second child attaches to the first one's terminal.
/// </para>
/// <para>
/// The order of the opening moves is not free. The kernel refuses <c>TIOCSWINSZ</c> on a master no
/// slave has ever been opened on and the child then reports a terminal of 0x0, so the parent opens
/// the slave itself before setting the size; and it closes that copy the moment the child has one of
/// its own, because while any descriptor on the slave is open the master can never reach end of
/// stream. For the same reason every descriptor the parent holds is kept out of the child — the
/// terminal by opening it close-on-exec, the teardown pipe by a close file action, since macOS has no
/// <c>pipe2</c> — as a descendant that inherited the parent's copy of the slave would hold the stream
/// open in its place.
/// </para>
/// <para>
/// The controlling terminal is arranged without a fork: <c>POSIX_SPAWN_SETSID</c> makes the child a
/// session leader, and the first file action opens the slave without <c>O_NOCTTY</c>, which is what
/// acquires it as the controlling terminal. Dup'ing the slave onto the three standard descriptors
/// alone would pass <c>[ -t 1 ]</c> and still leave the child with no job control and no Ctrl-C.
/// </para>
/// <para>
/// Output is drained onto a <see cref="TerminalOutput"/> of the session's own rather than read out of
/// the master by the caller, because macOS throws the terminal's queue away when the last descriptor
/// on the slave closes: without something already reading, a child's last line dies with it. Holding
/// the parent's copy of the slave open would keep the queue instead, and was measured to wedge the
/// child in <c>exit</c> for as long as it is held, which is worse.
/// </para>
/// <para>
/// A child ended by a signal has no exit code at all — the status word says which signal it was
/// instead — and it is still a child that ended on its own, so <see cref="Exited"/> reports it as a
/// completion carrying the number every shell reports for the same event, 128 plus the signal. A
/// child this session ended is told apart by <see cref="Dispose"/> having already published a
/// teardown, never by the status word, which cannot tell the two apart.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
internal sealed class UnixPtySession : IPtySession
{
    const int DrainSize = 4096;

    static readonly TimeSpan ThreadPatience = TimeSpan.FromSeconds(2);
    static readonly object TerminalNaming = new();

    readonly GatedHandle<SafeFileDescriptor> _master;
    readonly GatedHandle<SafeFileDescriptor> _wakeup;
    readonly SafeFileDescriptor _wakeupSignal;
    readonly int _child;
    readonly int _signalTarget;
    readonly TaskCompletionSource<PtyExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TerminalOutput _received = new();
    readonly Thread _drain;
    readonly Thread _watcher;
    readonly object _reaping = new();

    bool _reaped;
    int _disposed;

    public UnixPtySession(PtySessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var terminal = Spawn(options);

        _master = new GatedHandle<SafeFileDescriptor>(terminal.Master);
        _wakeup = new GatedHandle<SafeFileDescriptor>(terminal.Wakeup);
        _wakeupSignal = terminal.WakeupSignal;
        _child = terminal.Child;
        _signalTarget = terminal.SignalTarget;

        _drain = new Thread(DrainOutput)
        {
            IsBackground = true,
            Name = "gitbench-pty-output",
        };

        _watcher = new Thread(WatchChild)
        {
            IsBackground = true,
            Name = "gitbench-pty-child",
        };

        _drain.Start();
        _watcher.Start();
    }

    public Task<PtyExit> Exited => _exit.Task;

    public int ReadOutput(Span<byte> buffer) => buffer.IsEmpty ? 0 : _received.Take(buffer);

    public unsafe void WriteInput(ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();

        if (bytes.IsEmpty)
            return;

        if (!_master.TryEnter(out var master))
        {
            ThrowIfDisposed();
            return;
        }

        try
        {
            var remaining = bytes;

            while (!remaining.IsEmpty)
            {
                nint written;

                fixed (byte* source = remaining)
                    written = UnixNative.Write(master.Descriptor, source, (nuint)remaining.Length);

                if (written < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();

                    if (UnixErrno.ShouldRetry(errno))
                        continue;

                    if (UnixErrno.ChildIsGone(errno))
                        return;

                    throw new IOException($"Writing to the pseudo-terminal failed with errno {errno}.");
                }

                remaining = remaining[(int)written..];
            }
        }
        finally
        {
            _master.Leave();
        }
    }

    /// <remarks>
    /// A failed ioctl is dropped for the reason a failed write is: a window manager keeps sending sizes
    /// through a drag, and a terminal whose child has gone is a normal thing to be handed one late.
    /// </remarks>
    public unsafe void Resize(PtySize size)
    {
        ThrowIfDisposed();

        if (!_master.TryEnter(out var master))
        {
            ThrowIfDisposed();
            return;
        }

        try
        {
            var requested = ToWindowSize(size);
            UnixNative.SetWindowSize(master.Descriptor, UnixNative.SetWindowSizeRequest, &requested);
        }
        finally
        {
            _master.Leave();
        }
    }

    /// <remarks>
    /// <para>
    /// A reader parked in <c>read</c> is released by a pipe this session polls alongside the master,
    /// not by the child's death closing the terminal. Death is the tempting mechanism, since the
    /// process group has to be signalled anyway, but it releases the reader only for as long as no
    /// descendant holds the slave — one <c>setsid</c> and the reader is stranded for the life of the
    /// app, which is the one failure <see cref="GatedHandle{THandle}"/> deliberately does not cover.
    /// The pipe answers for the session itself and owes nothing to what the child left behind.
    /// </para>
    /// <para>
    /// The whole process group is ended rather than the child alone. <c>POSIX_SPAWN_SETSID</c> makes
    /// the child a group leader, so a shell's background jobs are inside it; on Linux anything still
    /// holding the slave keeps the terminal open, and macOS only cleans up after them because the
    /// session leader died. The group is named by the child's own pid, and the spawn is checked to
    /// have made the child lead one rather than assumed to have: a negative number naming a group the
    /// child does not lead would name this process's own.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        lock (_reaping)
        {
            if (!_reaped)
            {
                _exit.TrySetResult(new PtyExit.TornDown());
                UnixNative.Kill(_signalTarget, UnixNative.Terminate);
            }
        }

        _received.Abandon();
        Wake();

        _master.Close();
        _wakeup.Close();

        _drain.Join(ThreadPatience);
        _watcher.Join(ThreadPatience);
    }

    void DrainOutput()
    {
        try
        {
            if (!_master.TryEnter(out var master))
                return;

            try
            {
                if (!_wakeup.TryEnter(out var wakeup))
                    return;

                try
                {
                    Drain(master.Descriptor, wakeup.Descriptor);
                }
                finally
                {
                    _wakeup.Leave();
                }
            }
            finally
            {
                _master.Leave();
            }
        }
        catch (IOException failure)
        {
            _received.Fail(failure);
        }
        finally
        {
            _received.End();
        }
    }

    unsafe void Drain(int master, int wakeup)
    {
        var buffer = new byte[DrainSize];

        while (WaitForOutput(master, wakeup))
        {
            nint read;

            fixed (byte* target = buffer)
                read = UnixNative.Read(master, target, DrainSize);

            if (read > 0)
            {
                if (!_received.Give(buffer.AsSpan(0, (int)read)))
                    return;

                continue;
            }

            if (read == 0)
                return;

            var errno = Marshal.GetLastPInvokeError();

            if (UnixErrno.ShouldRetry(errno))
                continue;

            if (UnixErrno.EndsTheOutputStream(errno) || Volatile.Read(ref _disposed) != 0)
                return;

            throw new IOException($"Reading the pseudo-terminal failed with errno {errno}.");
        }
    }

    unsafe void Wake()
    {
        var wakeup = (byte)0;
        UnixNative.Write(_wakeupSignal.Descriptor, &wakeup, 1);
        _wakeupSignal.Dispose();
    }

    void WatchChild()
    {
        try
        {
            var reaped = Reap();

            lock (_reaping)
            {
                _reaped = true;
                _exit.TrySetResult(reaped);
            }
        }
        catch (IOException failure)
        {
            lock (_reaping)
            {
                _reaped = true;
                _exit.TrySetException(failure);
            }
        }
    }

    PtyExit Reap()
    {
        while (true)
        {
            var reaped = UnixNative.WaitForProcess(_child, out var status, 0);

            if (reaped == _child)
                return Decode(status);

            var errno = Marshal.GetLastPInvokeError();

            if (UnixErrno.ShouldRetry(errno))
                continue;

            throw new IOException($"Waiting for pseudo-terminal child {_child} failed with errno {errno}.");
        }
    }

    static PtyExit Decode(int status) =>
        (status & 0x7F) == 0
            ? new PtyExit.Completed((status >> 8) & 0xFF)
            : new PtyExit.Completed(128 + (status & 0x7F));

    static unsafe bool WaitForOutput(int master, int wakeup)
    {
        var watched = stackalloc PollDescriptor[2];

        while (true)
        {
            watched[0] = new PollDescriptor { Descriptor = master, Requested = UnixNative.Readable };
            watched[1] = new PollDescriptor { Descriptor = wakeup, Requested = UnixNative.Readable };

            var ready = UnixNative.Poll(watched, 2, UnixNative.WaitForever);

            if (ready < 0)
            {
                var errno = Marshal.GetLastPInvokeError();

                if (UnixErrno.ShouldRetry(errno))
                    continue;

                throw new IOException($"Waiting on the pseudo-terminal failed with errno {errno}.");
            }

            if (watched[1].Returned != 0)
                return false;

            if (watched[0].Returned != 0)
                return true;
        }
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    static WindowSize ToWindowSize(PtySize size) => new()
    {
        Rows = (ushort)size.Rows,
        Columns = (ushort)size.Columns,
    };

    readonly record struct Terminal(
        SafeFileDescriptor Master,
        SafeFileDescriptor Wakeup,
        SafeFileDescriptor WakeupSignal,
        int Child,
        int SignalTarget);

    static unsafe Terminal Spawn(PtySessionOptions options)
    {
        var arguments = EncodeArguments(options);
        var environment = UnixEnvironmentBlock.Build(
            UnixEnvironmentBlock.CaptureInherited(), options.Environment);
        var workingDirectory = Encode(options.WorkingDirectory);
        var file = Encode(options.Executable);

        SafeFileDescriptor? master = null;
        SafeFileDescriptor? slave = null;
        SafeFileDescriptor? wakeup = null;
        SafeFileDescriptor? wakeupSignal = null;

        try
        {
            byte[] slaveName;

            lock (TerminalNaming)
            {
                master = OpenMaster(options.Executable);
                slaveName = NameSlave(options.Executable, master.Descriptor);
                slave = OpenSlave(options.Executable, slaveName);
            }

            var size = ToWindowSize(options.Size);

            if (UnixNative.SetWindowSize(master.Descriptor, UnixNative.SetWindowSizeRequest, &size) != 0)
                throw HostRefused(
                    options.Executable, "the terminal size could not be set", Marshal.GetLastPInvokeError());

            (wakeup, wakeupSignal) = OpenWakeup(options.Executable);

            var child = StartChild(
                options.Executable,
                file,
                slaveName,
                workingDirectory,
                arguments,
                environment,
                [wakeup.Descriptor, wakeupSignal.Descriptor]);
            var group = UnixNative.GetProcessGroup(child);

            var terminal = new Terminal(
                master, wakeup, wakeupSignal, child, group == child ? -child : child);

            master = null;
            wakeup = null;
            wakeupSignal = null;

            return terminal;
        }
        finally
        {
            slave?.Dispose();
            master?.Dispose();
            wakeup?.Dispose();
            wakeupSignal?.Dispose();
        }
    }

    static SafeFileDescriptor OpenMaster(string executable)
    {
        var master = UnixNative.OpenPseudoTerminal(
            UnixNative.ReadWrite | UnixNative.NoControllingTerminal | UnixNative.CloseOnExec);

        if (master < 0)
            throw HostRefused(
                executable, "no pseudo-terminal could be opened", Marshal.GetLastPInvokeError());

        return new SafeFileDescriptor(master);
    }

    static unsafe byte[] NameSlave(string executable, int master)
    {
        if (UnixNative.GrantSlave(master) != 0 || UnixNative.UnlockSlave(master) != 0)
            throw HostRefused(
                executable, "the slave could not be unlocked", Marshal.GetLastPInvokeError());

        var name = UnixNative.SlaveName(master);

        if (name is null)
            throw HostRefused(
                executable, "the terminal has no slave to open", Marshal.GetLastPInvokeError());

        var found = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        var copied = new byte[found.Length + 1];
        found.CopyTo(copied);
        return copied;
    }

    static unsafe SafeFileDescriptor OpenSlave(string executable, byte[] name)
    {
        int slave;

        fixed (byte* path = name)
            slave = UnixNative.Open(path, UnixNative.ReadWrite | UnixNative.CloseOnExec);

        if (slave < 0)
            throw HostRefused(
                executable, "the slave could not be opened", Marshal.GetLastPInvokeError());

        return new SafeFileDescriptor(slave);
    }

    static unsafe (SafeFileDescriptor Wakeup, SafeFileDescriptor Signal) OpenWakeup(string executable)
    {
        var ends = stackalloc int[2];

        if (UnixNative.Pipe(ends) != 0)
            throw HostRefused(
                executable, "the teardown pipe could not be opened", Marshal.GetLastPInvokeError());

        return (new SafeFileDescriptor(ends[0]), new SafeFileDescriptor(ends[1]));
    }

    static unsafe int StartChild(
        string executable,
        byte[] file,
        byte[] slaveName,
        byte[] workingDirectory,
        byte[][] arguments,
        byte[][] environment,
        int[] closed)
    {
        var actions = stackalloc long[UnixNative.SpawnStorageBytes / sizeof(long)];
        var attributes = stackalloc long[UnixNative.SpawnStorageBytes / sizeof(long)];

        var opened = UnixNative.SpawnActionsInit(actions);

        if (opened != 0)
            throw HostRefused(executable, "the spawn's file actions could not be built", opened);

        try
        {
            opened = UnixNative.SpawnAttributesInit(attributes);

            if (opened != 0)
                throw HostRefused(executable, "the spawn's attributes could not be built", opened);

            try
            {
                Describe(executable, actions, attributes, slaveName, workingDirectory, closed);
                return StartChild(executable, actions, attributes, file, arguments, environment);
            }
            finally
            {
                UnixNative.SpawnAttributesDestroy(attributes);
            }
        }
        finally
        {
            UnixNative.SpawnActionsDestroy(actions);
        }
    }

    static unsafe void Describe(
        string executable,
        void* actions,
        void* attributes,
        byte[] slaveName,
        byte[] workingDirectory,
        int[] closed)
    {
        fixed (byte* slave = slaveName)
        fixed (byte* directory = workingDirectory)
        {
            var described = UnixNative.SpawnAttributesSetFlags(attributes, UnixNative.SpawnSetSession);

            if (described == 0)
                described = UnixNative.SpawnActionsAddOpen(actions, 0, slave, UnixNative.ReadWrite, 0);

            if (described == 0)
                described = UnixNative.SpawnActionsAddDuplicate(actions, 0, 1);

            if (described == 0)
                described = UnixNative.SpawnActionsAddDuplicate(actions, 0, 2);

            if (described == 0)
                described = UnixNative.SpawnActionsAddChangeDirectory(actions, directory);

            foreach (var descriptor in closed)
            {
                if (described == 0)
                    described = UnixNative.SpawnActionsAddClose(actions, descriptor);
            }

            if (described != 0)
                throw HostRefused(executable, "the child could not be given the terminal", described);
        }
    }

    static unsafe int StartChild(
        string executable,
        void* actions,
        void* attributes,
        byte[] file,
        byte[][] arguments,
        byte[][] environment)
    {
        var (argumentBytes, argumentStarts) = Flatten(arguments);
        var (environmentBytes, environmentStarts) = Flatten(environment);
        var argumentVector = new nint[arguments.Length + 1];
        var environmentVector = new nint[environment.Length + 1];

        fixed (byte* argumentBlock = argumentBytes)
        fixed (byte* environmentBlock = environmentBytes)
        {
            for (var i = 0; i < argumentStarts.Length; i++)
                argumentVector[i] = (nint)(argumentBlock + argumentStarts[i]);

            for (var i = 0; i < environmentStarts.Length; i++)
                environmentVector[i] = (nint)(environmentBlock + environmentStarts[i]);

            fixed (byte* path = file)
            fixed (nint* argumentPointers = argumentVector)
            fixed (nint* environmentPointers = environmentVector)
            {
                var errno = UnixNative.SpawnOnPath(
                    out var child,
                    path,
                    actions,
                    attributes,
                    (byte**)argumentPointers,
                    (byte**)environmentPointers);

                if (errno != 0)
                    throw new PtySpawnException(
                        UnixErrno.ToSpawnFailure(errno),
                        executable,
                        $"Could not start '{executable}' (errno {errno}).");

                return child;
            }
        }
    }

    static byte[][] EncodeArguments(PtySessionOptions options)
    {
        var encoded = new byte[options.Arguments.Count + 1][];
        encoded[0] = Encode(options.Executable);

        for (var i = 0; i < options.Arguments.Count; i++)
            encoded[i + 1] = Encode(options.Arguments[i]);

        return encoded;
    }

    static (byte[] Bytes, int[] Starts) Flatten(byte[][] entries)
    {
        var starts = new int[entries.Length];
        var length = 0;

        for (var i = 0; i < entries.Length; i++)
        {
            starts[i] = length;
            length += entries[i].Length;
        }

        var bytes = new byte[length];

        for (var i = 0; i < entries.Length; i++)
            entries[i].CopyTo(bytes, starts[i]);

        return (bytes, starts);
    }

    static byte[] Encode(string text)
    {
        var encoded = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, encoded);
        return encoded;
    }

    static PtySpawnException HostRefused(string executable, string what, int errno) =>
        new(PtySpawnFailure.Other,
            executable,
            $"Could not open a pseudo-terminal for '{executable}': {what} (errno {errno}).");
}
