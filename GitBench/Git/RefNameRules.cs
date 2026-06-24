using GitBench.Controls.Dialogs;
using GitBench.Localization;

namespace GitBench.Git;

/// <summary>
/// A lightweight subset of git's refname rules (see <c>git check-ref-format</c>), shared by the
/// branch- and tag-name dialogs. Enough to catch the obvious typos live while a dialog is open;
/// the git invocation on submit is still the source of truth for the full rule set.
///
/// <paramref name="noun"/> is the localized ref kind named in the message ("Branch", "Tag"). Empty
/// input — and a trailing slash, which marks a still-incomplete path (e.g. a name pre-filled with a
/// folder prefix like "feature/") — are reported as neutral (<c>null</c>) rather than as an error:
/// callers gate their submit button on emptiness and <see cref="IsValid"/> (which still rejects the
/// trailing slash) separately, so the button stays disabled without a red message while the user is
/// mid-type. <see cref="IsValid"/> is the locale-independent gate; <see cref="Validate"/> produces
/// the localized message for display.
/// </summary>
internal static class RefNameRules
{
    private enum Violation { None, Spaces, LeadingDash, DoubleDot, TrailingSlash }

    public static bool IsValid(string name) => Check(name) == Violation.None;

    public static FieldStatus? Validate(string name, Strings s, string noun) => Check(name) switch
    {
        Violation.Spaces => new FieldStatus(FieldSeverity.Error, s.RefnameNoSpaces(noun)),
        Violation.LeadingDash => new FieldStatus(FieldSeverity.Error, s.RefnameNoLeadingDash(noun)),
        Violation.DoubleDot => new FieldStatus(FieldSeverity.Error, s.RefnameNoDoubleDot(noun)),
        // A trailing slash means the path isn't finished yet (e.g. a folder-prefilled name);
        // treat it as incomplete/neutral. IsValid still rejects it, so submit stays gated.
        Violation.TrailingSlash => null,
        _ => null,
    };

    private static Violation Check(string name)
    {
        if (name.Length == 0) return Violation.None;

        foreach (var c in name)
            if (char.IsWhiteSpace(c))
                return Violation.Spaces;

        if (name[0] == '-') return Violation.LeadingDash;
        if (name.Contains("..")) return Violation.DoubleDot;
        if (name[^1] == '/') return Violation.TrailingSlash;

        return Violation.None;
    }
}
