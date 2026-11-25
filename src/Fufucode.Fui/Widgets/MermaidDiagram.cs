namespace Fufucode.Fui;

// Terminal Mermaid support is intentionally a truthful subset: common flowchart TD/LR nodes and
// labelled --> edges. Anything outside that grammar falls back to the original fenced source.
internal static class MermaidDiagram
{
    private enum NodeShape { Default, Square, Round, Diamond }

    private sealed class Node(string id)
    {
        internal string Id { get; } = id;
        internal string Label { get; set; } = id;
        internal NodeShape Shape { get; set; }
    }

    private sealed record Edge(Node From, Node To, string Label);

    internal static bool TryRender(string source, int width, out IReadOnlyList<string> rows, out string reason)
    {
        rows = [];
        reason = "";
        width = Math.Max(1, width);
        var input = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var lines = input.Select(line => line.Trim()).Where(line => line.Length != 0 && !line.StartsWith("%%", StringComparison.Ordinal)).ToArray();
        if (lines.Length == 0 || !TryHeader(lines[0], out var direction))
        {
            reason = "需要 flowchart/graph TD 或 LR 标题";
            return false;
        }

        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var order = new List<Node>();
        var edges = new List<Edge>();
        for (var lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (UnsupportedDirective(line))
            {
                reason = "不支持子图、样式或交互指令";
                return false;
            }
            if (line.EndsWith(';')) line = line[..^1].TrimEnd();
            if (line.Contains(';'))
            {
                reason = "一行只能包含一个节点或连线";
                return false;
            }
            if (!TryParseStatement(line, nodes, order, edges, out reason)) return false;
        }
        if (order.Count == 0)
        {
            reason = "没有可显示的节点";
            return false;
        }

        var outgoing = order.ToDictionary(node => node.Id, _ => new List<Edge>(), StringComparer.Ordinal);
        var incoming = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            outgoing[edge.From.Id].Add(edge);
            incoming.Add(edge.To.Id);
        }

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var path = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string> { lines[0] };
        var roots = order.Where(node => !incoming.Contains(node.Id)).ToArray();
        if (roots.Length == 0) roots = [order[0]];
        foreach (var root in roots)
        {
            if (expanded.Contains(root.Id)) continue;
            if (output.Count > 1) output.Add("");
            RenderNode(root, "", "", true, null, direction, outgoing, expanded, path, width, output);
        }
        foreach (var node in order)
        {
            if (expanded.Contains(node.Id)) continue;
            if (output.Count > 1) output.Add("");
            RenderNode(node, "", "", true, null, direction, outgoing, expanded, path, width, output);
        }
        rows = output;
        return true;
    }

    private static bool TryHeader(string line, out string direction)
    {
        var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        direction = "";
        if (parts.Length != 2 || parts[0] is not ("flowchart" or "graph")) return false;
        parts[1] = parts[1].ToUpperInvariant();
        if (parts[1] is not ("TD" or "TB" or "LR")) return false;
        direction = parts[1] == "TB" ? "TD" : parts[1];
        return true;
    }

    private static bool UnsupportedDirective(string line)
    {
        var first = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first is "subgraph" or "end" or "classDef" or "class" or "style" or "linkStyle" or "click" or
            "direction" or "sequenceDiagram" or "classDiagram" or "stateDiagram" or "erDiagram" or "gantt" or
            "pie" or "journey";
    }

    private static bool TryParseStatement(string line, Dictionary<string, Node> nodes, List<Node> order,
        List<Edge> edges, out string reason)
    {
        reason = "";
        var position = 0;
        if (!TryNode(line, ref position, nodes, order, out var from))
        {
            reason = "节点语法无效";
            return false;
        }
        SkipSpace(line, ref position);
        if (position == line.Length) return true;

        while (position < line.Length)
        {
            string? label = null;
            if (StartsAt(line, position, "-->"))
            {
                position += 3;
            }
            else if (StartsAt(line, position, "--"))
            {
                position += 2;
                var close = line.IndexOf("-->", position, StringComparison.Ordinal);
                if (close < 0)
                {
                    reason = "仅支持 --> 连线";
                    return false;
                }
                label = line[position..close].Trim();
                position = close + 3;
            }
            else
            {
                reason = "仅支持 --> 连线";
                return false;
            }

            SkipSpace(line, ref position);
            if (position < line.Length && line[position] == '|')
            {
                var close = line.IndexOf('|', position + 1);
                if (close < 0)
                {
                    reason = "条件标签缺少右竖线";
                    return false;
                }
                label = line[(position + 1)..close].Trim();
                position = close + 1;
                SkipSpace(line, ref position);
            }
            if (!TryNode(line, ref position, nodes, order, out var to))
            {
                reason = "连线目标节点无效";
                return false;
            }
            edges.Add(new(from, to, CleanLabel(label)));
            from = to;
            SkipSpace(line, ref position);
        }
        return true;
    }

    private static bool TryNode(string line, ref int position, Dictionary<string, Node> nodes, List<Node> order, out Node node)
    {
        node = null!;
        SkipSpace(line, ref position);
        var start = position;
        while (position < line.Length && (char.IsLetterOrDigit(line[position]) || line[position] is '_' or '-' or '.') &&
            !(line[position] == '-' && position + 1 < line.Length && line[position + 1] == '-'))
            position++;
        if (position == start) return false;
        var id = line[start..position];
        if (!char.IsLetter(id[0]) && id[0] != '_') return false;
        if (!nodes.TryGetValue(id, out node!))
        {
            node = new Node(id);
            nodes.Add(id, node);
            order.Add(node);
        }

        SkipSpace(line, ref position);
        if (position >= line.Length || line[position] is not ('[' or '(' or '{')) return true;
        var open = line[position++];
        var close = open switch { '[' => ']', '(' => ')', _ => '}' };
        var end = line.IndexOf(close, position);
        if (end < 0) return false;
        node.Label = CleanLabel(line[position..end]);
        node.Shape = open switch { '[' => NodeShape.Square, '(' => NodeShape.Round, _ => NodeShape.Diamond };
        position = end + 1;
        return true;
    }

    private static void RenderNode(Node node, string linePrefix, string childIndent, bool root, string? edgeLabel,
        string direction, Dictionary<string, List<Edge>> outgoing, HashSet<string> expanded, HashSet<string> path,
        int width, List<string> output)
    {
        var arrow = direction == "LR" ? "→" : "↓";
        var line = root ? NodeText(node) :
            linePrefix + (edgeLabel is { Length: > 0 } ? edgeLabel + " " + arrow + " " : arrow + " ") + NodeText(node);
        output.Add(TerminalText.Clip(line, width));
        if (!expanded.Add(node.Id)) return;

        path.Add(node.Id);
        var children = outgoing[node.Id];
        for (var i = 0; i < children.Count; i++)
        {
            var edge = children[i];
            var last = i == children.Count - 1;
            var branch = childIndent + (last ? "└─ " : "├─ ");
            if (path.Contains(edge.To.Id))
            {
                var cycle = branch + (edge.Label.Length == 0 ? "" : edge.Label + " ") + "↺ " + NodeText(edge.To);
                output.Add(TerminalText.Clip(cycle, width));
                continue;
            }
            var nextIndent = childIndent + (last ? "   " : "│  ");
            RenderNode(edge.To, branch, nextIndent, false, edge.Label, direction, outgoing, expanded, path, width, output);
        }
        path.Remove(node.Id);
    }

    private static string NodeText(Node node)
    {
        var label = node.Label.Length == 0 ? node.Id : node.Label;
        return node.Shape switch
        {
            NodeShape.Square => $"{node.Id}[{label}]",
            NodeShape.Round => $"{node.Id}({label})",
            NodeShape.Diamond => $"{node.Id}{{{label}}}",
            _ => label == node.Id ? $"({node.Id})" : $"{node.Id}({label})"
        };
    }

    private static string CleanLabel(string? value) => TerminalText.SingleLine(value ?? "").Trim();

    private static bool StartsAt(string text, int at, string value) =>
        at + value.Length <= text.Length && text.AsSpan(at, value.Length).SequenceEqual(value);

    private static void SkipSpace(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
    }
}
