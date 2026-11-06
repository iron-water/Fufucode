using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fufucode.App.Abstractions.Graph;

namespace Fufucode.App.Features.Graph;

/// <summary>Shared by the central page and all dock contributions. The service remains the sole
/// authority; selection, drafts and viewport state are local presentation state.</summary>
public sealed class GraphWorkspaceViewModel : GraphObservable, IDisposable
{
    public const string PageId = "graph";
    public const string PropertiesPageId = "graph-properties";
    public const string TicketsPageId = "graph-tickets";
    public const string StructurePageId = "graph-structure";
    public const string HistoryPageId = "graph-history";
    public const string MiniMapPageId = "graph-minimap";
    private readonly IGraphProjectService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, WorkspaceState> _projects = new(StringComparer.Ordinal);
    private WorkspaceState _state = new();
    private string? _projectId;
    private int _refreshQueued, _operations;
    private bool _disposed;
    private string? _error;
    private bool _moduleEditorOpen, _relationEditorOpen, _ticketEditorOpen;
    private GraphModuleDraft _moduleDraft = new();
    private GraphRelationDraft _relationDraft = new();
    private GraphCorrespondenceDraft _correspondence = new();
    private readonly Dictionary<string, CancellationTokenSource> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Avalonia.Vector> _readingOffsets = new(StringComparer.Ordinal);
    internal void SaveReadingOffset(string key, Avalonia.Vector offset) => _readingOffsets[key] = offset;
    internal Avalonia.Vector ReadReadingOffset(string key) => _readingOffsets.TryGetValue(key, out var value) ? value : default;

    private sealed class WorkspaceState
    {
        public readonly HashSet<string> Selection = new(StringComparer.Ordinal);
        public string? ModuleId, TicketId;
        public long? Revision;
        public string[]? Scope;
        public int Depth;
        public bool List;
        public string Purpose = "";
        public bool PurposeDirty;
        public GraphTicketDraft TicketDraft = new();
        public readonly Dictionary<string, GraphModuleDraft> ModuleDrafts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, GraphCorrespondenceDraft> Replies = new(StringComparer.Ordinal);
        public readonly Dictionary<string, GraphRelationDraft> RelationDrafts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (GraphViewport Main, GraphViewport Mini)> Viewports = new(StringComparer.Ordinal);
    }

    public GraphWorkspaceViewModel(IGraphProjectService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Snapshot = service.ReadSnapshot();
        Scene = GraphSceneBuilder.Build(Snapshot, null, 0);
        ApplySnapshot(Snapshot);
        _service.Changed += OnServiceChanged;
    }

