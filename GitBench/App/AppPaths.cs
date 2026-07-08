namespace GitBench.App;

internal static class AppPaths
{
    // GITBENCH_DATA_DIR points a run at an alternate data folder (e.g. a scratch dir for testing
    // first-run flows) without touching the real per-user state. Missing files load as defaults
    // and the stores create the directory on first write, so the folder need not exist.
    public static string AppDataPath(string fileName)
    {
        var root = Environment.GetEnvironmentVariable("GITBENCH_DATA_DIR");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitBench");
        return Path.Combine(root, fileName);
    }
}
