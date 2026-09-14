using GitBench.Features.Diff;
using ZGF.Observable;

namespace GitBench.Features.Review.Walkthrough;

/// <summary>One line of one file on one side of the review diff, in file coordinates. Rows shift
/// when a gap expands or a file folds; file lines do not, so this is what a narrator addresses.</summary>
internal readonly record struct ReviewLineRef(string Path, DiffLineSide Side, FileLine Line);

/// <summary>A range of lines a narrator lit up, numbered by its position in the set, with the note
/// the rail lists beside that number.</summary>
internal sealed record ReviewSpotlight(string Path, DiffLineSide Side, FileLine From, FileLine To, string? Note = null)
{
    public bool Contains(DiffLineSide side, FileLine line) => side == Side && From <= line && line <= To;
}

/// <summary>What a focus or spotlight landed on. The text comes back so a narrator that guessed a
/// line number wrong sees it in the tool result instead of the reviewer seeing a pin on the wrong
/// line.</summary>
internal abstract record ReviewLineResolution
{
    /// <summary>The line is on screen; <paramref name="Text"/> is its content as the diff shows it.</summary>
    public sealed record Resolved(ReviewLineRef Line, string Text) : ReviewLineResolution;

    /// <summary>The file is in the review but the diff holds no such line on that side — behind no
    /// gap that could be expanded, past the end, or a number the other side owns. The neighbours
    /// are the nearest lines the diff does hold, so the caller can correct itself.</summary>
    public sealed record NotInDiff(ReviewLineRef Line, FileLine? NearestBefore, FileLine? NearestAfter) : ReviewLineResolution;

    /// <summary>No file at that path in this review.</summary>
    public sealed record NoSuchFile(string Path) : ReviewLineResolution;

    /// <summary>The file's diff could not be shown (binary, load error, load timed out).</summary>
    public sealed record Unavailable(string Path, string Reason) : ReviewLineResolution;
}

/// <summary>A stop on a guided walkthrough: what to say, where to look, and what to light up.</summary>
internal sealed record WalkthroughStep(
    string Title,
    string BodyMarkdown,
    ReviewLineRef? Focus,
    IReadOnlyList<ReviewSpotlight> Spotlights,
    bool Dim = false);

/// <summary>
/// What a narrator — an MCP session or the built-in assistant — can do to one review window's
/// diff surface. Implemented by <see cref="ReviewWindowViewModel"/>; the stacked diff list services
/// it. Every member is UI-thread only; tools hop over via <c>AssistantWriteSurface.OnUiThreadAsync</c>.
/// </summary>
internal interface IReviewPresentation
{
    /// <summary>Activates the file and scrolls so the line sits about a third of the way down the
    /// viewport, waiting for the file's diff to load first if it has not, and expanding a collapsed
    /// gap that hides it. Completes once the line is laid out (or with why it cannot be).</summary>
    Task<ReviewLineResolution> FocusLineAsync(ReviewLineRef line, CancellationToken ct);

    /// <summary>Activates the file and scrolls its header onto the pin line, unfolding it.</summary>
    Task<ReviewLineResolution> FocusFileAsync(string path, CancellationToken ct);

    /// <summary>Replaces the spotlight set. <paramref name="dim"/> washes out the rest of the spotlit
    /// files' sections — never other files. Resolves each range to the text it covers.</summary>
    Task<IReadOnlyList<ReviewLineResolution>> SetSpotlightsAsync(IReadOnlyList<ReviewSpotlight> spotlights, bool dim, CancellationToken ct);

    void ClearSpotlights();

    IReadable<IReadOnlyList<ReviewSpotlight>> Spotlights { get; }
    IReadable<bool> SpotlightDim { get; }

    /// <summary>The reviewer's current text selection in the stacked diff, as the quote the
    /// "Explain this selection" menu sends; null when nothing is selected.</summary>
    IReadable<DiffSelectionQuote?> Selection { get; }
}
