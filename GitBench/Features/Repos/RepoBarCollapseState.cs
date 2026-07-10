using GitBench.App;
using ZGF.Observable;

namespace GitBench.Features.Repos;

/// <summary>
/// Whether the repo sidebar shows the full bar or the compact icon rail. Shared by the bar's
/// collapse button, the rail's expand button, and the sidebar host that swaps between them;
/// the choice persists through preferences.
/// </summary>
public sealed class RepoBarCollapseState
{
    private readonly State<bool> _collapsed;
    private readonly PreferencesService _preferences;

    public RepoBarCollapseState(PreferencesService preferences)
    {
        _preferences = preferences;
        _collapsed = new State<bool>(preferences.Current.RepoBarCollapsed);
    }

    public IReadable<bool> IsCollapsed => _collapsed;

    public void Toggle()
    {
        _collapsed.Value = !_collapsed.Value;
        _preferences.SetRepoBarCollapsed(_collapsed.Value);
    }
}
