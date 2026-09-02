namespace GitBench.Features.FileBrowser;

internal interface IFileNavigator
{
    void NavigateTo(string absolutePath, int line);

    void GoBack();
}
