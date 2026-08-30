namespace GitBench.Terminal.Vt;

/// <summary>Which of the terminal's clipboards an OSC 52 sequence addressed.</summary>
public enum ClipboardTarget : byte
{
    /// <summary>The system clipboard, <c>Pc</c> of <c>c</c> — and the default when none was named.</summary>
    Clipboard = 0,

    /// <summary>The primary selection, <c>Pc</c> of <c>p</c> or <c>s</c>.</summary>
    Primary = 1,
}

/// <summary>
/// A program asking the terminal to put text on a clipboard, through OSC 52.
/// </summary>
/// <remarks>
/// <para>
/// Only the write half reaches this far. A read — OSC 52 with a payload of <c>?</c> — lets any
/// program running in the pane take whatever the user last copied, so the engine answers it with an
/// empty clipboard and never surfaces it. There is deliberately no request case for a read: what
/// cannot be constructed cannot be wired up later by accident.
/// </para>
/// <para>
/// The text is already decoded and already known to be valid. A payload that is not base64, or that
/// is longer than a clipboard has any business being, produces no request at all — the sequence did
/// not happen, rather than a request carrying something a caller has to check.
/// </para>
/// </remarks>
public readonly record struct TerminalClipboardRequest(ClipboardTarget Target, string Text);
