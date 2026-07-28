namespace GitBench.App;

/// <summary>
/// The app's user-facing display name. Purely visual — the update identity (Velopack packId,
/// executable name, data directory) is still "GitBench" until the identity migration and must
/// not be derived from this.
/// </summary>
internal static class AppInfo
{
    public const string DisplayName = "DiffDino";
}
