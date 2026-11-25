namespace Fufucode.Fui;

internal sealed class FuiSessionTree
{
    private sealed class Node(FuiChoice choice)
    {
        public FuiChoice Choice { get; } = choice;
        public List<Node> Children { get; } = [];
        public Node? Parent;
        public bool Expanded;
        public int Walk, VisibleIndex;
    }

    private readonly Dictionary<string, Node> _byId;
    private readonly List<Node> _roots = [];

    public IReadOnlyList<FuiChoice> Rows { get; private set; } = [];
    public int Selected { get; private set; }

    public FuiSessionTree(IReadOnlyList<FuiChoice> choices, string currentId)
    {
        var nodes = choices.Select(c => new Node(c)).ToArray();
        _byId = nodes.ToDictionary(n => n.Choice.Value, StringComparer.Ordinal);
        foreach (var node in nodes)
            if (node.Choice.ParentSessionId is { } id)
                node.Parent = _byId.GetValueOrDefault(id);

        BreakCycles(nodes);
        foreach (var node in nodes)
            (node.Parent?.Children ?? _roots).Add(node);

        var current = _byId.GetValueOrDefault(currentId);
        for (var parent = current?.Parent; parent is not null; parent = parent.Parent)
            parent.Expanded = true;
        RebuildRows(current);
    }

    public void Move(long step) => Selected = (int)Math.Clamp(Selected + step, 0, Math.Max(0, Rows.Count - 1));

    public void Traverse(bool right)
    {
        if (Rows.Count == 0) return;
        var node = _byId[Rows[Selected].Value];
        if (right)
        {
            if (node.Children.Count == 0) return;
            if (node.Expanded) { Selected = node.Children[0].VisibleIndex; return; }
        }
        else if (!node.Expanded)
        {
            if (node.Parent is { } parent) Selected = parent.VisibleIndex;
            return;
        }
        node.Expanded = right;
        RebuildRows(node);
    }

    private static void BreakCycles(Node[] nodes)
    {
        // Follow each parent link once. An edge back into this walk, unlike a previously
        // finished walk, is a cycle. Promote its lowest ID so reopening a session cannot
        // change the repair by reordering the source rows.
        for (var i = 0; i < nodes.Length; i++)
        {
            var walk = i + 1;
            Node? node = nodes[i];
            while (node is { Walk: 0 })
            {
                node.Walk = walk;
                node = node.Parent;
            }
            if (node is null || node.Walk != walk) continue;
            var root = node;
            for (var member = node.Parent!; member != node; member = member.Parent!)
                if (string.CompareOrdinal(member.Choice.Value, root.Choice.Value) < 0) root = member;
            root.Parent = null;
        }
    }

    private void RebuildRows(Node? focus)
    {
        var rows = new List<FuiChoice>();
        var stack = new Stack<(Node Node, int Depth)>();
        Push(_roots, 0);
        while (stack.TryPop(out var item))
        {
            var (node, depth) = item;
            node.VisibleIndex = rows.Count;
            if (node == focus) Selected = rows.Count;
            rows.Add(node.Choice with { TreePosition = new(depth, node.Children.Count == 0 ? null : node.Expanded) });
            if (node.Expanded) Push(node.Children, depth + 1);
        }
        Rows = rows;

        void Push(List<Node> siblings, int depth)
        {
            // Preserve the metadata store's most-recent-first ordering among siblings.
            for (var i = siblings.Count - 1; i >= 0; i--) stack.Push((siblings[i], depth));
        }
    }
}
