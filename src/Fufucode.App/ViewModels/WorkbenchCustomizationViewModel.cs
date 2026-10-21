using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

/// <summary>工作台页面目录项：设置页只读取宿主真实注册的页面，不维护第二份页面清单。</summary>
public sealed record WorkbenchPageChoice(string Id, string Title);

/// <summary>设置页里“功能入口”类分类的真实运行状态：状态词 + 说明 + 打开的页面或动作。</summary>
public sealed record WorkbenchFeatureStatus(
    string State, string Detail, string? PageId = null, string? ActionId = null, string? ActionLabel = null);

public sealed class WorkbenchCustomizationViewModel : ViewModelBase
{
    private readonly WorkbenchAppearanceStore _store;
    private readonly Stack<(bool Project, string Json)> _undo = new(), _redo = new();
    private bool _projectScope;
    private string _json = "", _status = "";
    public WorkbenchCustomizationViewModel(WorkbenchAppearanceStore store) { _store = store; Refresh(); }
    public event Action<WorkbenchAppearance>? Applied;
    public WorkbenchAppearance Current => _store.Current;
    public bool HasProject => _store.ProjectPath is not null;
    public bool ProjectScope { get => _projectScope; set { if (SetProperty(ref _projectScope, value && HasProject)) Refresh(); } }
    public string ScopePath => ProjectScope ? _store.ProjectPath! : _store.GlobalPath;
    public string DataRoot => System.IO.Path.GetDirectoryName(_store.GlobalPath)!;
    public string Json { get => _json; set => SetProperty(ref _json, value); }
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public ObservableCollection<AppActionDefinition> Actions { get; } = new();
    public Func<WorkbenchLayoutState>? LayoutSnapshot { get; set; }
    /// <summary>宿主注册的工作台页面（含插件页）；未注入时回退内建静态目录。</summary>
    public Func<IReadOnlyList<WorkbenchPageChoice>>? PageCatalog { get; set; }
    public IReadOnlyList<WorkbenchPageChoice> Pages =>
        PageCatalog?.Invoke() ?? WorkbenchLayoutState.KnownPages.Select(id => new WorkbenchPageChoice(id, id)).ToArray();
    /// <summary>页面集合变化（插件启停）后由宿主通知设置页重建页面位置行。</summary>
    public event Action? PagesChanged;
    public void NotifyPagesChanged() { OnPropertyChanged(nameof(Pages)); PagesChanged?.Invoke(); }
    /// <summary>宿主提供的功能运行状态（终端插件、自动开发模块等）。</summary>
    public Func<string, WorkbenchFeatureStatus>? FeatureStatus { get; set; }
    public WorkbenchFeatureStatus StatusOf(string pageId) =>
        FeatureStatus?.Invoke(pageId) ?? new("未知", "宿主尚未提供该功能的运行状态。");
    public event Action? ActionsChanged;
    private IEnumerable<string> KnownPageIds => Pages.Select(page => page.Id).Concat(WorkbenchLayoutState.KnownPages);
    public WorkbenchLayoutState Layout => WorkbenchLayoutState.Restore((Current.Layout ?? LayoutSnapshot?.Invoke() ?? new()).Serialize(), KnownPageIds);
    public void EditLayout(Action<WorkbenchLayoutState> edit)
    {
        var layout = Layout; edit(layout); Change(s => s with { Layout = layout });
    }

