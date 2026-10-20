using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

public enum CommandPaletteItemKind
{
    Action,
    Session,
    Setting,
}

/// <summary>命令面板的一行：全局动作、会话或设置项共用同一列表与键盘导航。</summary>
public sealed record CommandPaletteItemViewModel(
    string Id,
    string Title,
    string Group,
    string Description,
    string Shortcut,
    string? DisabledReason,
    bool IsDestructive,
    bool ShowGroupHeader,
    CommandPaletteItemKind Kind)
{
    /// <summary>只参与搜索、不显示的补充词（设置项的中英文搜索词）。</summary>
    public string SearchText { get; init; } = string.Empty;

    public bool HasShortcut => !string.IsNullOrWhiteSpace(Shortcut);
    public bool IsEnabled => DisabledReason is null;
    public bool HasDisabledReason => DisabledReason is not null;
}

/// <summary>
/// 统一命令面板：全局动作、当前会话与设置导航共用 Ctrl+K。
/// 输入框斜杠命令仍归 composer，不混入本面板。
/// </summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase, IDisposable
{
    public CommandPaletteViewModel(AppActionRegistry registry)
    {
        Registry = registry;
        Registry.Changed += OnRegistryChanged;
        Refresh();
    }

    public AppActionRegistry Registry { get; }

    /// <summary>会话结果来源；由窗口组合根注入，返回 null 或空集合时不显示会话组。</summary>
    public Func<IReadOnlyList<CommandPaletteItemViewModel>>? SessionSource { get; set; }

    /// <summary>设置导航结果来源；由窗口组合根注入。</summary>
    public Func<IReadOnlyList<CommandPaletteItemViewModel>>? SettingSource { get; set; }

    public ObservableCollection<CommandPaletteItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private CommandPaletteItemViewModel? _selectedItem;
    [ObservableProperty] private string? _feedback;
    public bool HasNoMatches => Items.Count == 0;
    public bool HasFeedback => !string.IsNullOrEmpty(Feedback);
    public event Action? Opened;
    public event Action? CloseRequested;
    public event Action<string>? SessionActivated;
    public event Action<string>? SettingActivated;
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnFeedbackChanged(string? value) => OnPropertyChanged(nameof(HasFeedback));

    public void Open()
    {
        SelectedItem = null;
        SearchText = string.Empty;
        Feedback = null;
        Refresh();
        IsOpen = true;
        Opened?.Invoke();
    }

    [RelayCommand] private void Close() { IsOpen = false; CloseRequested?.Invoke(); }

    public void MoveSelection(int offset)
    {
        if (Items.Count == 0) return;
        var index = SelectedItem is null ? 0 : Items.IndexOf(SelectedItem);
        SelectedItem = Items[(Math.Max(index, 0) + offset + Items.Count) % Items.Count];
    }

    [RelayCommand]
    private async Task ExecuteSelectedAsync()
    {
        if (SelectedItem is not { } selected) return;
        if (selected.Kind == CommandPaletteItemKind.Session)
        {
            if (SessionActivated is null) { Feedback = "没有可打开的会话。"; return; }
            SessionActivated(selected.Id);
            Close();
            return;
        }
        if (selected.Kind == CommandPaletteItemKind.Setting)
        {
            if (SettingActivated is null) { Feedback = "设置尚未就绪。"; return; }
            SettingActivated(selected.Id);
            Close();
            return;
        }
        var result = await Registry.ExecuteAsync(selected.Id);
        Feedback = result.Reason;
        if (result.Executed) Close();
    }

    private void OnRegistryChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess()) Dispatcher.UIThread.Post(Refresh);
        else Refresh();
    }

    public void Refresh()
    {
        var selectedId = SelectedItem?.Id;
        var selectedKind = SelectedItem?.Kind;
        // Observable setters use record equality; clear the old row before rebuilding
        // so selection always refers to the row that is actually in Items.
        SelectedItem = null;
        var query = SearchText.Trim();
        Items.Clear();
        string? previousGroup = null;

        void Append(IEnumerable<CommandPaletteItemViewModel> source)
        {
            foreach (var item in source)
            {
                var showHeader = item.Group != previousGroup;
                Items.Add(item with { ShowGroupHeader = showHeader });
                previousGroup = item.Group;
            }
        }

        Append(Registry.Actions
            .Where(action => Matches(query, action.Title, action.Group, action.Shortcut, action.Description))
            .OrderBy(action => action.Group, StringComparer.CurrentCulture)
            .ThenBy(action => action.Title, StringComparer.CurrentCulture)
            .Select(action => new CommandPaletteItemViewModel(
                action.Id, action.Title, action.Group, action.Description, action.Shortcut,
                Registry.GetDisabledReason(action.Id), action.IsDestructive, false, CommandPaletteItemKind.Action)));

        if (SessionSource?.Invoke() is { } sessions)
            Append(sessions.Where(item => Matches(query, item.Title, item.Group, item.Description, item.Shortcut, item.SearchText)));

        if (SettingSource?.Invoke() is { } settings)
            Append(settings.Where(item => Matches(query, item.Title, item.Group, item.Description, item.Shortcut, item.SearchText)));

        SelectedItem = Items.FirstOrDefault(item => item.Id == selectedId && item.Kind == selectedKind)
            ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private static bool Matches(string query, params string?[] fields) =>
        query.Length == 0 || fields.Any(field =>
            field is not null && field.Contains(query, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => Registry.Changed -= OnRegistryChanged;
}
