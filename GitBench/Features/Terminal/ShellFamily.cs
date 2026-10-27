namespace GitBench.Features.Terminal;

/// <summary>Which language the shell in the pane speaks, for the one caller that has to write a
/// command into it rather than a keystroke.</summary>
internal enum ShellFamily
{
    Posix,
    PowerShell,
    CommandProcessor,
}
