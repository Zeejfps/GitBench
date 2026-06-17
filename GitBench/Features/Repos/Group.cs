using ZGF.Observable;

namespace GitBench.Features.Repos;

// A named, collapsible group of repos. A live entity mutated in place (rename, collapse,
// reorder) rather than a value replaced wholesale, so views bound to its state update without
// the section being torn down and rebuilt.
public sealed class Group
{
    public Guid Id { get; }
    public State<string> Name { get; }
    public State<bool> IsCollapsed { get; }
    public ObservableList<Guid> RepoIds { get; }

    public Group(Guid id, string name, bool isCollapsed, IEnumerable<Guid> repoIds)
    {
        Id = id;
        Name = new State<string>(name);
        IsCollapsed = new State<bool>(isCollapsed);
        RepoIds = new ObservableList<Guid>();
        foreach (var repoId in repoIds) RepoIds.Add(repoId);
    }

    public GroupState ToState() => new(Id, Name.Value, IsCollapsed.Value, RepoIds.ToList());

    public static Group FromState(GroupState s) => new(s.Id, s.Name, s.IsCollapsed, s.RepoIds);
}

// Serializable snapshot of a Group for the on-disk repo state.
public sealed record GroupState(Guid Id, string Name, bool IsCollapsed, List<Guid> RepoIds);
