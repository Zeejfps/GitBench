namespace GitBench.Features.Assistant.Backend;

/// <summary>
/// Which of the assistant's jobs a turn is doing. Each role is given its own model in settings,
/// on whichever provider has a key, so the chat, the review and the walkthrough narrator can run
/// on three different models from three different providers.
/// </summary>
internal enum AssistantRole
{
    /// <summary>The conversation a repository's session keeps. Nothing asks it anything since the
    /// chat moved to ACP agents; kept so saved settings still read.</summary>
    General,

    /// <summary>The commit bar's "Generate commit message". Its own role because it is the one job
    /// small and frequent enough to want a cheaper model than the rest.</summary>
    CommitMessage,

    /// <summary>What the commit bar's "Review changes" ran on before it went to the agent; kept so
    /// saved settings still read.</summary>
    Review,

    /// <summary>The review window's narrator.</summary>
    Walkthrough,
}

internal static class AssistantRoles
{
    public static IReadOnlyList<AssistantRole> All { get; } =
        [AssistantRole.General, AssistantRole.CommitMessage, AssistantRole.Review, AssistantRole.Walkthrough];

    /// <summary>The name a role is stored and declared under — in the preferences file and in an
    /// agent prompt's header.</summary>
    public static string Id(AssistantRole role) => role switch
    {
        AssistantRole.General => "general",
        AssistantRole.CommitMessage => "commit-message",
        AssistantRole.Review => "review",
        AssistantRole.Walkthrough => "walkthrough",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    /// <summary>The role stored under this name, or null for a name this build does not know.</summary>
    public static AssistantRole? Parse(string? id)
    {
        foreach (var role in All)
            if (string.Equals(Id(role), id, StringComparison.OrdinalIgnoreCase))
                return role;
        return null;
    }
}
