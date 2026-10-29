using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace Fufucode.App.Features.FilePreview;

/// <summary>层级模板将 Children 绑定到子树，节点按需展开；解析时有全树预算。</summary>
public sealed class StructuredPreviewControl : UserControl
{
    public const int MaximumNodes = 10000;
    public const int MaximumDepth = 64;
    public const int MaximumChildren = 1000;
    public const int MaximumValueCharacters = 4096;

    public StructuredPreviewControl(FilePreviewResult result)
    {
        var tree = new TreeView
        {
            ItemsSource = result.Tree,
            Margin = new Thickness(10),
            ItemTemplate = new FuncTreeDataTemplate<FilePreviewTreeNode>(
                (node, _) => new TextBlock
                {
                    Text = node.DisplayValue,
                    MaxWidth = 900,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                node => node.Children ?? Array.Empty<FilePreviewTreeNode>()),
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var note = result.Format.StartsWith("YAML", StringComparison.Ordinal)
            ? "YAML 按缩进显示大纲；复杂 YAML 语法请使用原文。" : string.Empty;
        if (result.IsTruncated) note += " 树已达到预览上限，省略位置已标出。";
        layout.Children.Add(new TextBlock
        {
            Text = note, IsVisible = note.Length > 0,
            Margin = new Thickness(10, 4), TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetRow(tree, 1);
        layout.Children.Add(tree);
        Content = layout;
    }

    public static IReadOnlyList<FilePreviewTreeNode> Parse(string text, string extension,
        out bool truncated, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new Budget(cancellationToken);
        IReadOnlyList<FilePreviewTreeNode> nodes;
        switch (extension.ToLowerInvariant())
        {
            case "json":
            case "jsonc":
                using (var document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    MaxDepth = MaximumDepth, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
                })) nodes = [JsonNode("root", document.RootElement, 0, budget)];
                break;
            case "xml":
            case "xsd":
                using (var input = new StringReader(text))
                using (var reader = XmlReader.Create(input, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = SafeFileReader.DefaultMaxBytes,
                }))
                {
                    var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root ?? throw new XmlException("XML 没有根节点。");
                    nodes = [XmlNode(root, 0, budget)];
                }
                break;
            default:
                nodes = YamlOutline(text, budget);
                break;
        }
        truncated = budget.Truncated;
        return nodes;
    }

    private static FilePreviewTreeNode JsonNode(string label, JsonElement element, int depth, Budget budget)
    {
        budget.Visit();
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return new(budget.Clip(label), budget.Clip(element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()));
        if (depth >= MaximumDepth) return new(budget.Clip(label)!, "深度上限", budget.OmittedChildren());
        var children = new List<FilePreviewTreeNode>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (children.Count >= MaximumChildren - 1 || budget.Exhausted) { children.AddRange(budget.OmittedChildren()); break; }
                children.Add(JsonNode(property.Name, property.Value, depth + 1, budget));
            }
        }
        else
        {
            foreach (var value in element.EnumerateArray())
            {
                if (children.Count >= MaximumChildren - 1 || budget.Exhausted) { children.AddRange(budget.OmittedChildren()); break; }
                children.Add(JsonNode($"[{children.Count}]", value, depth + 1, budget));
            }
        }
        return new(budget.Clip(label), element.ValueKind == JsonValueKind.Array ? $"[{element.GetArrayLength()}]" : "{}", children);
    }

    private static FilePreviewTreeNode XmlNode(XElement element, int depth, Budget budget)
    {
        budget.Visit();
        if (depth >= MaximumDepth) return new(budget.Clip(element.Name.ToString()), "深度上限", budget.OmittedChildren());
        var children = new List<FilePreviewTreeNode>();
        foreach (var attribute in element.Attributes())
        {
            if (children.Count >= MaximumChildren - 1 || budget.Exhausted) { children.AddRange(budget.OmittedChildren()); break; }
            budget.Visit();
            children.Add(new(budget.Clip("@" + attribute.Name), budget.Clip(attribute.Value)));
        }
        foreach (var child in element.Elements())
        {
            if (children.Count >= MaximumChildren - 1 || budget.Exhausted)
            {
                if (children.Count == 0 || children[^1].Label != "…") children.AddRange(budget.OmittedChildren());
                break;
            }
            children.Add(XmlNode(child, depth + 1, budget));
        }
        var text = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value)).Trim();
        return new(budget.Clip(element.Name.ToString()), budget.Clip(text), children);
    }

    private static IReadOnlyList<FilePreviewTreeNode> YamlOutline(string text, Budget budget)
    {
        var roots = new List<OutlineNode>();
        var stack = new Stack<(int Indent, OutlineNode Node)>();
        using var input = new StringReader(text);
        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;
            while (stack.Count > 0 && stack.Peek().Indent >= indent) stack.Pop();
            var siblings = stack.Count == 0 ? roots : stack.Peek().Node.Children;
            if (budget.Exhausted || stack.Count >= MaximumDepth || siblings.Count >= MaximumChildren - 1)
            {
                foreach (var marker in budget.OmittedChildren()) siblings.Add(new OutlineNode(marker.Label, marker.Value));
                break;
            }
            budget.Visit();
            var content = line.Trim();
            var separator = content.IndexOf(':');
            var node = new OutlineNode(budget.Clip(separator > 0 ? content[..separator].Trim() : content)!,
                separator > 0 ? budget.Clip(content[(separator + 1)..].Trim()) : null);
            siblings.Add(node);
            stack.Push((indent, node));
        }
        return roots.Select(Freeze).ToArray();
        static FilePreviewTreeNode Freeze(OutlineNode node) => new(node.Label, node.Value, node.Children.Select(Freeze).ToArray());
    }

    private sealed record OutlineNode(string Label, string? Value)
    {
        public List<OutlineNode> Children { get; } = [];
    }

    private sealed class Budget(CancellationToken cancellationToken)
    {
        private int _nodes;
        private bool _terminalMarkerAdded;
        public bool Truncated { get; set; }
        // 留最后一个预算给省略节点，返回树含标记仍不超过 MaximumNodes。
        public bool Exhausted => _nodes >= MaximumNodes - 1;
        public void Visit() { cancellationToken.ThrowIfCancellationRequested(); _nodes++; }
        public IReadOnlyList<FilePreviewTreeNode> OmittedChildren()
        {
            Truncated = true;
            if (Exhausted)
            {
                if (_terminalMarkerAdded) return [];
                _terminalMarkerAdded = true;
            }
            Visit();
            return [new("…", "已省略后续节点")];
        }
        [return: NotNullIfNotNull(nameof(value))]
        public string? Clip(string? value)
        {
            if (value is null || value.Length <= MaximumValueCharacters) return value;
            Truncated = true;
            return value[..MaximumValueCharacters] + "…";
        }
    }
}
