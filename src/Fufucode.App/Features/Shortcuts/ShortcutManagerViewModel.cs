using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>
/// 快捷键管理界面的一行。绑定与冲突都在行上可见，
/// 「不可用原因」直接取自动作注册表的可用性判断，不另建一份判断。
/// </summary>
public sealed partial class ShortcutRowViewModel : ObservableObject
{
    private readonly Action<string> _commit;

    public ShortcutRowViewModel(ShortcutAction action, string effectiveText, string originText,
        bool isDefault, bool isDisabled, bool hasConflict, string? conflictText, string? scopeNote,
        Func<string?> unavailableReason, Action<string> commit)
    {
        Action = action;
        _commit = commit;
        _effectiveText = effectiveText;
        _originText = originText;
        _isDefault = isDefault;
        _isDisabled = isDisabled;
        _hasConflict = hasConflict;
        _conflictText = conflictText;
        _scopeNote = scopeNote;
        UnavailableReason = unavailableReason();
    }

    public ShortcutAction Action { get; }
    public string Id => Action.Id;
    public string Title => Action.Title;
    public string Group => Action.CategoryOrGroup;
    public string ScopeTitle => Action.Scope.Title();
    public string Description => Action.Description ?? string.Empty;
    public bool HasDescription => Description.Length > 0;

    /// <summary>动作当前不可用的原因（来自注册表）；null 表示可用。</summary>
    public string? UnavailableReason { get; private set; }
    public bool IsUnavailable => UnavailableReason is not null;

    [ObservableProperty] private string _effectiveText;
    public bool HasBinding => EffectiveText.Length > 0;

    [ObservableProperty] private string _originText;
    [ObservableProperty] private bool _isDefault;
    [ObservableProperty] private bool _isDisabled;

    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private string? _conflictText;
    public bool HasConflictText => HasConflict && !string.IsNullOrWhiteSpace(ConflictText);

    [ObservableProperty] private string? _scopeNote;
    public bool HasScopeNote => !string.IsNullOrWhiteSpace(ScopeNote);

    /// <summary>录制结果落定：由界面把用户按下的手势传给 VM。</summary>
    public void Commit(string gestureText) => _commit(gestureText);

    public void RefreshUnavailable(Func<string?> reason)
    {
        UnavailableReason = reason();
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(IsUnavailable));
    }

    partial void OnHasConflictChanged(bool value) => OnPropertyChanged(nameof(HasConflictText));
    partial void OnConflictTextChanged(string? value) => OnPropertyChanged(nameof(HasConflictText));
    partial void OnScopeNoteChanged(string? value) => OnPropertyChanged(nameof(HasScopeNote));
    partial void OnEffectiveTextChanged(string value) => OnPropertyChanged(nameof(HasBinding));
}

/// <summary>冲突列表里的一行：手势 + 涉及的动作，双方都要能看见。</summary>
public sealed record ShortcutConflictRowViewModel(string GestureText, string ScopeTitle, string ActionTitles, int ActionCount);

/// <summary>
/// 快捷键管理 VM：真实动作列表、搜索、按范围/分类分组、冲突解释、
/// 编辑/禁用/逐项恢复/全部恢复/保存。动作目录与可用性由调用方从
/// 动作注册表投影注入，模块本身不持有产品状态。
/// </summary>
public sealed partial class ShortcutManagerViewModel : ObservableObject
{
    private readonly ObservableCollection<ShortcutAction> _catalog = [];
    private readonly Dictionary<string, string[]> _userBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<string?>> _unavailable = new(StringComparer.Ordinal);
    private readonly Func<ShortcutBindingsStore?>? _storeProvider;

    public ShortcutManagerViewModel(Func<ShortcutBindingsStore?>? storeProvider = null)
    {
        _storeProvider = storeProvider;
        ReloadFromStore();
    }

    /// <summary>动作集合变化（注册表刷新或重新接线）后调用。</summary>
    public event Action? Changed;

    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = [];
    public ObservableCollection<ShortcutConflictRowViewModel> Conflicts { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private ShortcutScope? _scopeFilter;
    [ObservableProperty] private string _status = "未修改";
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private bool _isWritable = true;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);
    public bool HasConflicts => Conflicts.Count > 0;
    public string SummaryText => $"{Rows.Count} 个动作 · {(Rows.Count(row => row.HasBinding))} 个已绑定 · " +
        $"{(IsModified ? "有未保存改动" : "已保存")} · {Conflicts.Count} 处冲突";
    public string ScopeFilterText => ScopeFilter?.Title() ?? "全部范围";

    public IReadOnlyList<ShortcutScope> ScopeOptions { get; } = ShortcutScopeInfo.All;

