using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fufucode.App.Infrastructure;

namespace Fufucode.App.ViewModels;

public sealed record CommandPaletteItemViewModel(AppActionDefinition Action, string? DisabledReason, bool ShowGroupHeader)
{
    public string Id => Action.Id;
    public string Title => Action.Title;
    public string Group => Action.Group;
    public string Shortcut => Action.Shortcut;
    public string Description => Action.Description;
    public bool HasShortcut => !string.IsNullOrWhiteSpace(Shortcut);
    public bool IsEnabled => DisabledReason is null;
    public bool HasDisabledReason => DisabledReason is not null;
    public bool IsDestructive => Action.IsDestructive;
}

/// <summary>Global UI actions only; Pi slash commands remain in the composer.</summary>
public sealed partial class CommandPaletteViewModel : ViewModelBase, IDisposable
{
    public CommandPaletteViewModel(AppActionRegistry registry)
    {
        Registry = registry;
        Registry.Changed += OnRegistryChanged;
        Refresh();
    }
    public AppActionRegistry Registry { get; }
    public ObservableCollection<CommandPaletteItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private CommandPaletteItemViewModel? _selectedItem;
    [ObservableProperty] private string? _feedback;
    public bool HasNoMatches => Items.Count == 0;
    public bool HasFeedback => !string.IsNullOrEmpty(Feedback);
    public event Action? Opened;
    public event Action? CloseRequested;
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
        // Observable setters use record equality; clear the old row before rebuilding
        // so selection always refers to the row that is actually in Items.
        SelectedItem = null;
        var query = SearchText.Trim();
        var source = Registry.Actions.Where(action => query.Length == 0 ||
                (action.Title + " " + action.Group + " " + action.Shortcut + " " + action.Description)
                .Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(action => action.Group, StringComparer.CurrentCulture).ThenBy(action => action.Title, StringComparer.CurrentCulture);
        Items.Clear();
        string? previousGroup = null;
        foreach (var action in source)
        {
            Items.Add(new(action, Registry.GetDisabledReason(action.Id), action.Group != previousGroup));
            previousGroup = action.Group;
        }
        SelectedItem = Items.FirstOrDefault(item => item.Id == selectedId) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoMatches));
    }
    public void Dispose() => Registry.Changed -= OnRegistryChanged;
}
