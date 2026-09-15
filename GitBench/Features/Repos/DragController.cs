using System.Diagnostics;
using GitBench.Git;
using ZGF.Geometry;
using ZGF.Gui;
using ZGF.Observable;

namespace GitBench.Features.Repos;

public abstract record DragSession
{
    private DragSession() { }

    public sealed record Repo(Guid RepoId) : DragSession;

    public sealed record Group(Guid GroupId) : DragSession;
}

public abstract record DropTarget
{
    private DropTarget(RectF indicatorBounds) => IndicatorBounds = indicatorBounds;

    public RectF IndicatorBounds { get; }

    public sealed record IntoGroup(Guid GroupId, int InsertIndex, RectF IndicatorBounds) : DropTarget(IndicatorBounds);

    public sealed record BetweenGroups(int InsertIndex, RectF IndicatorBounds) : DropTarget(IndicatorBounds);
}

public sealed class DragController
{
    private abstract record Registration
    {
        private Registration() { }

        public sealed record RepoRow(Guid GroupId, Guid RepoId) : Registration;

        public sealed record GroupHeader(Guid GroupId) : Registration;

        public sealed record GroupSection(Guid GroupId) : Registration;
    }

    private readonly IRepoRegistry _registry;
    private readonly Dictionary<View, Registration> _registrations = new();
    private DragSession? _session;

    public DragController(IRepoRegistry registry)
    {
        _registry = registry;
    }

    public State<DropTarget?> Target { get; } = new(null);

    public void StartRepoDrag(Repo source)
    {
        _session = new DragSession.Repo(source.Id);
        Target.Value = null;
    }

    public void StartGroupDrag(Group source)
    {
        _session = new DragSession.Group(source.Id);
        Target.Value = null;
    }

    public void UpdateDrag(PointF mouse)
    {
        Target.Value = _session switch
        {
            DragSession.Repo => ResolveRepoTarget(mouse),
            DragSession.Group group => ResolveGroupTarget(mouse, group.GroupId),
            null => null,
            _ => throw new UnreachableException(),
        };
    }

    public void CompleteDrag()
    {
        var session = _session;
        var target = Target.Value;
        _session = null;
        Target.Value = null;
        switch (session, target)
        {
            case (DragSession.Repo repo, DropTarget.IntoGroup into):
                _registry.MoveRepo(repo.RepoId, into.GroupId, into.InsertIndex);
                break;
            case (DragSession.Group group, DropTarget.BetweenGroups between):
                _registry.MoveGroup(group.GroupId, between.InsertIndex);
                break;
        }
    }

    public void CancelDrag()
    {
        _session = null;
        Target.Value = null;
    }

    public void RegisterRepoRow(View view, Guid groupId, Guid repoId)
        => _registrations[view] = new Registration.RepoRow(groupId, repoId);

    public void RegisterGroupHeader(View view, Guid groupId)
        => _registrations[view] = new Registration.GroupHeader(groupId);

    public void RegisterGroupSection(View view, Guid groupId)
        => _registrations[view] = new Registration.GroupSection(groupId);

    public void Unregister(View view) => _registrations.Remove(view);

    private DropTarget? ResolveRepoTarget(PointF mouse)
    {
        foreach (var (view, reg) in _registrations)
        {
            if (reg is not Registration.RepoRow row) continue;
            if (!view.Position.ContainsPoint(mouse)) continue;
            var pos = view.Position;
            var midY = pos.Bottom + pos.Height * 0.5f;
            var insertAbove = mouse.Y > midY;
            var group = FindGroup(row.GroupId);
            if (group is null) return null;
            var currentIndex = group.RepoIds.IndexOf(row.RepoId);
            if (currentIndex < 0) return null;
            var insertIndex = insertAbove ? currentIndex : currentIndex + 1;
            var indicatorY = insertAbove ? pos.Top : pos.Bottom;
            return new DropTarget.IntoGroup(
                row.GroupId,
                insertIndex,
                new RectF(pos.Left, indicatorY - 1, pos.Width, 2));
        }

        foreach (var (view, reg) in _registrations)
        {
            if (reg is not Registration.GroupHeader header) continue;
            if (!view.Position.ContainsPoint(mouse)) continue;
            var pos = view.Position;
            return new DropTarget.IntoGroup(
                header.GroupId,
                0,
                new RectF(pos.Left, pos.Bottom - 1, pos.Width, 2));
        }

        foreach (var (view, reg) in _registrations)
        {
            if (reg is not Registration.GroupSection section) continue;
            if (!view.Position.ContainsPoint(mouse)) continue;
            var group = FindGroup(section.GroupId);
            if (group is null) continue;
            var pos = view.Position;
            return new DropTarget.IntoGroup(
                section.GroupId,
                group.RepoIds.Count,
                new RectF(pos.Left, pos.Bottom - 1, pos.Width, 2));
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
                if (reg is not Registration.GroupSection section) continue;
                if (section.GroupId != groupId) continue;
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

        return new DropTarget.BetweenGroups(insertIndex.Value, indicator);
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