    /// <summary>把动作目录绑定到本 VM。调用方传注册表投影；重复调用等价于刷新。</summary>
    public void SetActions(IEnumerable<ShortcutAction> actions, Func<string, string?>? unavailableReason = null)
    {
        _catalog.Clear();
        foreach (var action in actions) _catalog.Add(action);
        _unavailable.Clear();
        if (unavailableReason is not null)
            foreach (var action in _catalog) _unavailable[action.Id] = () => unavailableReason(action.Id);
        ReloadFromStore();
    }

    /// <summary>取当前生效绑定（用户改动优先，否则默认）。</summary>
    public IReadOnlyList<ShortcutGesture> EffectiveGestures(ShortcutAction action)
    {
        if (_userBindings.TryGetValue(action.Id, out var user))
            return user.Select(ShortcutGesture.Parse).OfType<ShortcutGesture>().ToArray();
        return action.DefaultGestures;
    }

    public bool IsDisabled(ShortcutAction action) =>
        _userBindings.TryGetValue(action.Id, out var user) && user.Length == 0;

    public ShortcutOrigin OriginOf(ShortcutAction action) =>
        !_userBindings.ContainsKey(action.Id) ? ShortcutOrigin.Default
        : _userBindings[action.Id].Length == 0 ? ShortcutOrigin.Disabled
        : ShortcutOrigin.User;

    private void ReloadFromStore()
    {
        _userBindings.Clear();
        LoadError = null;
        IsWritable = true;
        if (_storeProvider?.Invoke() is { } store)
        {
            foreach (var pair in store.Current.Bindings) _userBindings[pair.Key] = pair.Value;
            LoadError = store.LoadError;
            IsWritable = store.Writable;
        }
        Refresh();
        Status = LoadError is null ? "已加载" : "配置损坏，已回退默认绑定";
    }

    /// <summary>绑定的规范化文本；多绑定时以「 / 」连接。</summary>
    public string BindingText(ShortcutAction action) =>
        string.Join(" / ", EffectiveGestures(action).Select(gesture => gesture.Text));

    /// <summary>把一个动作改绑到新文本；空串 = 禁用。冲突不阻止保存，但在界面明确显示。</summary>
    public void SetBinding(string actionId, string? gestureText)
    {
        if (_catalog.FirstOrDefault(action => action.Id == actionId) is not { } action)
        {
            Status = "动作不存在：" + actionId;
            return;
        }

        var requested = (gestureText ?? string.Empty).Trim().Replace(' ', '+');
        if (requested.Length == 0)
        {
            _userBindings[actionId] = [];
            Status = $"已禁用「{action.Title}」的按键绑定";
            Refresh();
            return;
        }

        var gesture = ShortcutGesture.Parse(requested);
        if (gesture is null)
        {
            Status = $"无法识别按键组合「{requested}」，请使用形如 Ctrl+K 的写法";
            return;
        }

        if (ShortcutRules.Validate(gesture, action.Scope, ShortcutDefaults.TargetsTerminalInput(action.Id)) is { } violation)
        {
            Status = violation.Reason;
            Refresh();
            return;
        }

        _userBindings[actionId] = [gesture.Text];
        var conflicts = ShortcutConflicts.DetectFor(EffectivePairs(), gesture, action.Scope, actionId);
        Status = conflicts.Count == 0
            ? $"已绑定「{action.Title}」→ {gesture.Text}"
            : $"已绑定「{action.Title}」→ {gesture.Text}；与 {Describe(conflicts[0].ActionIds)} 冲突";
        Refresh();
    }

    /// <summary>恢复某项默认绑定（撤销用户的禁用或自定义）。</summary>
    public void ResetAction(string actionId)
    {
        if (!_userBindings.Remove(actionId)) { Status = "该项已沿用默认绑定"; Refresh(); return; }
        Status = "已恢复默认绑定";
        Refresh();
    }

    /// <summary>
    /// 恢复全部默认绑定。用户自定义绑定整体清空——包括已保存的，
    /// 因为「全部恢复默认」的语义就是回到产品默认，而不是只清本次未保存的改动。
    /// 结果需要 <see cref="Save"/> 落盘；未保存前为可撤销状态。
    /// </summary>
    public void ResetAll()
    {
        if (_userBindings.Count == 0 && _storeProvider?.Invoke() is not { Current.Bindings.Count: > 0 })
        {
            Status = "没有需要恢复的自定义绑定";
            Refresh();
            return;
        }
        _userBindings.Clear();
        Status = _storeProvider?.Invoke() is { Current.Bindings.Count: > 0 }
            ? "已恢复全部默认绑定，保存后生效"
            : "已恢复全部默认绑定";
        Refresh();
    }

