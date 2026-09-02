using System.Text;
using GitBench.Lsp;
using GitBench.Lsp.Documents;

namespace GitBench.Features.LanguageServers;

/// <summary>
/// What one hover card says. The problems on the line come first and the symbol's type second,
/// because a reader who stopped on a squiggle stopped for the problem; the two are separated so it
/// is never ambiguous which half is the server describing the code and which is it complaining
/// about it.
/// </summary>
internal static class HoverCardText
{
    private const string SectionBreak = "\n\n---\n\n";

    public static HoverText? Compose(IReadOnlyList<Diagnostic> diagnostics, HoverText? hover)
    {
        if (diagnostics.Count == 0) return hover;

        var text = new StringBuilder();
        foreach (var diagnostic in diagnostics)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append(Line(diagnostic));
        }

        if (hover is { Markdown.Length: > 0 }) text.Append(SectionBreak).Append(hover.Markdown);
        return new HoverText(text.ToString());
    }

    private static string Line(Diagnostic diagnostic)
    {
        var text = new StringBuilder("**").Append(Label(diagnostic.Severity)).Append("** ");
        text.Append(diagnostic.Message.Trim());

        var attribution = Attribution(diagnostic);
        if (attribution.Length > 0) text.Append(" *(").Append(attribution).Append(")*");
        return text.ToString();
    }

    private static string Attribution(Diagnostic diagnostic) => (diagnostic.Source, diagnostic.Code) switch
    {
        (null or "", null or "") => string.Empty,
        (null or "", var code) => code!,
        (var source, null or "") => source!,
        var (source, code) => $"{source} {code}",
    };

    private static string Label(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Warning => "Warning",
        DiagnosticSeverity.Information => "Info",
        DiagnosticSeverity.Hint => "Hint",
        _ => "Error",
    };
}
