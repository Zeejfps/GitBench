namespace GitBench.Theming;

/// <summary>
/// Vivid identity hues for commit-graph lanes and author avatars. These encode identity
/// (which lane, which author) rather than surface or role, so they stay constant across light
/// and dark — unlike the role colors in <see cref="ThemePalette"/>. Avatars draw from the first
/// <see cref="AvatarSpan"/> hues; lanes cycle through the full set.
/// </summary>
public static class CategoricalPalette
{
    private static readonly uint[] Colors =
    {
        0xFF5865F2,
        0xFFEB459E,
        0xFF57F287,
        0xFFFEE75C,
        0xFFED4245,
        0xFF9B59B6,
        0xFFE67E22,
        0xFF1ABC9C,
        0xFF3498DB,
        0xFFE91E63,
        0xFF2ECC71,
        0xFFF1C40F,
    };

    private const int AvatarSpan = 8;

    public static uint Lane(int lane) => Colors[Mod(lane, Colors.Length)];

    public static uint Avatar(int seedHash) => Colors[Mod(seedHash, AvatarSpan)];

    private static int Mod(int value, int span) => ((value % span) + span) % span;
}
