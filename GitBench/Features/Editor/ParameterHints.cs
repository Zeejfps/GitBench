using GitBench.Features.LanguageServers;
using GitBench.Lsp;
using GitBench.Lsp.Documents;
using ZGF.Observable;

namespace GitBench.Features.Editor;

/// <summary>
/// Parameter info over the call the caret is in: opened on request or on a character the server
/// names, asked again whenever the text or the caret moves while it shows, and closed the moment
/// the server says the caret is in no call. The server is the only judge of where a call ends; the
/// text is only consulted for which argument the caret is in, where the server does not say.
/// </summary>
internal sealed class ParameterHints : IDisposable
{
    private readonly ISignatureHelpSource _source;
    private readonly ProbeSlot _slot;
    private readonly Action<SignatureHelp?> _present;
    private readonly Func<TextPosition, int?> _argumentAt;

    /// <param name="argumentAt">Which argument a position is in, counted from the text, for a
    /// server that answers with overloads but never says which parameter the caret is in.</param>
    public ParameterHints(
        ISignatureHelpSource source, IUiDispatcher dispatcher, Action<SignatureHelp?> present,
        Func<TextPosition, int?> argumentAt)
    {
        _source = source;
        _slot = new ProbeSlot(dispatcher);
        _present = present;
        _argumentAt = argumentAt;
    }

    /// <summary>Whether it is showing, or has been asked for and not yet answered.</summary>
    public bool IsOpen { get; private set; }

    public void Invoke(string path, TextPosition caret)
    {
        if (!_source.CanHelpWithSignatures(path)) return;
        IsOpen = true;
        Ask(path, caret, SignatureAsk.Invoked);
    }

    /// <summary>After a character lands: opens on one the server names, and otherwise keeps an open
    /// one current.</summary>
    public void Typed(string path, TextPosition caret, char typed)
    {
        if (_source.SignatureTriggers(path) is SignatureHelpSupport.Offered(var triggers, _) && triggers.Contains(typed))
        {
            IsOpen = true;
            Ask(path, caret, new SignatureAsk.TypedTrigger(typed));
            return;
        }

        Moved(path, caret);
    }

    /// <summary>After the caret or the text moved some other way.</summary>
    public void Moved(string path, TextPosition caret)
    {
        if (IsOpen) Ask(path, caret, SignatureAsk.Following);
    }

    public void Close()
    {
        _slot.Cancel();
        if (!IsOpen) return;
        IsOpen = false;
        _present(null);
    }

    public void Dispose() => _slot.Dispose();

    private void Ask(string path, TextPosition caret, SignatureAsk ask) =>
        _slot.Ask(
            TimeSpan.Zero,
            cancel => _source.SignatureHelpAsync(path, caret.Line, caret.Column, ask, cancel),
            reply =>
            {
                if (!IsOpen) return;
                if (reply is not SignatureReply.Answered { Help.Signatures.Count: > 0 } answered)
                {
                    Close();
                    return;
                }

                var help = answered.Help;
                if (!help.NamesActiveParameter && _argumentAt(caret) is { } counted)
                    help = help with { ActiveParameter = counted };
                _present(help);
            });
}
