namespace GitBench.Git;

/// <summary>Which of <see cref="RefNameRules"/>' rules a name breaks, if any.</summary>
internal enum RefNameViolation { None, Spaces, LeadingDash, DoubleDot, TrailingSlash }

/// <summary>
/// A lightweight subset of git's refname rules (see <c>git check-ref-format</c>), shared by the
/// branch- and tag-name dialogs. Enough to catch the obvious typos live while a dialog is open;
/// the git invocation on submit is still the source of truth for the full rule set.
///
/// Empty input — and a trailing slash, which marks a still-incomplete path (e.g. a name pre-filled
/// with a folder prefix like "feature/") — are not errors to show: callers gate their submit button
/// on emptiness and <see cref="IsValid"/> (which still rejects the trailing slash) separately, so
/// the button stays disabled without a red message while the user is mid-type.
/// <see cref="IsValid"/> is the locale-independent gate; the message for display is
/// <c>RefNameMessages.Validate</c>'s, built from <see cref="Check"/>.
/// </summary>
internal static class RefNameRules
{
    public static bool IsValid(string name) => Check(name) == RefNameViolation.None;

    public static RefNameViolation Check(string name)
    {
        if (name.Length == 0) return RefNameViolation.None;

        foreach (var c in name)
            if (char.IsWhiteSpace(c))
                return RefNameViolation.Spaces;

        if (name[0] == '-') return RefNameViolation.LeadingDash;
        if (name.Contains("..")) return RefNameViolation.DoubleDot;
        if (name[^1] == '/') return RefNameViolation.TrailingSlash;

        return RefNameViolation.None;
    }
}