    public event Func<string, string, string, string[], TimeSpan, CancellationToken, Task<string>>? ValidateCandidateRequested;
    public event Func<string, string[], string, string, CancellationToken, Task<string>>? CompleteInternalRequested;
    public string CoveredCriteria { get; set; } = "";
    public string ContinuationReason { get; set; } = "";
    public Task<bool> CompleteInternalAsync() => ExecuteAsync("完成内部工作", async token =>
    {
        var handler = CompleteInternalRequested ?? throw new InvalidOperationException("可信完成宿主未连接");
        var criteria = CoveredCriteria.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (criteria.Length == 0 || string.IsNullOrWhiteSpace(ContinuationReason)) throw new InvalidOperationException("必须明确验证覆盖的验收项和后续工作说明");
        _developmentResults[RequireTicket()] = await handler(RequireTicket(), criteria, "active_goal", ContinuationReason, token);
        Notify(nameof(DevelopmentResult));
    }, default);
    public event Func<string, CancellationToken, Task<string>>? IntegrateCandidateRequested;
    private string _validationExecutable = "", _validationArguments = "", _validationTimeout = "300";
    private readonly Dictionary<string, string> _developmentResults = new(StringComparer.Ordinal);
    public string ValidationExecutable { get => _validationExecutable; set => Set(ref _validationExecutable, value); }
    public string ValidationArguments { get => _validationArguments; set => Set(ref _validationArguments, value); }
    public string ValidationTimeout { get => _validationTimeout; set => Set(ref _validationTimeout, value); }
    public string DevelopmentResult => SelectedTicketId is { } id && _developmentResults.TryGetValue(id, out var result) ? result : "";
    public string? CandidateManifestPath
    {
        get
        {
            foreach (var entry in Snapshot.Entries.Where(e => e.TicketId == SelectedTicketId && e.Kind == "run-result" && e.Actor == "executor").OrderByDescending(e => e.CreatedUtc))
            {
                try
                {
                    using var data = JsonDocument.Parse(entry.Text);
                    if (data.RootElement.TryGetProperty("candidateManifestPath", out var path) && path.ValueKind == JsonValueKind.String &&
                        path.GetString() is { } value && Path.IsPathFullyQualified(value)) return value;
                }
                catch (JsonException) { }
            }
            return null;
        }
    }
    public Task<bool> ValidateCandidateAsync() => ExecuteAsync("候选验证", async token =>
    {
        var ticket = RequireTicket();
        var manifest = CandidateManifestPath ?? throw new InvalidOperationException("选中工单没有候选清单");
        if (!Path.IsPathFullyQualified(ValidationExecutable) || !double.TryParse(ValidationTimeout, out var seconds) ||
            !double.IsFinite(seconds) || seconds <= 0 || seconds > 86400) throw new InvalidOperationException("需要绝对可执行路径及1天以内的正数超时秒数");
        var handler = ValidateCandidateRequested ?? throw new InvalidOperationException("验证宿主未连接");
        var arguments = ValidationArguments.Length == 0 ? Array.Empty<string>() : ValidationArguments.Replace("\r\n", "\n").Split('\n');
        _developmentResults[ticket] = await handler(ticket, manifest, ValidationExecutable, arguments, TimeSpan.FromSeconds(seconds), token);
        Notify(nameof(DevelopmentResult));
    }, default);
    public Task<bool> IntegrateCandidateAsync() => ExecuteAsync("候选集成", async token =>
    {
        var ticket = RequireTicket();
        var handler = IntegrateCandidateRequested ?? throw new InvalidOperationException("集成宿主未连接");
        _developmentResults[ticket] = await handler(ticket, token);
        Notify(nameof(DevelopmentResult));
    }, default);
    public event Action<string?, string?, string>? OpenChatRequested;
    public event Action<string>? RevealPanelRequested;
    public event Action? SceneChanged;
    public GraphSnapshot Snapshot { get; private set; }
    public GraphScene Scene { get; private set; }
    public ObservableCollection<GraphModule> Modules { get; } = new();
    public ObservableCollection<GraphModule> VisibleModules { get; } = new();
    public ObservableCollection<GraphRelation> SelectedModuleRelations { get; } = new();
    public ObservableCollection<GraphRelationRow> RelationRows { get; } = new();
    public ObservableCollection<GraphOrganizationMessage> HumanInbox { get; } = new();
    public ObservableCollection<GraphResponsibility> OrganizationResponsibilities { get; } = new();
    public ObservableCollection<GraphWorkDependency> WorkDependencies { get; } = new();
    private string? _selectedMessageId;
    private string _organizationReply = "";
    private bool _showInternalWork;
    public bool ShowInternalWork { get => _showInternalWork; set { if (Set(ref _showInternalWork, value)) Notify(nameof(ShowHumanInbox)); } }
    public bool ShowHumanInbox => !ShowInternalWork;
    public GraphOrganizationMessage? SelectedHumanMessage => HumanInbox.FirstOrDefault(m => m.Id == _selectedMessageId);
    public bool HasSelectedHumanMessage => SelectedHumanMessage != null;
    public string OrganizationReply { get => _organizationReply; set => Set(ref _organizationReply, value); }
    public string OrganizationVersion => Snapshot.Organization is { } organization ? $"组织 v{organization.Version}" : "此快照无组织记录";
    public string ResponsibilityText(GraphResponsibility responsibility) => responsibility.Id + " · " + responsibility.Role + "\n范围：" +
        string.Join("、", responsibility.ModuleIds.Select(id => Snapshot.Modules.FirstOrDefault(m => m.Id == id)?.Name ?? id)) +
        "\n指派：" + (responsibility.TicketId is { } ticket ? Snapshot.Tickets.FirstOrDefault(t => t.Id == ticket)?.Title ?? ticket : "项目持续职责");
    public string DependencyText(GraphWorkDependency dependency) =>
        (Snapshot.Tickets.FirstOrDefault(t => t.Id == dependency.TicketId)?.Title ?? dependency.TicketId) + " ← " +
        (Snapshot.Tickets.FirstOrDefault(t => t.Id == dependency.DependsOnTicketId)?.Title ?? dependency.DependsOnTicketId);
    public void SelectHumanMessage(string id)
    {
        _selectedMessageId = id;
        if (SelectedHumanMessage is { } message)
        {
            if (message.Content.TicketId is { } ticket) SelectTicket(ticket);
            if (message.Content.ModuleId is { } module) SelectModule(module);
        }
        Notify(null);
    }
    public bool ReplyToHumanMessage() => Mutate("回复", () =>
    {
        var message = SelectedHumanMessage ?? throw new InvalidOperationException("未选择收件消息");
        if (string.IsNullOrWhiteSpace(OrganizationReply)) throw new InvalidOperationException("请输入回复内容");
        if (_service is not IGraphOrganizationService service)
            throw new NotSupportedException("当前服务不支持组织消息");
        var recipient = message.SenderId == "host" ? "project-lead" : message.SenderId;
        service.SendOrganizationMessage(Guid.NewGuid().ToString("N"), new GraphMessageDraft(recipient,
            recipient == "human", "reply", OrganizationReply.Trim(), message.Content.TicketId, message.Content.ModuleId, Snapshot.Revision));
        OrganizationReply = "";
    });
    public ObservableCollection<GraphTicketRow> TicketRows { get; } = new();
    public ObservableCollection<GraphTicket> Tickets { get; } = new();
    public ObservableCollection<GraphEntry> ThreadEntries { get; } = new();
    public ObservableCollection<GraphEntry> Entries { get; } = new();
    public ObservableCollection<GraphRevision> Revisions { get; } = new();
    public ObservableCollection<GraphRun> SelectedTicketRuns { get; } = new();
    public ObservableCollection<GraphModuleOption> ModuleOptions { get; } = new();
    public ObservableCollection<GraphModuleOption> ParentOptions { get; } = new();
    public ObservableCollection<GraphKindOption> AvailableRelationKinds { get; } = new();
    public ObservableCollection<GraphModuleOption> TicketModules { get; } = new();
    public GraphViewportState MainViewport { get; } = new();
    public GraphViewportState MiniMapViewport { get; } = new();
    public static IReadOnlyList<GraphKindOption> RelationKinds { get; } = new[]
    { new GraphKindOption("dependency", "依赖"), new GraphKindOption("dataflow", "数据流") };
    public static IReadOnlyList<GraphKindOption> EntryKinds { get; } = new[]
    { new GraphKindOption("message", "邮件"), new GraphKindOption("evidence", "证据") };
    public bool IsHistorical => _state.Revision.HasValue || Snapshot.IsHistorical;
    public bool CanEdit => !IsHistorical && !_disposed;
    public bool IsBusy => _operations > 0;
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string VersionLabel => IsHistorical ? $"v{Snapshot.Revision} · 只读" : "现在";
    public string ProjectStatus => GraphLabels.Status(Snapshot.ProjectStatus);
    public int Depth => _state.Depth;
    public IReadOnlyCollection<string> SelectedModuleIds => _state.Selection;
    public string? SelectedModuleId => _state.ModuleId;
    public string? SelectedTicketId => _state.TicketId;
    public GraphModule? SelectedModule => Snapshot.Modules.FirstOrDefault(m => m.Id == SelectedModuleId);
    public GraphTicket? SelectedTicket => Snapshot.Tickets.FirstOrDefault(t => t.Id == SelectedTicketId);
    public bool HasSelectedModule => SelectedModule != null;
    public bool HasSelectedTicket => SelectedTicket != null;
    public bool MissingSelectedModule => SelectedModuleId != null && SelectedModule == null;
    public bool MissingSelectedTicket => SelectedTicketId != null && SelectedTicket == null;
    public string SelectionLabel => SelectedModule?.Name ?? (MissingSelectedModule ? "尚未建立" : "未选择模块");
    public string TicketSelectionLabel => SelectedTicket?.Title ?? (MissingSelectedTicket ? "尚未建立" : "未选择工单");
    public string SelectedTicketStatus => SelectedTicket == null ? "" : GraphLabels.Status(SelectedTicket.Status);
    public bool NeedsTicketReconciliation => SelectedTicket?.Status == "NeedsReconciliation";
    public bool HasActiveTicketVeto => SelectedTicketId is { } id && Snapshot.Entries.Any(e => GraphEntryPresentation.IsActiveTicketVeto(e, id));
    public bool HasTicketRecoveryActions => HasActiveTicketVeto || NeedsTicketReconciliation;
    public string SelectedModuleSources => SelectedModule == null ? "" : string.Join(Environment.NewLine, SelectedModule.SourcePaths);
    public string SelectedModuleSummary => GraphLabels.ModuleSummary(SelectedModule?.Summary ?? "");
    public string SelectedTicketAcceptance => SelectedTicket == null ? "" : string.Join(Environment.NewLine, SelectedTicket.Acceptance);
    public string SelectedModuleParent => SelectedModule?.ParentId is { } id
        ? Snapshot.Modules.FirstOrDefault(m => m.Id == id)?.Name ?? "尚未建立" : Snapshot.Title;
    public string PurposeDraft
    {
        get => _state.Purpose;
        set { if (_state.Purpose == value) return; _state.Purpose = value; _state.PurposeDirty = true; Notify(); }
    }
    public bool IsListView
    {
        get => _state.List;
        set { if (_state.List == value) return; _state.List = value; Notify(); }
    }
    public GraphModuleDraft ModuleDraft => _moduleDraft;
    public GraphRelationDraft RelationDraft => _relationDraft;
    public GraphTicketDraft TicketDraft => _state.TicketDraft;
    public GraphCorrespondenceDraft Correspondence => _correspondence;
    public bool IsModuleEditorOpen { get => _moduleEditorOpen; private set => Set(ref _moduleEditorOpen, value); }
    public bool IsRelationEditorOpen { get => _relationEditorOpen; private set => Set(ref _relationEditorOpen, value); }
    public bool IsTicketEditorOpen { get => _ticketEditorOpen; private set => Set(ref _ticketEditorOpen, value); }