    /// <summary>写入磁盘。配置损坏时拒绝覆盖并保留可见原因。</summary>
    public bool Save()
    {
        if (_storeProvider?.Invoke() is not { } store)
        {
            Status = "快捷键存储不可用";
            return false;
        }
        try
        {
            store.Save(_userBindings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            IsModified = false;
            LoadError = store.LoadError;
            IsWritable = store.Writable;
            Status = "已保存并生效";
            Refresh();
            return true;
        }
        catch (Exception error)
        {
            Status = "保存失败：" + error.GetBaseException().Message;
            Refresh();
            return false;
        }
    }

    /// <summary>放弃未保存改动，回到磁盘内容；同时清掉搜索与范围过滤，让改动结果可见。</summary>
    public void Revert()
    {
        SearchText = string.Empty;
        ScopeFilter = null;
        ReloadFromStore();
        Status = "已放弃未保存改动";
    }

    private IEnumerable<(ShortcutAction Action, IReadOnlyList<ShortcutGesture> Gestures)> EffectivePairs() =>
        _catalog.Select(action => (action, EffectiveGestures(action)));

    /// <summary>重新计算列表、冲突与改动状态；外部数据变化后由界面或宿主调用。</summary>
    public void Refresh()
    {
        var term = SearchText.Trim();
        var effective = EffectivePairs().ToArray();
        // 全表冲突只算一次，行标记与冲突列表共用同一结果，规则天然一致。
        var conflicts = ShortcutConflicts.Detect(effective);
        var rows = new List<ShortcutRowViewModel>();
        foreach (var action in _catalog)
        {
            if (ScopeFilter is { } scope && action.Scope != scope) continue;
            if (term.Length > 0 && !(action.Title + " " + action.Id + " " + action.CategoryOrGroup + " " + action.Scope.Title() + " " + BindingText(action))
                    .Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(BuildRow(action, conflicts));
        }

        Rows.Clear();
        foreach (var row in rows.OrderBy(r => r.Action.Scope.Rank()).ThenBy(r => r.Group, StringComparer.CurrentCulture)
                     .ThenBy(r => r.Title, StringComparer.CurrentCulture))
            Rows.Add(row);

        Conflicts.Clear();
        foreach (var conflict in conflicts)
            Conflicts.Add(new ShortcutConflictRowViewModel(
                conflict.GestureText, conflict.Scope.Title(),
                string.Join("、", conflict.ActionIds.Select(DescribeOne)), conflict.ActionIds.Count));

        IsModified = CalculateModified();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ScopeFilterText));
        OnPropertyChanged(nameof(HasLoadError));
        Changed?.Invoke();
    }

    /// <summary>与磁盘内容比对：内容不同即视为有未保存改动。</summary>
    private bool CalculateModified()
    {
        if (_storeProvider?.Invoke() is not { } store) return _userBindings.Count > 0;
        var saved = store.Current.Bindings;
        if (saved.Count != _userBindings.Count) return true;
        foreach (var pair in _userBindings)
            if (!saved.TryGetValue(pair.Key, out var stored) || !stored.SequenceEqual(pair.Value, StringComparer.Ordinal))
                return true;
        return false;
    }

    private ShortcutRowViewModel BuildRow(ShortcutAction action, IReadOnlyList<ShortcutConflict> conflicts)
    {
        var origin = OriginOf(action);
        var binding = BindingText(action);
        var conflictGestureTexts = conflicts
            .Where(conflict => conflict.ActionIds.Contains(action.Id, StringComparer.Ordinal))
            .SelectMany(conflict => conflict.ActionIds.Where(id => !string.Equals(id, action.Id, StringComparison.Ordinal)))
            .Select(DescribeOne)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var scopeNote = action.Scope == ShortcutScope.Terminal
            ? "终端输入优先：全局快捷键在此让路。"
            : action.Scope == ShortcutScope.Editor ? "仅文本框编辑焦点下生效。" : null;

        return new ShortcutRowViewModel(
            action, binding,
            origin switch { ShortcutOrigin.User => "自定义", ShortcutOrigin.Disabled => "已禁用", _ => "默认" },
            origin == ShortcutOrigin.Default, origin == ShortcutOrigin.Disabled,
            conflictGestureTexts.Length > 0,
            conflictGestureTexts.Length > 0 ? "与 " + string.Join("、", conflictGestureTexts) + " 冲突" : null,
            scopeNote,
            () => _unavailable.TryGetValue(action.Id, out var reason) ? reason() : null,
            gestureText => SetBinding(action.Id, gestureText));
    }

    private string Describe(IReadOnlyList<string> ids) => string.Join("、", ids.Select(DescribeOne));

    private string DescribeOne(string id) =>
        _catalog.FirstOrDefault(action => string.Equals(action.Id, id, StringComparison.Ordinal))?.Title ?? id;

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnScopeFilterChanged(ShortcutScope? value) => Refresh();
    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(HasLoadError));
}
