using GitBench.Features.Commits;
using GitBench.Localization;
using ZGF.Gui.Desktop.Input;
using ZGF.Observable;

namespace GitBench.Controls.Dialogs;

/// <summary>
/// The checkable file list behind the Stash and Discard dialogs: rows sorted by path, the checked
/// set, and the Shift-range click model. A Shift-click extends from the anchor (the row of the last
/// plain click) to the clicked row and sets the range to the clicked row's toggled state; any other
/// click toggles one row and moves the anchor. An empty <paramref name="preChecked"/> checks all rows.
/// </summary>
internal sealed class CheckedFileList
{
    private readonly State<IReadOnlySet<string>> _checked;
    private int _anchorIndex = -1;

    public IReadOnlyList<FileChange> Files { get; }
    public IReadable<IReadOnlySet<string>> CheckedPaths => _checked;
    public IReadable<string> Header { get; }

    public CheckedFileList(IEnumerable<FileChange> files, IReadOnlyList<string> preChecked, Strings strings)
    {
        var rows = files.ToList();
        rows.Sort(static (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        Files = rows;

        var paths = rows.Select(r => r.Path);
        _checked = new State<IReadOnlySet<string>>(
            new HashSet<string>(preChecked.Count == 0 ? paths : paths.Intersect(preChecked)));

        Header = new Derived<string>(() => Files.Count == 0
            ? strings.LocalchangesFilesHeaderEmpty
            : strings.LocalchangesFilesHeader(_checked.Value.Count, Files.Count));
    }

    public void ClickRow(int index, InputModifiers modifiers)
    {
        if ((uint)index >= (uint)Files.Count) return;
        var next = new HashSet<string>(_checked.Value);

        if ((modifiers & InputModifiers.Shift) != 0 && (uint)_anchorIndex < (uint)Files.Count)
        {
            var targetChecked = !next.Contains(Files[index].Path);
            for (var i = Math.Min(_anchorIndex, index); i <= Math.Max(_anchorIndex, index); i++)
            {
                if (targetChecked) next.Add(Files[i].Path);
                else next.Remove(Files[i].Path);
            }
        }
        else
        {
            _anchorIndex = index;
            var path = Files[index].Path;
            if (!next.Add(path)) next.Remove(path);
        }

        _checked.Value = next;
    }

    public List<string> CheckedInOrder()
    {
        var checkedPaths = _checked.Value;
        var paths = new List<string>(checkedPaths.Count);
        foreach (var f in Files)
            if (checkedPaths.Contains(f.Path)) paths.Add(f.Path);
        return paths;
    }
}
