using GitBench.Features.Diff;
using GitBench.Features.FileBrowser;
using GitBench.Features.Repos;
using GitBench.Localization;
using GitBench.Widgets;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.LanguageServers;

/// <summary>Answers "where is this used?" for a symbol, and takes the reader to the answer they
/// pick.</summary>
internal interface IUsagesPresenter
{
    void ShowUsagesOf(PointF anchor, FileLine line, RawColumn column);
}

/// <summary>
/// Shows a symbol's usages as a searchable menu and navigates to the one picked. A single usage
/// never opens the menu — the reader asked to be taken somewhere, and there is only one somewhere —
/// and no usages opens nothing, because an empty popup is a thing to dismiss rather than an answer.
/// </summary>
internal sealed class UsagesPopup : IUsagesPresenter, IDisposable
{
    private readonly Context _context;
    private readonly IReferenceSource _servers;
    private readonly IFileNavigator _navigator;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<(string Root, string Path)?> _document;
    private readonly Func<string, IReadOnlyList<string>?> _readLines;

    private CancellationTokenSource? _pending;

    public UsagesPopup(
        Context context,
        IReferenceSource servers,
        IFileNavigator navigator,
        IUiDispatcher dispatcher,
        Func<(string Root, string Path)?> document,
        Func<string, IReadOnlyList<string>?>? readLines = null)
    {
        _context = context;
        _servers = servers;
        _navigator = navigator;
        _dispatcher = dispatcher;
        _document = document;
        _readLines = readLines ?? ReadLines;
    }

    public void ShowUsagesOf(PointF anchor, FileLine line, RawColumn column)
    {
        if (_document() is not { } document) return;
        if (!_servers.CanReference(document.Path)) return;

        Cancel();
        var cancel = new CancellationTokenSource();
        _pending = cancel;
        var token = cancel.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await _servers
                    .ReferencesAsync(document.Path, line, column, token)
                    .ConfigureAwait(false);
                if (token.IsCancellationRequested) return;
                // Nobody could be asked. Opening an empty popup would say the symbol is unused,
                // which is a different thing and not one this knows.
                if (reply is not ReferenceReply.Answered answered) return;

                // Off the UI thread on purpose: this opens up to a hundred files, and the answer is
                // already late enough that a frozen frame while it lands would be the visible part.
                var usages = Usages.From(document.Root, answered.Sites, _readLines);

                _dispatcher.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (_document() is not { } still || still.Path != document.Path) return;
                    Present(anchor, usages);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LanguageServers] find usages failed: {ex.Message}");
            }
        }, token);
    }

    private void Present(PointF anchor, UsageList usages)
    {
        var sites = Usages.SitesOf(usages);
        if (sites.Count == 0) return;
        if (sites.Count == 1)
        {
            _navigator.NavigateTo(sites[0].AbsolutePath, sites[0].Line);
            return;
        }

        var strings = _context.Localization().Strings.Value;
        RepoBarContextMenu.ShowSearchable(
            _context, anchor, Rows(usages, strings), strings.UsagesFilter, strings.UsagesNoMatches);
    }

    private IReadOnlyList<RepoBarContextMenu.Item> Rows(UsageList usages, Strings strings)
    {
        // Snapshot rather than bound: the menu is a popup the next click closes, and it has no
        // second frame to re-theme in.
        var dim = _context.Theme().Styles.Value.ContextMenu.ItemTextDisabled;

        var sites = Usages.SitesOf(usages);
        var rows = new List<RepoBarContextMenu.Item>(sites.Count + 2);
        foreach (var site in sites) rows.Add(Row(site, dim));

        if (usages is UsageList.Capped capped)
        {
            // Last rather than first: the searchable menu commits its topmost visible row on Enter,
            // and a notice standing there would swallow that keypress on a row that goes nowhere.
            rows.Add(RepoBarContextMenu.Separator);
            rows.Add(new RepoBarContextMenu.Item(
                strings.UsagesCapped(capped.Sites.Count, capped.Total),
                static () => { },
                Enabled: false));
        }

        return rows;
    }

    private RepoBarContextMenu.Item Row(UsageSite site, uint dim)
    {
        var source = SourceOf(site.Text);
        // The label is what the filter box matches on, so it carries both halves even though the
        // row is drawn from the segments: a reader types a file name or a fragment of code.
        var label = source.Length == 0 ? site.Where : $"{site.Where}  {source}";
        return new RepoBarContextMenu.Item(
            label,
            () => _navigator.NavigateTo(site.AbsolutePath, site.Line),
            LabelSegments: source.Length == 0
                ? [new MenuLabelSegment(site.Where, dim)]
                : [new MenuLabelSegment(site.Where + "  ", dim), new MenuLabelSegment(source)]);
    }

    private static string SourceOf(UsageText text) => text switch
    {
        UsageText.Source source => source.Text,
        UsageText.Unreadable => string.Empty,
        _ => throw new NotSupportedException($"unhandled usage text {text.GetType().Name}"),
    };

    /// <summary>The lines of a file, or null when there are none to be had.</summary>
    private static IReadOnlyList<string>? ReadLines(string absolutePath)
    {
        try
        {
            return File.ReadAllLines(absolutePath);
        }
        catch (Exception)
        {
            // Every failure here means the same thing to the row that wanted the text — deleted
            // since the server indexed it, locked, or not readable by this process — and none of
            // them makes the location any less true, so the row survives without its source line.
            return null;
        }
    }

    private void Cancel()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }

    public void Dispose() => Cancel();
}
