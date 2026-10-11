using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

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
    /// <summary>外部设置提示文本（例如"请先选择重置范围"）。</summary>
    public void Notify(string message) => Status = message;
    public ObservableCollection<AppActionDefinition> Actions { get; } = new();
    public Func<WorkbenchLayoutState>? LayoutSnapshot { get; set; }
    public WorkbenchLayoutState Layout => WorkbenchLayoutState.Restore((Current.Layout ?? LayoutSnapshot?.Invoke() ?? new()).Serialize(), WorkbenchLayoutState.KnownPages);
    public string Section { get; set; } = "appearance";
    public void EditLayout(Action<WorkbenchLayoutState> edit)
    {
        var layout = Layout; edit(layout); Change(s => s with { Layout = layout });
    }
    public void ResetLayout() => Change(s => s with { Layout = new() });
    public void SynchronizeLayout(WorkbenchLayoutState layout)
    {
        var snapshot = WorkbenchLayoutState.Restore(layout.Serialize(), WorkbenchLayoutState.KnownPages);
        if (Current.Layout?.Serialize() == snapshot.Serialize()) return;
        Execute(() => _store.Save(Current with { Layout = snapshot }, HasProject), HasProject);
    }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void SetActions(IEnumerable<AppActionDefinition> actions)
    { Actions.Clear(); foreach (var action in actions) Actions.Add(action); }
    public void AttachProject(string? path)
    { _store.AttachProject(path); _projectScope = _projectScope && HasProject; _undo.Clear(); _redo.Clear(); Refresh(); Applied?.Invoke(Current); }
    public void Change(Func<WorkbenchAppearance, WorkbenchAppearance> edit) => Execute(() => _store.Save(edit(Current), ProjectScope));
    public void ApplyJson() => Execute(() => _store.Replace(Json, ProjectScope));
    public void Preset(string preset) => Change(current => WorkbenchAppearance.Preset(preset) with { Actions = current.Actions, Layout = current.Layout, UserName = current.UserName, AssistantName = current.AssistantName });
    public void Reset() => Execute(() => { _store.Reset(ProjectScope); _store.Save(Current with { Layout = new() }, ProjectScope); });
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