    /// <summary>
    /// 显示/隐藏停靠区。显示空区时补一个该区默认页；否则布局会被“空区不收起”
    /// 规范化重新隐藏，用户表现为开关无效。
    /// </summary>
    public void SetZoneVisible(DockZone zone, bool visible) => EditLayout(layout =>
    {
        layout.Hidden[zone] = !visible;
        if (!visible || layout.Pages[zone].Count > 0) return;
        var available = Pages.Select(page => page.Id).ToHashSet(StringComparer.Ordinal);
        var occupied = layout.Pages.Where(pair => pair.Key != zone).SelectMany(pair => pair.Value)
            .Concat(layout.Floating.SelectMany(floating => floating.Pages))
            .ToHashSet(StringComparer.Ordinal);
        var preferred = new WorkbenchLayoutState().Pages[zone]
            .Where(id => available.Contains(id)).ToList();
        var fallback = preferred.FirstOrDefault(id => !occupied.Contains(id))
            ?? preferred.FirstOrDefault()
            ?? Pages.Select(page => page.Id).FirstOrDefault();
        if (fallback is null) return;
        if (occupied.Contains(fallback)) layout.Move(fallback, zone);
        else layout.Open(fallback, zone);
    });
    public void ResetLayout() => Change(s => s with { Layout = new() { NotificationMigrationApplied = true } });
    public void SynchronizeLayout(WorkbenchLayoutState layout)
    {
        var snapshot = WorkbenchLayoutState.Restore(layout.Serialize(), KnownPageIds);
        if (Current.Layout?.Serialize() == snapshot.Serialize()) return;
        // 自动同步是现场布局的持久化，不是用户编辑；不能污染撤销栈。
        try
        {
            _store.Save(Current with { Layout = snapshot }, HasProject);
            Refresh();
            Applied?.Invoke(Current with { Layout = Current.Layout ?? new() });
        }
        catch (Exception error)
        {
            Status = error.GetBaseException().Message;
            OnPropertyChanged(nameof(Current));
        }
    }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void SetActions(IEnumerable<AppActionDefinition> actions)
    {
        Actions.Clear();
        foreach (var action in actions) Actions.Add(action);
        ActionsChanged?.Invoke();
    }
    public void AttachProject(string? path)
    { _store.AttachProject(path); _projectScope = _projectScope && HasProject; _undo.Clear(); _redo.Clear(); Refresh(); Applied?.Invoke(Current); }
    public void Change(Func<WorkbenchAppearance, WorkbenchAppearance> edit) => Execute(() => _store.Save(edit(Current), ProjectScope));
    public void ApplyJson() => Execute(() => _store.Replace(Json, ProjectScope));
    public void Preset(string preset) => Change(current => WorkbenchAppearance.Preset(preset) with { Skin = current.Skin, Actions = current.Actions, Layout = current.Layout, UserName = current.UserName, AssistantName = current.AssistantName });
    public void Reset() => Execute(() => { _store.Reset(ProjectScope); _store.Save(Current with { Layout = new() { NotificationMigrationApplied = true } }, ProjectScope); });
    public void ClearOverrides() { ProjectScope = true; Execute(_store.ClearProjectOverrides); }
    public void Reload() { _store.Reload(); Refresh(); Applied?.Invoke(Current); }
    private void Execute(Action operation, bool? scope = null)
    {
        var target = scope ?? ProjectScope;
        var previous = (target, _store.ScopeJson(target));
        try
        {
            if (Current.Layout is null && LayoutSnapshot is not null)
            {
                _store.Save(Current with { Layout = Layout }, target);
                previous = (target, _store.ScopeJson(target));
            }
            operation(); _undo.Push(previous); _redo.Clear(); Refresh(); Applied?.Invoke(Current with { Layout = Current.Layout ?? new() }); Status = "已保存并应用";
        }
        catch (Exception error) { Status = error.GetBaseException().Message; OnPropertyChanged(nameof(Current)); }
    }
    public void Undo() => Restore(_undo, _redo);
    public void Redo() => Restore(_redo, _undo);
    private void Restore(Stack<(bool Project, string Json)> source, Stack<(bool Project, string Json)> destination)
    {
        if (!source.TryPeek(out var entry)) return;
        try
        {
            var previous = (entry.Project, _store.ScopeJson(entry.Project));
            _store.Replace(entry.Json, entry.Project);
            source.Pop(); destination.Push(previous); Refresh(); Applied?.Invoke(Current with { Layout = Current.Layout ?? new() }); Status = "已恢复配置";
        }
        catch (Exception error) { Status = error.GetBaseException().Message; }
    }
    public void SetPlacement(string id, UiActionPosition position, int order)
    {
        Change(current =>
        {
            var actions = new Dictionary<string, UiActionPlacement>(current.Actions, StringComparer.Ordinal) { [id] = new(position, order) };
            return current with { Actions = actions };
        });
    }
    private void Refresh()
    {
        Json = _store.ScopeJson(ProjectScope);
        Status = _store.LoadError ?? "";
        foreach (var name in new[] { nameof(Current), nameof(HasProject), nameof(ProjectScope), nameof(ScopePath), nameof(CanUndo), nameof(CanRedo) }) OnPropertyChanged(name);
    }
}
