using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Observable;

namespace GitGui;

public enum DragKind { Repo, Group }

public sealed class DragSession
{
    public required DragKind Kind { get; init; }
    public Repo? Repo { get; init; }
    public Group? Group { get; init; }
    public PointF MousePosition { get; set; }
}

public sealed class DropTarget
{
    public required DragKind Kind { get; init; }
    public Guid? GroupId { get; init; }
    public required int InsertIndex { get; init; }
    public required RectF IndicatorBounds { get; init; }
}

public interface IDragController
{
    State<DragSession?> Session { get; }
    State<DropTarget?> Target { get; }
    void StartRepoDrag(Repo source, PointF mouse);
    void StartGroupDrag(Group source, PointF mouse);
    void UpdateDrag(PointF mouse);
    void CompleteDrag();
    void CancelDrag();
    void RegisterRepoRow(View view, Guid groupId, Guid repoId);
    void RegisterGroupHeader(View view, Guid groupId);
    void RegisterGroupSection(View view, Guid groupId);
    void Unregister(View view);
}

public sealed class DragController : IDragController
{
    private enum TargetKind { Repo, Header, Section }

    private sealed record Registration(TargetKind Kind, Guid GroupId, Guid RepoId);

    private readonly IRepoRegistry _registry;
    private readonly Dictionary<View, Registration> _registrations = new();

    public DragController(IRepoRegistry registry)
    {
        _registry = registry;
    }

    public State<DragSession?> Session { get; } = new(null);
    public State<DropTarget?> Target { get; } = new(null);

    public void StartRepoDrag(Repo source, PointF mouse)
    {
        Session.Value = new DragSession { Kind = DragKind.Repo, Repo = source, MousePosition = mouse };
        Target.Value = null;
    }

    public void StartGroupDrag(Group source, PointF mouse)
    {
        Session.Value = new DragSession { Kind = DragKind.Group, Group = source, MousePosition = mouse };
        Target.Value = null;
    }

    public void UpdateDrag(PointF mouse)
    {
        var session = Session.Value;
        if (session is null) return;
        session.MousePosition = mouse;
        Target.Value = session.Kind switch
        {
            DragKind.Repo => ResolveRepoTarget(mouse, session.Repo!.Id),
            DragKind.Group => ResolveGroupTarget(mouse, session.Group!.Id),
            _ => null,
        };
    }

    public void CompleteDrag()
    {
        var session = Session.Value;
        var target = Target.Value;
        Session.Value = null;
        Target.Value = null;
        if (session is null || target is null) return;
        switch (session.Kind)
        {
            case DragKind.Repo when target.GroupId is { } gid:
                _registry.MoveRepo(session.Repo!.Id, gid, target.InsertIndex);
                break;
            case DragKind.Group:
                _registry.MoveGroup(session.Group!.Id, target.InsertIndex);
                break;
        }
    }

    public void CancelDrag()
    {
        Session.Value = null;
        Target.Value = null;
    }

    public void RegisterRepoRow(View view, Guid groupId, Guid repoId)
        => _registrations[view] = new Registration(TargetKind.Repo, groupId, repoId);

    public void RegisterGroupHeader(View view, Guid groupId)
        => _registrations[view] = new Registration(TargetKind.Header, groupId, Guid.Empty);

    public void RegisterGroupSection(View view, Guid groupId)
        => _registrations[view] = new Registration(TargetKind.Section, groupId, Guid.Empty);

    public void Unregister(View view) => _registrations.Remove(view);

