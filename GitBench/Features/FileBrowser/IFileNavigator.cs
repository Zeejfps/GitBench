namespace GitBench.Features.FileBrowser;

/// <summary>Taking the reader to a file by name — a definition jump, a usage, a search hit.</summary>
/// <remarks>
/// Only forwards. Back and forward are the content panel's, not the browser's: the trail they walk
/// runs through the terminals and the two views as well as the files.
/// </remarks>
internal interface IFileNavigator
{
    void NavigateTo(string absolutePath, int line);
}
