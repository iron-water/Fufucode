using System.Collections.ObjectModel;
using Avalonia.Controls;
using Fufucode.Fufu.Bot;

namespace Fufucode.Fufu.Pet;

public sealed record MemoryScopeRow(ResidentMemoryScope Scope, string Name);

/// <summary>Resident Core 记忆管理窗口；Markdown 直接可编辑。</summary>
public sealed partial class ResidentMemoryWindow : Window
{
    private readonly ResidentCore _resident;
    private readonly ObservableCollection<MemoryScopeRow> _scopes = new();
    private ResidentMemoryScope _selected;

    public ResidentMemoryWindow(ResidentCore resident)
    {
        _resident = resident;
        InitializeComponent();
        ScopeList.ItemsSource = _scopes;
        foreach (var scope in Enum.GetValues<ResidentMemoryScope>())
            _scopes.Add(new(scope, scope switch
            {
                ResidentMemoryScope.Identity => "FUFU 身份",
                ResidentMemoryScope.User => "用户记忆",
                ResidentMemoryScope.Project => "项目记忆",
                ResidentMemoryScope.Agent => "Agent 记忆",
                _ => scope.ToString(),
            }));
        ScopeList.SelectionChanged += (_, _) => LoadSelected();
        SaveMemoryButton.Click += (_, _) => SaveSelected();
        RefreshButton.Click += (_, _) => LoadSelected();
        CloseButton.Click += (_, _) => Close();
        ScopeList.SelectedIndex = 0;
    }

    private void LoadSelected()
    {
        if (ScopeList.SelectedItem is not MemoryScopeRow row) return;
        _selected = row.Scope;
        ScopeTitle.Text = row.Name;
        MemoryBox.Text = _resident.ReadMemory(_selected);
    }

    private void SaveSelected()
    {
        _resident.WriteMemory(_selected, MemoryBox.Text ?? string.Empty);
        _resident.Observe("user", "memory.edited", _selected.ToString(), "memory edited in resident window");
    }
}
