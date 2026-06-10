namespace GitBench.App;

internal static class AppPaths
{
    public static string AppDataPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GitBench", fileName);
}