    private DropTarget? ResolveRepoTarget(PointF mouse, Guid sourceRepoId)
    {
        foreach (var (view, reg) in _registrations)
        {
            if (reg.Kind != TargetKind.Repo) continue;
            if (!view.Position.ContainsPoint(mouse)) continue;
            var pos = view.Position;
            var midY = pos.Bottom + pos.Height * 0.5f;
            var insertAbove = mouse.Y > midY;
            var group = FindGroup(reg.GroupId);
            if (group is null) return null;
            var currentIndex = group.RepoIds.IndexOf(reg.RepoId);
            if (currentIndex < 0) return null;
            var insertIndex = insertAbove ? currentIndex : currentIndex + 1;
            var indicatorY = insertAbove ? pos.Top : pos.Bottom;
            return new DropTarget
            {
                Kind = DragKind.Repo,
                GroupId = reg.GroupId,
                InsertIndex = insertIndex,
                IndicatorBounds = new RectF(pos.Left, indicatorY - 1, pos.Width, 2),
            };
        }

        foreach (var (view, reg) in _registrations)
        {
            if (reg.Kind != TargetKind.Header) continue;
            if (!view.Position.ContainsPoint(mouse)) continue;
            var pos = view.Position;
            return new DropTarget
            {
                Kind = DragKind.Repo,
                GroupId = reg.GroupId,
                InsertIndex = 0,
                IndicatorBounds = new RectF(pos.Left, pos.Bottom - 1, pos.Width, 2),
            };
        }

        foreach (var (view, reg) in _registrations)
        {
            if (reg.Kind != TargetKind.Section) continue;
            if (!view.Position.ContainsPoint(mouse)) continue;
            var group = FindGroup(reg.GroupId);
            if (group is null) continue;
            var pos = view.Position;
            return new DropTarget
            {
                Kind = DragKind.Repo,
                GroupId = reg.GroupId,
                InsertIndex = group.RepoIds.Count,
                IndicatorBounds = new RectF(pos.Left, pos.Bottom - 1, pos.Width, 2),
            };
        }

        return null;
    }

    private DropTarget? ResolveGroupTarget(PointF mouse, Guid sourceGroupId)
    {
        var ordered = new List<(int index, View view)>();
        for (var i = 0; i < _registry.Groups.Count; i++)
        {
            var groupId = _registry.Groups[i].Id;
            View? sectionView = null;
            foreach (var (view, reg) in _registrations)
            {
                if (reg.Kind != TargetKind.Section) continue;
                if (reg.GroupId != groupId) continue;
                sectionView = view;
                break;
            }
            if (sectionView is null) continue;
            ordered.Add((i, sectionView));
        }
        if (ordered.Count == 0) return null;

        var sourceIndex = -1;
        for (var i = 0; i < _registry.Groups.Count; i++)
        {
            if (_registry.Groups[i].Id != sourceGroupId) continue;
            sourceIndex = i;
            break;
        }
        if (sourceIndex < 0) return null;

        int? insertIndex = null;
        RectF indicator = default;
        for (var i = 0; i < ordered.Count; i++)
        {
            var (groupIdx, sectionView) = ordered[i];
            var pos = sectionView.Position;
            if (mouse.Y > pos.Top) continue;
            if (mouse.Y < pos.Bottom) continue;

            var midY = pos.Bottom + pos.Height * 0.5f;
            if (mouse.Y > midY)
            {
                insertIndex = groupIdx;
                indicator = new RectF(pos.Left, pos.Top - 1, pos.Width, 2);
            }
            else
            {
                insertIndex = groupIdx + 1;
                indicator = new RectF(pos.Left, pos.Bottom - 1, pos.Width, 2);
            }
            break;
        }

        if (insertIndex is null)
        {
            var last = ordered[^1];
            var lastBottom = last.view.Position.Bottom;
            if (mouse.Y < lastBottom)
            {
                insertIndex = _registry.Groups.Count;
                indicator = new RectF(last.view.Position.Left, lastBottom - 1, last.view.Position.Width, 2);
            }
        }

        if (insertIndex is null) return null;
        if (insertIndex == sourceIndex || insertIndex == sourceIndex + 1) return null;

        return new DropTarget
        {
            Kind = DragKind.Group,
            InsertIndex = insertIndex.Value,
            IndicatorBounds = indicator,
        };
    }

    private Group? FindGroup(Guid groupId)
    {
        foreach (var group in _registry.Groups)
        {
            if (group.Id == groupId) return group;
        }
        return null;
    }
}
