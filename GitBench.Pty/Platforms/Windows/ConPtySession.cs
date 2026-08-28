using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GitBench.Pty.Platforms.Windows;

/// <summary>
/// A pseudo-terminal session backed by the Windows console pseudo-console (ConPTY).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtySession : IPtySession
{
    const uint TornDownExitCode = 1;
    static readonly TimeSpan WatcherPatience = TimeSpan.FromSeconds(5);

    readonly GatedHandle<SafeFileHandle> _output;
    readonly GatedHandle<SafeFileHandle> _input;
    readonly GatedHandle<SafePseudoConsoleHandle> _pseudoConsole;
    readonly GatedHandle<SafeProcessHandle> _process;
    readonly TaskCompletionSource<PtyExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Thread _watcher;

    int _disposed;
    int _outputEnded;

    public ConPtySession(PtySessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var terminal = Spawn(options);

        _output = new GatedHandle<SafeFileHandle>(terminal.Output);
        _input = new GatedHandle<SafeFileHandle>(terminal.Input);
        _pseudoConsole = new GatedHandle<SafePseudoConsoleHandle>(terminal.PseudoConsole);
        _process = new GatedHandle<SafeProcessHandle>(terminal.Process);

        _watcher = new Thread(WatchChild)
        {
            IsBackground = true,
            Name = "gitbench-pty-child",
        };

        _watcher.Start();
    }

    public Task<PtyExit> Exited => _exit.Task;

    public unsafe int ReadOutput(Span<byte> buffer)
    {
        if (buffer.IsEmpty || Volatile.Read(ref _outputEnded) != 0)
            return 0;

        if (!_output.TryEnter(out var output))
            return EndOutput();

        try
        {
            bool succeeded;
            int read;

            fixed (byte* target = buffer)
                succeeded = WindowsNative.ReadFile(output, target, (uint)buffer.Length, out read, IntPtr.Zero);

            if (succeeded)
                return read > 0 ? read : EndOutput();

            var error = Marshal.GetLastPInvokeError();

            if (error is WindowsNative.ErrorBrokenPipe or WindowsNative.ErrorOperationAborted
                || Volatile.Read(ref _disposed) != 0)
                return EndOutput();

            throw new Win32Exception(error);
        }
        finally
        {
            _output.Leave();
        }
    }

    public unsafe void WriteInput(ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();

        if (bytes.IsEmpty)
            return;

        if (!_input.TryEnter(out var input))
        {
            ThrowIfDisposed();
            return;
        }

        try
        {
            var remaining = bytes;

            while (!remaining.IsEmpty)
            {
                bool succeeded;
                int written;

                fixed (byte* source = remaining)
                    succeeded = WindowsNative.WriteFile(input, source, (uint)remaining.Length, out written, IntPtr.Zero);

                if (!succeeded)
                {
                    var error = Marshal.GetLastPInvokeError();

                    if (error is WindowsNative.ErrorBrokenPipe or WindowsNative.ErrorNoData)
                        return;

                    throw new Win32Exception(error);
                }

                if (written <= 0)
                    return;

                remaining = remaining[written..];
            }
        }
        finally
        {
            _input.Leave();
        }
    }

    public void Resize(PtySize size)
    {
        ThrowIfDisposed();

        var requested = ToCoord(size, nameof(size));

        if (!_pseudoConsole.TryEnter(out var pseudoConsole))
        {
            ThrowIfDisposed();
            return;
        }

        try
        {
            Marshal.ThrowExceptionForHR(WindowsNative.ResizePseudoConsole(pseudoConsole, requested));
        }
        finally
        {
            _pseudoConsole.Leave();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (!_process.TryEnter(out var process))
        {
            _exit.TrySetResult(new PtyExit.TornDown());
        }
        else
        {
            try
            {
                if (WindowsNative.WaitForSingleObject(process, 0) == WindowsNative.WaitObject0)
                {
                    PublishChildExit(process);
                }
                else
                {
                    _exit.TrySetResult(new PtyExit.TornDown());
                    WindowsNative.TerminateProcess(process, TornDownExitCode);
                }
            }
            finally
            {
                _process.Leave();
            }
        }

        _input.Close();
        _pseudoConsole.Close();
        _output.Close();
        _process.Close();

        _watcher.Join(WatcherPatience);
    }

    void WatchChild()
    {
        if (_process.TryEnter(out var process))
        {
            try
            {
                if (WindowsNative.WaitForSingleObject(process, WindowsNative.Infinite) == WindowsNative.WaitObject0)
                    PublishChildExit(process);
                else
                    _exit.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError()));
            }
            finally
            {
                _process.Leave();
            }
        }

        _pseudoConsole.Close();
    }

    void PublishChildExit(SafeProcessHandle process)
    {
        if (WindowsNative.GetExitCodeProcess(process, out var code))
            _exit.TrySetResult(new PtyExit.Completed(code));
        else
            _exit.TrySetException(new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    int EndOutput()
    {
        Volatile.Write(ref _outputEnded, 1);
        return 0;
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    static Coord ToCoord(PtySize size, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size.Columns, short.MaxValue, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size.Rows, short.MaxValue, parameterName);

        return new Coord { X = (short)size.Columns, Y = (short)size.Rows };
    }

    readonly record struct Terminal(
        SafeFileHandle Output,
        SafeFileHandle Input,
        SafePseudoConsoleHandle PseudoConsole,
        SafeProcessHandle Process);

    static Terminal Spawn(PtySessionOptions options)
    {
        var commandLine = WindowsCommandLine.Build(options.Executable, options.Arguments);
        var environment = WindowsEnvironmentBlock.Build(
            WindowsEnvironmentBlock.CaptureInherited(), options.Environment);
        var size = ToCoord(options.Size, nameof(options));

        SafeFileHandle? terminalInput = null;
        SafeFileHandle? terminalOutput = null;
        SafeFileHandle? ourInput = null;
        SafeFileHandle? ourOutput = null;
        SafePseudoConsoleHandle? pseudoConsole = null;
        ProcThreadAttributeList? attributes = null;

        try
        {
            (terminalInput, ourInput) = CreatePipe(options.Executable);
            (ourOutput, terminalOutput) = CreatePipe(options.Executable);

            var result = WindowsNative.CreatePseudoConsole(
                size, terminalInput, terminalOutput, 0, out var rawPseudoConsole);

            if (result != 0)
                throw HostRefused(
                    options.Executable,
                    "CreatePseudoConsole failed",
                    Marshal.GetExceptionForHR(result) ?? new Win32Exception(result));

            pseudoConsole = new SafePseudoConsoleHandle(rawPseudoConsole);

            terminalInput.Dispose();
            terminalOutput.Dispose();

            attributes = BuildAttributes(options.Executable, rawPseudoConsole);

            var terminal = new Terminal(
                ourOutput, ourInput, pseudoConsole, StartChild(options, commandLine, environment, attributes));

            ourOutput = null;
            ourInput = null;
            pseudoConsole = null;

            return terminal;
        }
        finally
        {
            attributes?.Dispose();
            terminalInput?.Dispose();
            terminalOutput?.Dispose();
            ourInput?.Dispose();
            ourOutput?.Dispose();
            pseudoConsole?.Dispose();
        }
    }

    static ProcThreadAttributeList BuildAttributes(string executable, IntPtr pseudoConsole)
    {
        try
        {
            return ProcThreadAttributeList.ForPseudoConsole(pseudoConsole);
        }
        catch (Win32Exception refused)
        {
            throw HostRefused(executable, "the process attribute list could not be built", refused);
        }
    }

    static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(string executable)
    {
        if (!WindowsNative.CreatePipe(out var read, out var write, IntPtr.Zero, 0))
            throw HostRefused(
                executable, "CreatePipe failed", new Win32Exception(Marshal.GetLastPInvokeError()));

        return (new SafeFileHandle(read, ownsHandle: true), new SafeFileHandle(write, ownsHandle: true));
    }

    static unsafe SafeProcessHandle StartChild(
        PtySessionOptions options, string commandLine, char[] environment, ProcThreadAttributeList attributes)
    {
        var writable = new char[commandLine.Length + 1];
        commandLine.CopyTo(writable);

        fixed (char* command = writable)
        fixed (char* block = environment)
        fixed (char* workingDirectory = options.WorkingDirectory)
        {
            var startup = default(StartupInfoEx);
            startup.StartupInfo.Size = (uint)sizeof(StartupInfoEx);
            startup.AttributeList = attributes.Block;
            startup.StartupInfo.Flags = WindowsNative.StartfUseStdHandles;
            startup.StartupInfo.StdInput = IntPtr.Zero;
            startup.StartupInfo.StdOutput = IntPtr.Zero;
            startup.StartupInfo.StdError = IntPtr.Zero;

            var started = WindowsNative.CreateProcessW(
                null,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: false,
                WindowsNative.ExtendedStartupInfoPresent | WindowsNative.CreateUnicodeEnvironment,
                block,
                workingDirectory,
                &startup,
                out var information);

            if (!started)
                throw SpawnFailed(options.Executable, Marshal.GetLastPInvokeError());

            WindowsNative.CloseHandle(information.Thread);
            return new SafeProcessHandle(information.Process, ownsHandle: true);
        }
    }

    static PtySpawnException SpawnFailed(string executable, int error)
    {
        var failure = error switch
        {
            WindowsNative.ErrorFileNotFound or WindowsNative.ErrorPathNotFound =>
                PtySpawnFailure.ExecutableNotFound,
            WindowsNative.ErrorAccessDenied => PtySpawnFailure.AccessDenied,
            _ => PtySpawnFailure.Other,
        };

        return new PtySpawnException(
            failure, executable, $"Could not start '{executable}'.", new Win32Exception(error));
    }

    static PtySpawnException HostRefused(string executable, string what, Exception cause) =>
        new(PtySpawnFailure.Other,
            executable,
            $"Could not open a pseudo-terminal for '{executable}': {what}.",
            cause);
}
