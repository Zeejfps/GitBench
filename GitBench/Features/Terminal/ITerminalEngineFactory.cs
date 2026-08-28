using GitBench.Terminal.Vt;
using GitBench.Terminal.Vt.Adapters;

namespace GitBench.Features.Terminal;

/// <summary>
/// Makes the engine a terminal session parses its output with.
/// </summary>
/// <remarks>
/// The one thing standing between the pane and a named engine. It keeps the choice of engine at the
/// composition root, where swapping it is a registration, and it lets a test drive the session's
/// read pump against a scripted engine instead of a real one.
/// </remarks>
internal interface ITerminalEngineFactory
{
    ITerminalEngine Create(TerminalSetup setup);
}

internal sealed class XtermSharpEngineFactory : ITerminalEngineFactory
{
    public ITerminalEngine Create(TerminalSetup setup) => new XtermSharpEngine(setup);
}
