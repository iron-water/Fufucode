using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

public abstract class GraphObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
    protected internal void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record GraphViewport(double X, double Y, double Scale, bool Positioned);

/// <summary>A viewport belongs to a presentation, never to the project authority.</summary>
public sealed class GraphViewportState : GraphObservable
{
    private GraphViewport _value = new(0, 0, 1, false);
    public double X => _value.X;
    public double Y => _value.Y;
    public double Scale => _value.Scale;
    public bool Positioned => _value.Positioned;
    public GraphViewport Capture() => _value;
    public void Restore(GraphViewport value)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Scale)) return;
        if (Set(ref _value, value with { Scale = Math.Clamp(value.Scale, 0.08, 4) }, nameof(Capture)))
            Notify(null);
    }
    public void Pan(double dx, double dy) => Restore(_value with { X = X + dx, Y = Y + dy, Positioned = true });
    public void Zoom(double factor, Point anchor)
    {
        var scale = Math.Clamp(Scale * factor, 0.08, 4);
        var ratio = scale / Scale;
        Restore(new(anchor.X - (anchor.X - X) * ratio, anchor.Y - (anchor.Y - Y) * ratio, scale, true));
    }
    public void Fit(Rect content, Size available)
    {
        if (available.Width < 1 || available.Height < 1 || content.Width < 1 || content.Height < 1) return;
        var scale = Math.Clamp(Math.Min((available.Width - 48) / content.Width,
            (available.Height - 48) / content.Height), 0.08, 1.25);
        Restore(new((available.Width - content.Width * scale) / 2 - content.X * scale,
            (available.Height - content.Height * scale) / 2 - content.Y * scale, scale, true));
    }
    public Point ToWorld(Point point) => new((point.X - X) / Scale, (point.Y - Y) / Scale);
}

