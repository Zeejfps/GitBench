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

    public static uint Avatar(int seedHash) => Tame(Colors[Mod(seedHash, AvatarSpan)]);

    // Avatar fills sit behind fixed white text, so cap their perceived brightness: the bright
    // lane hues (yellow, spring green) darken toward the same hue until white stays legible,
    // while the already-dark hues pass through untouched. Lanes keep the full-brightness colors.
    private static uint Tame(uint color)
    {
        const float maxLuminance = 130f;
        var r = (color >> 16) & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = color & 0xFF;
        var luminance = 0.299f * r + 0.587f * g + 0.114f * b;
        if (luminance <= maxLuminance) return color;
        var scale = maxLuminance / luminance;
        return 0xFF000000u
            | (uint)(r * scale) << 16
            | (uint)(g * scale) << 8
            | (uint)(b * scale);
    }

    private static int Mod(int value, int span) => ((value % span) + span) % span;
}