    public void Refresh()
    {
        if (_disposed) return;
        if (!Dispatcher.UIThread.CheckAccess()) { OnServiceChanged(); return; }
        try
        {
            var current = _service.ReadSnapshot();
            var projectChanged = _projectId != current.ProjectId;
            if (projectChanged)
            {
                SaveViewport();
                _projectId = current.ProjectId;
                if (!_projects.TryGetValue(_projectId, out _state!))
                    _projects[_projectId] = _state = new WorkspaceState();
                IsModuleEditorOpen = IsRelationEditorOpen = IsTicketEditorOpen = false;
                _selectedMessageId = null;
                OrganizationReply = "";
                ShowInternalWork = false;
            }
            var next = _state.Revision is { } revision ? _service.ReadSnapshot(revision) : current;
            ApplySnapshot(next);
            if (projectChanged) RestoreViewport();
        }
        catch (Exception ex) { Error = "读取项目失败：" + ex.Message; }
    }

    private void OnServiceChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed) Refresh();
        }, DispatcherPriority.Background);
    }

    private void ApplySnapshot(GraphSnapshot next)
    {
        if (_projectId == null)
        {
            _projectId = next.ProjectId;
            _projects[_projectId] = _state;
        }
        Snapshot = next;
        Reconcile(HumanInbox, (next.Organization?.Messages ?? Array.Empty<GraphOrganizationMessage>())
            .Where(m => m.Content.HumanRecipient && m.Content.RecipientId == "human").OrderByDescending(m => m.Sequence), m => m.Id);
        Reconcile(OrganizationResponsibilities, next.Organization?.Responsibilities ?? Array.Empty<GraphResponsibility>(), r => r.Id);
        Reconcile(WorkDependencies, next.Organization?.Dependencies ?? Array.Empty<GraphWorkDependency>(), d => (d.TicketId, d.DependsOnTicketId));
        if (!_state.PurposeDirty) _state.Purpose = next.Purpose;
        Reconcile(Modules, next.Modules, m => m.Id);
        Reconcile(Tickets, next.Tickets.OrderByDescending(t => t.UpdatedUtc), t => t.Id);
        Reconcile(TicketRows, Tickets.Select(t => new GraphTicketRow(t, next.Entries
            .Where(e => e.TicketId == t.Id).OrderByDescending(e => e.CreatedUtc).FirstOrDefault())), r => r.Ticket.Id);
        Reconcile(Entries, next.Entries.OrderByDescending(e => e.CreatedUtc), e => e.Id);
        Reconcile(Revisions, next.Revisions.OrderBy(r => r.Version), r => r.Version);
        Reconcile(ModuleOptions, next.Modules.Select(m => new GraphModuleOption(m.Id, m.Name)), m => m.Id!);
        Reconcile(AvailableRelationKinds, RelationKinds.Concat(next.Relations.Select(r =>
            new GraphKindOption(r.Kind, GraphLabels.RelationKind(r.Kind)))).DistinctBy(k => k.Id), k => k.Id);
        Reconcile(ParentOptions, new[] { new GraphModuleOption("", next.Title) }.Concat(ModuleOptions), m => m.Id ?? "");
        foreach (var module in next.Modules)
            if (_state.ModuleDrafts.TryGetValue(module.Id, out var draft) && !draft.IsDirty && !IsHistorical) draft.Load(module);
        SyncSelection();
        RebuildScene(false);
        Notify(null);
    }

    private void SyncSelection()
    {
        Reconcile(SelectedModuleRelations, Snapshot.Relations.Where(r =>
            r.SourceId == SelectedModuleId || r.TargetId == SelectedModuleId), r => r.Id);
        Reconcile(RelationRows, SelectedModuleRelations.Select(r => new GraphRelationRow(r,
            Snapshot.Modules.FirstOrDefault(m => m.Id == r.SourceId)?.Name ?? "尚未建立",
            Snapshot.Modules.FirstOrDefault(m => m.Id == r.TargetId)?.Name ?? "尚未建立")), r => r.Relation.Id);
        Reconcile(ThreadEntries, Snapshot.Entries.Where(e => e.TicketId == SelectedTicketId && SelectedTicketId != null)
            .OrderBy(e => e.CreatedUtc), e => e.Id);
        Reconcile(SelectedTicketRuns, Snapshot.Runs.Where(r => r.TicketId == SelectedTicketId && SelectedTicketId != null)
            .OrderByDescending(r => r.StartedUtc), r => r.Id);
        Reconcile(TicketModules, (SelectedTicket?.ModuleIds ?? Array.Empty<string>()).Select(id =>
            new GraphModuleOption(id, Snapshot.Modules.FirstOrDefault(m => m.Id == id)?.Name ?? $"尚未建立 · {id}")), m => m.Id!);
        if (SelectedTicketId is { } ticketId)
        {
            if (!_state.Replies.TryGetValue(ticketId, out _correspondence!))
                _state.Replies[ticketId] = _correspondence = new();
        }
        else _correspondence = new();
        Notify(nameof(Correspondence));
    }

    internal static void Reconcile<T, TKey>(ObservableCollection<T> target, IEnumerable<T> values, Func<T, TKey> key)
        where TKey : notnull
    {
        var next = values.ToArray();
        var wanted = next.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--) if (!wanted.Contains(key(target[i]))) target.RemoveAt(i);
        for (var i = 0; i < next.Length; i++)
        {
            if (i < target.Count && EqualityComparer<TKey>.Default.Equals(key(target[i]), key(next[i])))
            { if (!EqualityComparer<T>.Default.Equals(target[i], next[i])) target[i] = next[i]; continue; }
            var found = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (EqualityComparer<TKey>.Default.Equals(key(target[j]), key(next[i]))) { found = j; break; }
            if (found >= 0)
            {
                target.Move(found, i);
                if (!EqualityComparer<T>.Default.Equals(target[i], next[i])) target[i] = next[i];
            }
            else target.Insert(i, next[i]);
        }
    }

    public void SelectModule(string moduleId, bool additive = false)
    {
        if (_disposed) return;
        if (!additive) _state.Selection.Clear();
        if (additive && !_state.Selection.Add(moduleId)) _state.Selection.Remove(moduleId);
        else _state.Selection.Add(moduleId);
        _state.ModuleId = _state.Selection.Contains(moduleId) ? moduleId : _state.Selection.LastOrDefault();
        IsModuleEditorOpen = IsRelationEditorOpen = false;
        SyncSelection();
        Notify(null);
        SceneChanged?.Invoke();
        RevealPanelRequested?.Invoke(PropertiesPageId);
    }
    public void SelectModules(IEnumerable<string> ids, bool additive = false)
    {
        if (_disposed) return;
        IsModuleEditorOpen = IsRelationEditorOpen = false;
        if (!additive) _state.Selection.Clear();
        foreach (var id in ids) _state.Selection.Add(id);
        _state.ModuleId = _state.Selection.LastOrDefault();
        SyncSelection();
        Notify(null);
        SceneChanged?.Invoke();
        if (_state.ModuleId != null) RevealPanelRequested?.Invoke(PropertiesPageId);
    }
    public void NavigateToModule(string moduleId)
    {
        if (_disposed) return;
        SelectModule(moduleId);
        if (!Scene.Nodes.Any(n => n.Module.Id == moduleId) && !Scene.Groups.Any(g => g.Module.Id == moduleId))
        {
            SaveViewport();
            _state.Scope = new[] { moduleId };
            _state.Depth = 0;
            RebuildScene(true);
        }
        RevealPanelRequested?.Invoke(PageId);
    }
    public void SelectTicket(string ticketId)
    {
        if (_disposed) return;
        _state.TicketId = ticketId;
        SyncSelection();
        Notify(null);
        // Reading a thread never invokes AcceptTicket or any other authority mutation.
    }
    public void OpenChat(string? moduleId = null, string? ticketId = null)
    {
        if (_disposed) return;
        moduleId ??= SelectedModuleId;
        ticketId ??= SelectedTicketId;
        try
        {
            var context = _service.BuildContext(moduleId, ticketId, IsHistorical ? Snapshot.Revision : null);
            OpenChatRequested?.Invoke(moduleId, ticketId, context);
        }
        catch (Exception ex) { Error = "打开会话失败：" + ex.Message; }
    }
    public void OpenProjectLeadChat()
    {
        if (_disposed) return;
        try { OpenChatRequested?.Invoke(null, null, _service.BuildContext(null, null, IsHistorical ? Snapshot.Revision : null)); }
        catch (Exception ex) { Error = "打开项目负责人失败：" + ex.Message; }
    }
    public void RevealPanel(string pageId) => RevealPanelRequested?.Invoke(pageId);
    public void DismissError() => Error = null;

    public void ShowRevision(long? revision)
    {
        if (_disposed || _state.Revision == revision) return;
        try
        {
            var snapshot = _service.ReadSnapshot(revision);
            SaveViewport();
            _state.Revision = revision;
            IsModuleEditorOpen = IsRelationEditorOpen = IsTicketEditorOpen = false;
            ApplySnapshot(snapshot);
            RestoreViewport();
        }
        catch (Exception ex) { Error = "读取历史失败：" + ex.Message; }
    }
    public void MoveHistory(int direction)
    {
        var versions = Revisions.Select(r => r.Version).Distinct().OrderBy(v => v).ToArray();
        if (versions.Length == 0) return;
        if (!IsHistorical)
        {
            if (direction < 0)
            {
                var previous = versions.Where(v => v < Snapshot.Revision).ToArray();
                if (previous.Length > 0) ShowRevision(previous[^1]);
            }
            return;
        }
        var candidates = versions.Where(v => direction < 0 ? v < Snapshot.Revision : v > Snapshot.Revision).ToArray();
        if (candidates.Length == 0) { if (direction > 0) ShowRevision(null); return; }
        var version = direction < 0 ? candidates[^1] : candidates[0];
        ShowRevision(direction > 0 && version == versions[^1] ? null : version);
    }
    public void ExpandSelected()
    {
        var selected = _state.Selection.Where(id => Snapshot.Modules.Any(m => m.Id == id)).ToArray();
        if (selected.Length == 0) { ExpandAll(); return; }
        SaveViewport();
        _state.Scope = selected;
        _state.Depth = Math.Min(1, MaximumDepth(selected));
        RebuildScene(true);
    }
    public void DrillInto(string moduleId) { SelectModule(moduleId); ExpandSelected(); }
    public void ExpandAll()
    {
        SaveViewport();
        _state.Scope = null;
        _state.Depth = Math.Min(_state.Depth + 1, MaximumDepth(null));
        RebuildScene(true);
    }
    public void GoDeeper()
    {
        SaveViewport();
        _state.Depth = Math.Min(_state.Depth + 1, MaximumDepth(_state.Scope));
        RebuildScene(true);
    }
    private int MaximumDepth(string[]? scope)
    {
        var modules = Snapshot.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var children = Snapshot.Modules.Where(m => m.ParentId != null).GroupBy(m => m.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        int Visit(string id, HashSet<string> path)
        {
            if (!path.Add(id) || !children.TryGetValue(id, out var nested)) return 0;
            return nested.Select(m => Visit(m.Id, new HashSet<string>(path, StringComparer.Ordinal)) + 1).DefaultIfEmpty(0).Max();
        }
        var roots = scope ?? Snapshot.Modules.Where(m => m.ParentId == null || !modules.ContainsKey(m.ParentId)).Select(m => m.Id).ToArray();
        return roots.Select(id => Visit(id, new HashSet<string>(StringComparer.Ordinal))).DefaultIfEmpty(0).Max();
    }
    public void GoUp()
    {
        SaveViewport();
        if (_state.Depth > 0) _state.Depth--;
        else if (_state.Scope != null)
        {
            var parents = _state.Scope.Select(id => Snapshot.Modules.FirstOrDefault(m => m.Id == id)?.ParentId)
                .Where(id => id != null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            _state.Scope = parents.Length == 0 ? null : parents;
        }
        RebuildScene(true);
    }
    public void GoTop() { SaveViewport(); _state.Scope = null; _state.Depth = 0; RebuildScene(true); }
    public void GoBottom()
    {
        SaveViewport(); _state.Scope = null; _state.Depth = MaximumDepth(null); RebuildScene(true);
    }
    private void RebuildScene(bool restore)
    {
        Scene = GraphSceneBuilder.Build(Snapshot, _state.Scope, _state.Depth);
        Reconcile(VisibleModules, Scene.Groups.Select(g => g.Module).Concat(Scene.Nodes.Select(n => n.Module))
            .DistinctBy(m => m.Id), m => m.Id);
        if (restore) RestoreViewport();
        Notify(nameof(Depth));
        Notify(nameof(Scene));
        SceneChanged?.Invoke();
    }
    private string ViewKey => $"{_state.Revision?.ToString() ?? "live"}/{_state.Depth}/{string.Join("|", _state.Scope ?? Array.Empty<string>())}";
    private void SaveViewport() => _state.Viewports[ViewKey] = (MainViewport.Capture(), MiniMapViewport.Capture());
    private void RestoreViewport()
    {
        var saved = _state.Viewports.TryGetValue(ViewKey, out var state) ? state :
            (new GraphViewport(0, 0, 1, false), new GraphViewport(0, 0, 1, false));
        MainViewport.Restore(saved.Item1);
        MiniMapViewport.Restore(saved.Item2);
    }

    public bool SavePurpose() => Mutate("保存目的", () =>
    {
        _service.SetPurpose(PurposeDraft);
        _state.PurposeDirty = false;
    });
    public void BeginModule(string? moduleId = null, string? parentId = null)
    {
        if (!CanEdit) return;
        var key = moduleId ?? "";
        if (!_state.ModuleDrafts.TryGetValue(key, out _moduleDraft!))
        {
            _moduleDraft = new GraphModuleDraft();
            if (moduleId is { } id && Snapshot.Modules.FirstOrDefault(m => m.Id == id) is { } module) _moduleDraft.Load(module);
            else _moduleDraft.ParentId = parentId;
            _state.ModuleDrafts[key] = _moduleDraft;
        }
        IsModuleEditorOpen = true;
        Notify(nameof(ModuleDraft));
        RevealPanelRequested?.Invoke(PropertiesPageId);
    }
    public void CancelModuleEdit() => IsModuleEditorOpen = false;
    public bool SaveModule()
    {
        GraphModule? saved = null;
        var success = Mutate("保存模块", () =>
        {
            if (string.IsNullOrWhiteSpace(ModuleDraft.Name)) throw new InvalidOperationException("模块名称不能为空");
            saved = _service.UpsertModule(ModuleDraft.Id, ModuleDraft.ParentId, ModuleDraft.Name,
                ModuleDraft.Summary, Lines(ModuleDraft.SourcePaths));
            _state.ModuleDrafts.Remove(ModuleDraft.Id ?? "");
            ModuleDraft.MarkSaved();
        });
        if (success && saved != null) { IsModuleEditorOpen = false; SelectModule(saved.Id); }
        return success;
    }
    public bool RemoveModule(string moduleId) => Mutate("删除模块", () => _service.RemoveModule(moduleId));
    public void BeginRelation(string? relationId = null)
    {
        if (!CanEdit) return;
        var key = relationId ?? "";
        if (!_state.RelationDrafts.TryGetValue(key, out _relationDraft!))
        {
            if (relationId is { } id && Snapshot.Relations.FirstOrDefault(r => r.Id == id) is { } relation)
                _relationDraft = new GraphRelationDraft { Id = id, SourceId = relation.SourceId, TargetId = relation.TargetId,
                    Kind = relation.Kind, Label = relation.Label };
            else _relationDraft = new GraphRelationDraft { SourceId = SelectedModuleId };
            _state.RelationDrafts[key] = _relationDraft;
        }
        IsRelationEditorOpen = true;
        Notify(nameof(RelationDraft));
        RevealPanelRequested?.Invoke(PropertiesPageId);
    }
    public void CancelRelationEdit() => IsRelationEditorOpen = false;
    public bool SaveRelation()
    {
        var success = Mutate("保存关系", () =>
        {
            if (RelationDraft.SourceId == null || RelationDraft.TargetId == null)
                throw new InvalidOperationException("请选择关系两端的模块");
            _service.UpsertRelation(RelationDraft.Id, RelationDraft.SourceId, RelationDraft.TargetId,
                RelationDraft.Kind, RelationDraft.Label);
        });
        if (success) { _state.RelationDrafts.Remove(RelationDraft.Id ?? ""); IsRelationEditorOpen = false; }
        return success;
    }
    public bool RemoveRelation(string relationId) => Mutate("删除关系", () => _service.RemoveRelation(relationId));
    public void BeginTicket()
    {
        if (!CanEdit) return;
        foreach (var id in _state.Selection) TicketDraft.IncludeModule(id, true);
        IsTicketEditorOpen = true;
        RevealPanelRequested?.Invoke(TicketsPageId);
    }
    public void CancelTicketEdit() => IsTicketEditorOpen = false;
    public bool CreateTicket()
    {
        GraphTicket? created = null;
        var success = Mutate("创建工单", () =>
        {
            if (string.IsNullOrWhiteSpace(TicketDraft.Title) || string.IsNullOrWhiteSpace(TicketDraft.Goal))
                throw new InvalidOperationException("工单主题和目标不能为空");
            created = _service.CreateTicket(TicketDraft.Title, TicketDraft.Goal, TicketDraft.ModuleIds.ToArray(), Lines(TicketDraft.Acceptance));
        });
        if (success && created != null)
        {
            _state.TicketDraft = new(); Notify(nameof(TicketDraft));
            IsTicketEditorOpen = false; SelectTicket(created.Id);
        }
        return success;
    }
    public bool SendEntry()
    {
        var success = Mutate("发送往来", () =>
        {
            var id = RequireTicket();
            if (string.IsNullOrWhiteSpace(Correspondence.Text)) throw new InvalidOperationException("往来内容不能为空");
            if (Correspondence.Kind == "evidence" && string.IsNullOrWhiteSpace(Correspondence.Reference))
                throw new InvalidOperationException("证据引用不能为空");
            _service.AddEntry(id, SelectedModuleId, Correspondence.Kind, Correspondence.Text,
                string.IsNullOrWhiteSpace(Correspondence.Reference) ? null : Correspondence.Reference);
        });
        if (success) { Correspondence.Text = ""; Correspondence.Reference = ""; }
        return success;
    }
    public bool AddEvidence(string criterion, string text, bool passed, string reference) => Mutate("添加证据", () =>
    {
        if (string.IsNullOrWhiteSpace(criterion) || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(reference))
            throw new InvalidOperationException("验收项、证据内容和引用不能为空");
        var payload = System.Text.Json.JsonSerializer.Serialize(new { criterion, text, passed });
        _service.AddEntry(RequireTicket(), SelectedModuleId, "evidence", payload, reference);
    });
    public bool VetoTicket() => Mutate("否决工单", () =>
    {
        if (string.IsNullOrWhiteSpace(Correspondence.VetoReason)) throw new InvalidOperationException("否决原因不能为空");
        _service.VetoTicket(RequireTicket(), Correspondence.VetoReason);
    });
    public bool LiftVeto() => Mutate("解除否决", () =>
    {
        if (string.IsNullOrWhiteSpace(Correspondence.LiftVetoReason)) throw new InvalidOperationException("解除理由不能为空");
        _service.LiftVeto(RequireTicket(), Correspondence.LiftVetoReason);
    });
    public bool ReconcileTicket() => Mutate("允许重试", () =>
    {
        if (!NeedsTicketReconciliation) throw new InvalidOperationException("工单无需核查未知结果");
        if (!Correspondence.SideEffectsChecked) throw new InvalidOperationException("请先核查副作用");
        if (string.IsNullOrWhiteSpace(Correspondence.RecoveryResolution))
            throw new InvalidOperationException("副作用核查与恢复说明不能为空");
        _service.ReconcileTicket(RequireTicket(), Correspondence.RecoveryResolution);
    });
    public bool AcceptTicket() => Mutate("验收工单", () =>
    {
        if (string.IsNullOrWhiteSpace(Correspondence.AcceptanceEvidence)) throw new InvalidOperationException("验收引用不能为空");
        _service.AcceptTicket(RequireTicket(), Correspondence.AcceptanceEvidence);
    });
    public bool LinkSession(string sessionId) => Mutate("关联会话", () => _service.LinkSession(RequireTicket(), sessionId));
    public Task<bool> ScanAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("扫描项目", token => _service.ScanAsync(token), cancellationToken);
    public async Task<bool> RunTicketAsync(CancellationToken cancellationToken = default)
    {
        if (!Writable()) return false;
        string id;
        try { id = RequireTicket(); }
        catch (Exception ex) { Error = ex.Message; return false; }
        if (_runs.ContainsKey(id)) { Error = "工单正在运行"; return false; }
        using var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _runs[id] = run;
        try { return await ExecuteAsync("运行工单", token => _service.RunTicketAsync(id, token), run.Token); }
        finally { _runs.Remove(id); }
    }
    public Task<bool> StopTicketAsync(CancellationToken cancellationToken = default)
    {
        var id = SelectedTicketId;
        if (id == null) { Error = "请选择工单"; return Task.FromResult(false); }
        return ExecuteAsync("停止工单", token => _service.StopTicketAsync(id, token), cancellationToken);
    }
    public Task<bool> SetAutonomyAsync(bool running, CancellationToken cancellationToken = default) =>
        ExecuteAsync(running ? "启动推进" : "停止推进", token => _service.SetAutonomyAsync(running, token), cancellationToken);
    public Task<bool> StopAllAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("停止全部", token => _service.StopAllAsync(token), cancellationToken);
    private string RequireTicket() => SelectedTicket != null ? SelectedTicket.Id : throw new InvalidOperationException("工单尚未建立");
    private bool Mutate(string action, Action command)
    {
        if (!Writable()) return false;
        Error = null;
        try { command(); Refresh(); return true; }
        catch (Exception ex) { Error = action + "失败：" + ex.Message; return false; }
    }
    private async Task<bool> ExecuteAsync(string action, Func<CancellationToken, Task> command, CancellationToken cancellationToken)
    {
        if (!Writable()) return false;
        Error = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _operations++; Notify(nameof(IsBusy));
        try { await command(linked.Token); if (!_disposed) Refresh(); return true; }
        catch (OperationCanceledException) { if (!_disposed) Error = action + "已取消"; return false; }
        catch (Exception ex) { if (!_disposed) Error = action + "失败：" + ex.Message; return false; }
        finally { _operations--; if (!_disposed) Notify(nameof(IsBusy)); }
    }
    private bool Writable()
    {
        if (_disposed) return false;
        if (IsHistorical) { Error = "历史版本只读"; return false; }
        return true;
    }
    private static string[] Lines(string text) => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.Changed -= OnServiceChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
        OpenChatRequested = null;
        CompleteInternalRequested = null;
        ValidateCandidateRequested = null;
        IntegrateCandidateRequested = null;
        RevealPanelRequested = null;
        SceneChanged = null;
    }
}
