namespace GitBench.App;

/// <summary>
/// Every name the app answers to. The display name is free to change with the brand, and the data
/// folder carries its own history so a renamed build still finds the state the previous one wrote.
/// Velopack's update identity — packId, bundleId, executable name — is deliberately absent: it is
/// fixed at pack time and must not be derived from anything here. See
/// <c>docs/plans/rename-safe-identity.md</c> for what a rename does and does not cost.
/// </summary>
internal static class AppIdentity
{
    public const string DisplayName = "DiffDino";

    public const string DataFolderName = "DiffDino";
    public const string DataDirEnvVar = "DIFFDINO_DATA_DIR";

    /// <summary>
    /// Names this app has previously stored data under, newest first. A rename prepends the
    /// outgoing name; entries are never removed, so an install that skipped several versions still
    /// migrates from whichever folder it actually has.
    /// </summary>
    public static readonly string[] LegacyDataFolderNames = ["GitBench"];

    /// <inheritdoc cref="LegacyDataFolderNames"/>
    public static readonly string[] LegacyDataDirEnvVars = ["GITBENCH_DATA_DIR"];
}
