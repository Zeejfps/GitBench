namespace GitBench.App;

/// <summary>
/// Resolves the per-user data folder, seeding it once from the folder a previously-named build
/// wrote to. <see cref="AppIdentity.DataDirEnvVar"/> points a run at an alternate folder (e.g. a
/// scratch dir for testing first-run flows) without touching real per-user state. Missing files
/// load as defaults and the stores create the directory on first write, so the folder need not
/// exist.
/// </summary>
internal static class AppPaths
{
    private static readonly Lazy<string> Root = new(() => ResolveRoot(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)));

    public static string AppDataPath(string fileName) => Path.Combine(Root.Value, fileName);

    /// <summary>
    /// An explicit override is taken verbatim and never migrated into — the caller named that
    /// folder and owns what is in it.
    /// </summary>
    internal static string ResolveRoot(Func<string, string?> readEnvVar, string appDataRoot)
    {
        if (ReadOverride(readEnvVar) is { } overridden) return overridden;

        var root = Path.Combine(appDataRoot, AppIdentity.DataFolderName);
        if (!Directory.Exists(root)) SeedFromLegacy(root, appDataRoot);
        return root;
    }

    private static string? ReadOverride(Func<string, string?> readEnvVar)
    {
        var current = readEnvVar(AppIdentity.DataDirEnvVar);
        if (!string.IsNullOrWhiteSpace(current)) return current;

        foreach (var legacy in AppIdentity.LegacyDataDirEnvVars)
        {
            var value = readEnvVar(legacy);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    /// <summary>
    /// Copies rather than moves, so rolling back to a build from before the rename still finds its
    /// state; the two folders then diverge, which is the accepted cost of that rollback. Staged
    /// through a temp folder and moved into place, so a copy that dies part-way leaves no partial
    /// folder to be mistaken for migrated state — the next launch simply tries again.
    /// </summary>
    private static void SeedFromLegacy(string root, string appDataRoot)
    {
        foreach (var name in AppIdentity.LegacyDataFolderNames)
        {
            var legacy = Path.Combine(appDataRoot, name);
            if (!Directory.Exists(legacy)) continue;

            var staging = root + ".migrating-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyDirectory(legacy, staging);
                Directory.Move(staging, root);
            }
            catch
            {
                TryDelete(staging);
            }

            // The newest legacy folder is the only candidate: older ones are further behind.
            return;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A stray staging folder is inert; failing the launch over it would be worse.
        }
    }
}