public sealed record GraphModuleOption(string? Id, string Name)
{
    public override string ToString() => Name;
}
public sealed record GraphKindOption(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class GraphModuleDraft : GraphObservable
{
    public string? Id { get; internal set; }
    private string? _parentId;
    private string _name = "", _summary = "", _sourcePaths = "";
    public bool IsDirty { get; private set; }
    public string? ParentId { get => _parentId; set { if (Set(ref _parentId, value)) IsDirty = true; } }
    public string Name { get => _name; set { if (Set(ref _name, value)) IsDirty = true; } }
    public string Summary { get => _summary; set { if (Set(ref _summary, value)) IsDirty = true; } }
    public string SourcePaths { get => _sourcePaths; set { if (Set(ref _sourcePaths, value)) IsDirty = true; } }
    internal void Load(GraphModule module)
    {
        Id = module.Id;
        _parentId = module.ParentId;
        _name = module.Name;
        _summary = module.Summary;
        _sourcePaths = string.Join(Environment.NewLine, module.SourcePaths);
        IsDirty = false;
        Notify(null);
    }
    internal void MarkSaved() => IsDirty = false;
}

public sealed class GraphRelationDraft : GraphObservable
{
    public string? Id { get; internal set; }
    private string? _sourceId, _targetId;
    private string _kind = "dependency", _label = "";
    public string? SourceId { get => _sourceId; set => Set(ref _sourceId, value); }
    public string? TargetId { get => _targetId; set => Set(ref _targetId, value); }
    public string Kind { get => _kind; set => Set(ref _kind, value); }
    public string Label { get => _label; set => Set(ref _label, value); }
}

public sealed class GraphTicketDraft : GraphObservable
{
    private string _title = "", _goal = "", _acceptance = "";
    private readonly HashSet<string> _moduleIds = new(StringComparer.Ordinal);
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Goal { get => _goal; set => Set(ref _goal, value); }
    public string Acceptance { get => _acceptance; set => Set(ref _acceptance, value); }
    public IReadOnlyCollection<string> ModuleIds => _moduleIds;
    public void IncludeModule(string id, bool include)
    {
        if (include ? _moduleIds.Add(id) : _moduleIds.Remove(id)) Notify(nameof(ModuleIds));
    }
}

public sealed class GraphCorrespondenceDraft : GraphObservable
{
    private string _text = "", _reference = "", _kind = "message", _vetoReason = "", _acceptanceEvidence = "";
    private string _evidenceCriterion = "", _evidenceText = "", _evidenceReference = "", _sessionId = "";
    private bool _evidencePassed;
    private string _liftVetoReason = "", _recoveryResolution = "";
    private bool _sideEffectsChecked;
    public string LiftVetoReason { get => _liftVetoReason; set => Set(ref _liftVetoReason, value); }
    public string RecoveryResolution { get => _recoveryResolution; set => Set(ref _recoveryResolution, value); }
    public bool SideEffectsChecked { get => _sideEffectsChecked; set => Set(ref _sideEffectsChecked, value); }
    public string EvidenceCriterion { get => _evidenceCriterion; set => Set(ref _evidenceCriterion, value); }
    public string EvidenceText { get => _evidenceText; set => Set(ref _evidenceText, value); }
    public string EvidenceReference { get => _evidenceReference; set => Set(ref _evidenceReference, value); }
    public bool EvidencePassed { get => _evidencePassed; set => Set(ref _evidencePassed, value); }
    public string SessionId { get => _sessionId; set => Set(ref _sessionId, value); }
    public string Text { get => _text; set => Set(ref _text, value); }
    public string Reference { get => _reference; set => Set(ref _reference, value); }
    public string Kind { get => _kind; set => Set(ref _kind, value); }
    public string VetoReason { get => _vetoReason; set => Set(ref _vetoReason, value); }
    public string AcceptanceEvidence { get => _acceptanceEvidence; set => Set(ref _acceptanceEvidence, value); }
}

public sealed record GraphRelationRow(GraphRelation Relation, string SourceName, string TargetName)
{
    public string Text => SourceName + " → " + TargetName + Environment.NewLine + GraphLabels.RelationKind(Relation.Kind) +
        (string.IsNullOrWhiteSpace(Relation.Label) ? "" : " · " + Relation.Label);
}
public sealed record GraphTicketRow(GraphTicket Ticket, GraphEntry? LastEntry)
{
    public string Status => GraphLabels.Status(Ticket.Status);
    public string Sender => LastEntry == null ? "" : GraphLabels.Actor(LastEntry.Actor);
    public string Preview => LastEntry == null ? Ticket.Goal : GraphEntryPresentation.Summary(LastEntry);
    public string Time => GraphLabels.Time(Ticket.UpdatedUtc);
}

public sealed record GraphNode(GraphModule Module, Rect Bounds, bool IsReference);
public sealed record GraphGroup(GraphModule Module, Rect Bounds);
public sealed record GraphEdge(GraphRelation Relation, Rect Source, Rect Target, bool IsUpperLevel)
{
    public string Label => (IsUpperLevel ? "上层关系 · " : "") + GraphLabels.RelationKind(Relation.Kind) +
        (string.IsNullOrWhiteSpace(Relation.Label) ? "" : " · " + Relation.Label);
}
public sealed record GraphScene(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphGroup> Groups,
    IReadOnlyList<GraphEdge> Edges, Rect Bounds)
{
    public string? Hit(Point world)
    {
        foreach (var node in Nodes.Reverse()) if (node.Bounds.Contains(world)) return node.Module.Id;
        foreach (var group in Groups.Reverse())
            if (new Rect(group.Bounds.Position, new Size(group.Bounds.Width, 30)).Contains(world)) return group.Module.Id;
        return null;
    }
    public Rect? FindBounds(string id) => Nodes.FirstOrDefault(n => n.Module.Id == id)?.Bounds ??
        Groups.FirstOrDefault(g => g.Module.Id == id)?.Bounds;
}

/// <summary>Projects only real modules and relations. Expanded endpoints attach to their group,
/// while references keep the authority's original id and relation endpoints.</summary>
public static class GraphSceneBuilder
{
    private const double NodeWidth = 200, NodeHeight = 84, Gap = 24;
    public static GraphScene Build(GraphSnapshot snapshot, IReadOnlyCollection<string>? scope, int depth)
    {
        var modules = snapshot.Modules.GroupBy(m => m.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var children = modules.Values.Where(m => m.ParentId != null && modules.ContainsKey(m.ParentId))
            .GroupBy(m => m.ParentId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var nodes = new List<GraphNode>();
        var groups = new List<GraphGroup>();
        var represented = new HashSet<string>(StringComparer.Ordinal);
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        var roots = scope == null ? modules.Values.Where(m => m.ParentId == null || !modules.ContainsKey(m.ParentId)).ToList()
            : scope.Where(modules.ContainsKey).Select(id => modules[id]).ToList();
        // Corrupt cycles remain inspectable without hanging the view.
        if (scope == null)
        {
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            void Visit(string id)
            {
                if (!reachable.Add(id)) return;
                if (children.TryGetValue(id, out var nested)) foreach (var child in nested) Visit(child.Id);
            }
            foreach (var root in roots) Visit(root.Id);
            foreach (var module in modules.Values)
                if (!reachable.Contains(module.Id)) { roots.Add(module); Visit(module.Id); }
        }
        Size Layout(GraphModule module, int remaining, double x, double y, HashSet<string> path)
        {
            if (represented.Contains(module.Id) || expanded.Contains(module.Id)) return default;
            if (!path.Add(module.Id) || remaining <= 0 || !children.TryGetValue(module.Id, out var nested) || nested.Length == 0)
            {
                represented.Add(module.Id);
                nodes.Add(new(module, new Rect(x, y, NodeWidth, NodeHeight), false));
                return new(NodeWidth, NodeHeight);
            }
            expanded.Add(module.Id);
            var start = groups.Count;
            groups.Add(new(module, default));
            double width = NodeWidth, height = 36;
            // Leaf siblings form two columns. Nested groups retain their own boundaries.
            var leafGrid = remaining == 1 || nested.All(m => !children.ContainsKey(m.Id));
            double rowHeight = 0;
            for (var i = 0; i < nested.Length; i++)
            {
                var column = leafGrid ? i % 2 : 0;
                var size = Layout(nested[i], remaining - 1, x + 14 + column * (NodeWidth + 16), y + height,
                    new HashSet<string>(path, StringComparer.Ordinal));
                rowHeight = Math.Max(rowHeight, size.Height);
                width = Math.Max(width, 28 + column * (NodeWidth + 16) + size.Width);
                if (!leafGrid || column == 1 || i == nested.Length - 1)
                { height += rowHeight + 14; rowHeight = 0; }
            }
            groups[start] = new(module, new Rect(x, y, width, height));
            return new(width, height);
        }
        double nextX = 24, nextY = 24, rowHeight = 0, rightmost = 24;
        var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(roots.Count)), 1, 4);
        var columnIndex = 0;
        foreach (var root in roots)
        {
            var size = Layout(root, Math.Clamp(depth, 0, modules.Count), nextX, nextY, new(StringComparer.Ordinal));
            if (size.Width <= 0) continue;
            rightmost = Math.Max(rightmost, nextX + size.Width);
            nextX += size.Width + Gap;
            rowHeight = Math.Max(rowHeight, size.Height);
            if (++columnIndex == columns)
            { nextX = 24; nextY += rowHeight + Gap; rowHeight = 0; columnIndex = 0; }
        }
        nextX = rightmost + Gap;
        var endpoint = nodes.ToDictionary(n => n.Module.Id, n => n.Bounds, StringComparer.Ordinal);
        foreach (var group in groups) endpoint.TryAdd(group.Module.Id, group.Bounds);
        var visibleIds = endpoint.Keys.ToHashSet(StringComparer.Ordinal);
        var relevant = snapshot.Relations.Where(r => visibleIds.Contains(r.SourceId) || visibleIds.Contains(r.TargetId)).ToArray();
        double referenceY = 24;
        foreach (var id in relevant.SelectMany(r => new[] { r.SourceId, r.TargetId }).Distinct(StringComparer.Ordinal))
        {
            if (endpoint.ContainsKey(id) || !modules.TryGetValue(id, out var module)) continue;
            var bounds = new Rect(nextX + 32, referenceY, NodeWidth, NodeHeight);
            nodes.Add(new(module, bounds, true));
            endpoint.Add(id, bounds);
            referenceY += NodeHeight + 16;
        }
        var edges = relevant.Where(r => endpoint.ContainsKey(r.SourceId) && endpoint.ContainsKey(r.TargetId))
            .Select(r => new GraphEdge(r, endpoint[r.SourceId], endpoint[r.TargetId],
                expanded.Contains(r.SourceId) || expanded.Contains(r.TargetId))).ToArray();
        var allBounds = nodes.Select(n => n.Bounds).Concat(groups.Select(g => g.Bounds)).ToArray();
        var boundsTotal = allBounds.Length == 0 ? new Rect(0, 0, 200, 84) : allBounds.Aggregate((a, b) => a.Union(b));
        return new(nodes, groups, edges, boundsTotal.Inflate(12));
    }
}

internal static class GraphLabels
{
    public static string RelationKind(string kind) => kind switch
    {
        "dependency" => "依赖", "dataflow" or "data-flow" or "data_flow" => "数据流",
        "ProjectReference" => "项目引用", "Import" => "导入", "Compile" => "编译引用", _ => kind
    };
    public static string Status(string status) => status switch
    {
        "open" or "pending" => "待处理", "running" => "运行中", "completed" => "待验收",
        "accepted" => "已验收", "vetoed" => "已否决", "failed" => "失败", "stopped" => "已停止",
        "stopping" => "停止中", "cancelled" => "已取消", "blocked" => "阻塞",
        "Authorized" => "已授权", "Active" => "运行中", "Review" => "待验收",
        "NeedsReconciliation" => "待核对", "ReplanRequired" => "待重规划", "Done" => "已验收",
        "Cancelled" => "已取消", "StopRequested" => "已请求停止", "Interrupted" => "已中断",
        "Disabled" => "已禁用", "NeedsPurpose" => "待设置目的", "Running" => "运行中", "Paused" => "已暂停",
        "Completed" => "已完成", "Archived" => "已归档", "Proposed" => "待授权", "WaitingPerson" => "等待处理",
        "WaitingDependency" => "等待依赖", "WaitingResource" => "等待资源", "HumanOwned" => "人工接管",
        "Failed" => "失败", "Queued" => "排队中", "Unknown" => "状态未知", "Received" => "已收到",
        "Delivered" => "已送达", "Read" => "已读", "Applied" => "已应用", "Open" => "待处理",
        "Answered" => "已答复", "Withdrawn" => "已撤回", "Expired" => "已过期", _ => status
    };
    public static string EntryKind(string kind) => kind switch
    {
        "message" or "mail" or "session-message" => "邮件", "evidence" => "证据", "decision" => "决定", "run" => "运行",
        "ticket-created" => "工单建立", "source" => "来源", "scan" => "扫描", "scan-notice" => "扫描提示",
        "conflict" => "待核对", "module" => "模块变更", "module-removed" => "模块移除", "relation" => "关系变更",
        "relation-removed" => "关系移除", "source-removed" => "来源移除", "suggestion" => "建议", "note" => "备注", "observation" => "观察",
        "evidence-claim" => "证据提交", "evidence-source" => "证据来源", "evidence-rejected" => "证据未通过",
        "session" => "会话关联", "claim" => "执行回复", "planning" => "规划", "progress" => "进展",
        "stop-requested" => "停止请求", "run-result" => "运行结果", "scheduler-error" => "推进失败",
        "purpose" => "目的变更", "options" => "选项变更", "attention" => "待处理事项",
        "veto-lifted" => "否决已解除", "reconciliation" => "恢复核查",
        "Veto" or "input:Veto" => "否决", "Acceptance" => "验收", "Stop" or "input:Stop" => "停止",
        "Gate" => "审批", "Constraint" => "约束", "Plan" => "计划", "Replan" => "重新规划",
        "Takeover" or "input:Takeover" => "人工接管", "input:Query" => "提问", "input:Suggestion" => "建议",
        "input:Evidence" => "证据", "input:ScopeChange" => "范围变更", "input:Answer" => "答复", "input:NewGoal" => "新目标",
        _ => kind
    };
    public static string Actor(string actor) => actor switch
    {
        "human" or "user" => "你", "agent" or "assistant" => "助手", "system" => "系统",
        "scanner" => "项目扫描", "executor" => "执行者", _ => actor
    };
    public static string ModuleSummary(string summary) => summary.StartsWith("Directory:", StringComparison.Ordinal)
        ? "目录：" + summary[10..].TrimStart() : summary.StartsWith("File:", StringComparison.Ordinal)
            ? "文件：" + summary[5..].TrimStart() : summary;
    public static string Time(long utc)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
        catch (ArgumentOutOfRangeException) { return utc.ToString(); }
    }
}
