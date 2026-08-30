using GitBench.Terminal.Vt;

namespace GitBench.Features.Terminal;

/// <summary>
/// A hyperlink this application is willing to open.
/// </summary>
/// <remarks>
/// <para>
/// A value rather than a string, for the reason <see cref="ClipboardText"/> is one: the url came off
/// a pseudo-terminal and the difference is invisible at a call site that takes a string.
/// <see cref="FromProgram"/> is the only way in, so a raw OSC 8 payload cannot reach
/// <c>IPlatformShell.OpenUrl</c> without passing the allowlist.
/// </para>
/// <para>
/// The engine deliberately hands over the url unjudged — which schemes are openable is this
/// application's policy and not a terminal's — so this is where the parse at that boundary lives. A
/// <c>file:</c> or UNC url would launch an executable named by whatever is on the far end of the
/// pseudo-terminal, which is why the allowlist is a constructor and not a check at the call site.
/// </para>
/// <para>
/// It is also the single predicate behind every affordance. The cursor, the underline and the click
/// all ask for one of these, so a link this application will not open is given no hand cursor and no
/// highlight either, rather than looking clickable and doing nothing.
/// </para>
/// </remarks>
internal sealed record TerminalLinkTarget
{
    TerminalLinkTarget(Uri value) => Value = value;

    public Uri Value { get; }

    /// <summary>The url as the program wrote it, for showing someone before they follow it.</summary>
    public string Text => Value.OriginalString;

    /// <summary>A link a program sent through OSC 8. Null unless it is an absolute http(s) url.</summary>
    public static TerminalLinkTarget? FromProgram(TerminalHyperlink link)
    {
        if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri)) return null;

        return uri.Scheme is "http" or "https" ? new TerminalLinkTarget(uri) : null;
    }

    public override string ToString() => Text;
}
