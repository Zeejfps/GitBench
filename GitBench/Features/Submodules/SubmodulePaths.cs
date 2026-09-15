namespace GitBench.Features.Submodules;

internal static class SubmodulePaths
{
    /// <summary>The submodule's path as git names it: relative to the parent root, forward-slashed.</summary>
    public static string Relative(string parentRoot, string submoduleAbs) =>
        Path.GetRelativePath(parentRoot, submoduleAbs).Replace('\\', '/').TrimEnd('/');
}
