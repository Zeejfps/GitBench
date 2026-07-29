using System.Text.Json;
using GitBench.Git;

namespace GitBench.Features.Assistant.Tools;

/// <summary>
/// Opens a range of one tracked file, so a question about a selection can be answered against the
/// code around it rather than the diff alone.
/// </summary>
/// <remarks>
/// Everything that decides whether a path may be opened at all lives in <see cref="RepoFileGuard"/>;
/// what is left here is how much comes back. A range, never a whole file: the model was asked about
/// a few lines, and a twenty-thousand-line answer buys nothing and costs the rest of the turn.
/// What comes back is always one contiguous run starting at <c>start_line</c>, so <c>end_line</c> is
/// the real number of the last line in <c>content</c> and every line between the two is present. Any
/// cap — line count, byte size, or the scan ceiling — shortens that run from the end and is reported
/// as <c>truncated</c>; none of them may punch a hole in the middle of it, because the model quotes
/// these numbers back to a reader who will open the file at them.
/// </remarks>
internal sealed class ReadFileTool : IAssistantTool
{
    public const int DefaultLines = 300;
    public const int MaxLines = 1200;

    // A ceiling on what one result may weigh, so a file of very long lines is capped by size even
    // when its line count is modest.
    private const int MaxBytes = 96 * 1024;

    // A file past this is not something a reviewer's question is about; the walk stops here, and
    // total_lines is then a floor rather than a count — which truncated says.
    public const int MaxScannedLines = 200_000;

    private readonly IGitService _git;
    private readonly Repo _repo;

    public ReadFileTool(IGitService git, Repo repo)
    {
        _git = git;
        _repo = repo;
    }

    public string Name => "read_file";

    public string Description =>
        "A range of lines from a tracked file in the working tree, for the code around a diff "
        + "rather than the diff itself. Repo-relative paths only, and only files git tracks: "
        + "untracked, ignored, credential-shaped and out-of-repository paths are refused. Returns "
        + "at most " + MaxLines + " lines, so ask for the range you need and page with start_line. "
        + "The lines come back contiguous from start_line: end_line is the real file line number of "
        + "the last one, and truncated means the range stopped before the end of the file.";

    public string JsonSchema =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Repo-relative path to a tracked file."},"start_line":{"type":"integer","description":"First line to return, 1-based. Default 1."},"line_count":{"type":"integer","description":"How many lines to return (1-1200, default 300)."}},"required":["path"],"additionalProperties":false}
        """;

    public bool IsWrite => false;

    public Task<ToolInvocation> InvokeAsync(JsonElement args, CancellationToken ct)
    {
        var requested = ToolJson.String(args, "path");
        var resolved = RepoFileGuard.Resolve(_git, _repo, requested);
        if (resolved.Refusal is { } refusal)
            return Task.FromResult(ToolInvocation.Error(refusal));

        var start = ToolJson.Int(args, "start_line", 1, 1, MaxScannedLines);
        var count = ToolJson.Int(args, "line_count", DefaultLines, 1, MaxLines);

        try
        {
            return Task.FromResult(Read(resolved.FullPath!, requested!.Trim().Replace('\\', '/'), start, count));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolInvocation.Error(ex.Message));
        }
    }

    private static ToolInvocation Read(string fullPath, string path, int start, int count)
    {
        if (LooksBinary(fullPath))
            return ToolInvocation.Error($"'{path}' is a binary file.");

        var lines = new List<string>(Math.Min(count, 512));
        var bytes = 0;
        var total = 0;
        var cappedBySize = false;
        var cappedByScan = false;

        foreach (var line in File.ReadLines(fullPath))
        {
            if (total == MaxScannedLines)
            {
                cappedByScan = true;
                break;
            }

            total++;
            // Once the size cap is reached the window is closed for good: skipping the long line and
            // taking the shorter ones after it would return a range with a hole in it, which reads as
            // contiguous and puts every line number after the hole wrong.
            if (cappedBySize || total < start || lines.Count >= count) continue;
            if (bytes + line.Length > MaxBytes)
            {
                cappedBySize = true;
                continue;
            }

            lines.Add(line);
            bytes += line.Length + 1;
        }

        // The first line of the window is over the ceiling on its own, so the only contiguous range
        // that fits is the empty one. Saying why beats handing back empty content.
        if (lines.Count == 0 && cappedBySize)
            return ToolInvocation.Error(
                $"Line {start} of '{path}' is longer on its own than the {MaxBytes / 1024} KB this "
                + "tool returns, so no range starting there can come back. Start after it.");

        var last = start + lines.Count - 1;
        var truncated = cappedBySize || cappedByScan || total > last;
        var body = string.Join('\n', lines);

        return ToolInvocation.Ok(ToolJson.Write(writer =>
        {
            writer.WriteString("path", path);
            writer.WriteNumber("start_line", start);
            writer.WriteNumber("end_line", last);
            writer.WriteNumber("total_lines", total);
            writer.WriteBoolean("truncated", truncated);
            writer.WriteString("content", body);
        }));
    }

    // A NUL in the first block is what git itself treats as "binary", and it keeps a compiled
    // artifact someone committed from arriving as several thousand replacement characters.
    private static bool LooksBinary(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        Span<byte> head = stackalloc byte[8000];
        var read = stream.Read(head);
        return head[..read].IndexOf((byte)0) >= 0;
    }
}
