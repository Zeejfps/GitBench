namespace GitGui;

public interface IPlatformShell
{
    string? PickFolder(string title);
    void OpenFolder(string path);
    void OpenTerminal(string path);
}
