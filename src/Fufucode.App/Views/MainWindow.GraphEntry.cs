using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Plugins.AutoDev;

namespace Fufucode.App;

public partial class MainWindow
{
    private bool _graphEntryPending;
    private readonly Border _graphEntryHost = new();
    private string? CurrentWorkProjectId => _graphEntryPending ? null :
        WorkMode == AppPage.Graph ? _graphWorkspace?.Service.ReadSnapshot().ProjectId : _host?.Current.Project.Id;

    private bool HasProjectGraph(ProjectRecord project) => _graphProjects.ContainsKey(project.Id) ||
        MetadataOrMock().GetPreference("workspace-graph:" + project.Id) == "created" ||
        File.Exists(RebirthPaths.Database(GraphLayout.Root, project.Path));

    private Task NewWorkspaceAsync() => WorkMode == AppPage.Graph ? NewGraphAsync() : NewChatAsync();

    private async Task NewGraphAsync()
    {
        if (!GraphEnabled || _switching || _restarting || _closed || _vm?.Input.IsTurnActive == true) return;
        if (!await ConfirmLeaveSettingsAsync()) return;
        if (!_graphEntryPending) SaveWorkModeLayout();
        if (_host is not null && _vm is { } previous) _host.SaveDraft(previous.Input.ComposePrompt());
        _graphEntryPending = true;
        _modeLayoutProject = null;
        _settingsReturnPage = AppPage.Graph;
        _lastWorkMode = AppPage.Graph;
        _dockLayout = new();
        _projectOnlyManager ??= new PersistentSessionManager(string.Empty, string.Empty, GraphLayout);
        BuildGraphEntry();
        if (_vm is { } vm) vm.CurrentPage = AppPage.Graph;
        UpdatePageHost();
        RenderDockPages();
        RefreshWorkModeSurfaces();
        RefreshIsland();
    }

    private void BuildGraphEntry()
    {
        var picker = new Button { Content = "选择所属项目", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(picker, "graph-project-picker");
        var menu = new MenuFlyout();
        foreach (var project in OverviewProjects())
        {
            var selected = project;
            var item = new MenuItem { Header = Organization().Project(project).Name };
            item.Click += async (_, _) => await SwitchProjectAsync(selected.Path);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var folder = new MenuItem { Header = "选择项目文件夹…" };
        folder.Click += async (_, _) => await OpenProjectAsync();
        menu.Items.Add(folder);
        picker.Flyout = menu;
        var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(cancel, "graph-entry-cancel");
        cancel.Click += async (_, _) => await CancelGraphEntryAsync();
        _graphEntryHost.Child = new StackPanel
        {
            Spacing = 16, Margin = new Thickness(32), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "新建图", FontSize = 24 }, picker, cancel }
        };
    }

    private async Task CancelGraphEntryAsync()
    {
        if (!_graphEntryPending) return;
        _graphEntryPending = false;
        if (_graphWorkspace is not null)
        {
            _graphMainHost.Child = _graphWorkspace.Main;
            UpdatePageHost();
            ApplyWorkModeLayout(AppPage.Graph);
        }
        else if (_host is not null) await NavigateWorkspaceAsync(AppPage.Chat);
        else
        {
            _graphEntryPending = true;
            return;
        }
        RenderDockPages();
        RefreshWorkModeSurfaces();
    }

    internal Task NewWorkspaceForTestingAsync() => NewWorkspaceAsync();
    internal Task CancelGraphEntryForTestingAsync() => CancelGraphEntryAsync();
    internal bool GraphEntryPendingForTesting => _graphEntryPending;
}
