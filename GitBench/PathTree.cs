namespace GitGui;

/// <summary>
/// One node of a <see cref="PathTree"/>: a folder when <see cref="Leaf"/> is null, otherwise a leaf
/// carrying <typeparamref name="TLeaf"/>. <see cref="Segment"/> is the path piece this node contributes
/// (a compacted folder holds several joined with "/"); <see cref="FullPath"/> is the slash-joined path
/// from the root.
/// </summary>
internal sealed class PathNode<TLeaf> where TLeaf : class
{
    public PathNode(string segment, string fullPath)
    {
        Segment = segment;
        FullPath = fullPath;
    }

    public string Segment { get; internal set; }
    public string FullPath { get; internal set; }
    public TLeaf? Leaf { get; internal set; }
    public List<PathNode<TLeaf>> Children { get; internal set; } = new();

    internal Dictionary<string, PathNode<TLeaf>> ChildIndex { get; } = new();
}

/// <summary>
/// Builds a folder/leaf tree from a flat item list keyed by a "/"-delimited path. Shared by the branches
/// sidebar (branch names) and the local-changes file panels (file paths): both split on "/", group leaves
/// under folder nodes, and sort folders-before-leaves alphabetically. The structure depends only on the
/// items and their paths — no layout, canvas, or view-tree dependency — so render and navigation code can
/// build the same tree independently. Each builder keeps its own row-emission step.
/// </summary>
internal static class PathTree
{
    /// <param name="compact">
    /// When true, a chain of single-child folders collapses into one node (Assets → Scripts → UI becomes
    /// a node with segment "Assets/Scripts/UI"). A folder whose only child is a leaf is never compacted.
    /// Branch folders keep every level; file folders compact.
    /// </param>
    public static IReadOnlyList<PathNode<TLeaf>> Build<TLeaf>(
        IReadOnlyList<TLeaf> items, Func<TLeaf, string> pathSelector, bool compact = false) where TLeaf : class
    {
        var root = new PathNode<TLeaf>("", "");
        foreach (var item in items)
        {
            var segments = pathSelector(item).Split('/');
            var current = root;
            for (var i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                var isLeaf = i == segments.Length - 1;
                if (!current.ChildIndex.TryGetValue(seg, out var child))
                {
                    var path = i == 0 ? seg : current.FullPath + "/" + seg;
                    child = new PathNode<TLeaf>(seg, path);
                    current.ChildIndex[seg] = child;
                    current.Children.Add(child);
                }
                if (isLeaf) child.Leaf = item;
                current = child;
            }
        }
        Sort(root);
        if (compact) Compact(root);
        return root.Children;
    }

    // Folders first, then leaves; alphabetical within each group. A path can't be both a leaf and a
    // folder in git, so no node is simultaneously both.
    private static void Sort<TLeaf>(PathNode<TLeaf> node) where TLeaf : class
    {
        node.Children.Sort((a, b) =>
        {
            var aFolder = a.Leaf == null;
            var bFolder = b.Leaf == null;
            if (aFolder != bFolder) return aFolder ? -1 : 1;
            return string.Compare(a.Segment, b.Segment, StringComparison.OrdinalIgnoreCase);
        });
        foreach (var c in node.Children) Sort(c);
    }

    private static void Compact<TLeaf>(PathNode<TLeaf> node) where TLeaf : class
    {
        foreach (var child in node.Children)
        {
            while (child.Children.Count == 1 && child.Children[0].Leaf == null)
            {
                var only = child.Children[0];
                child.Segment += "/" + only.Segment;
                child.FullPath = only.FullPath;
                child.Children = only.Children;
            }
            Compact(child);
        }
    }
}
