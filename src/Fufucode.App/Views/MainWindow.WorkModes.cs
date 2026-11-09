using System;
using System.Collections.Generic;
using System.Linq;
using Fufucode.App.Infrastructure;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    // Settings and auxiliary pages inherit the development mode they were opened from.
    private AppPage WorkMode => _vm?.CurrentPage is AppPage.Chat or AppPage.Graph
        ? _vm.CurrentPage : _vm?.IsSettingsPage == true && _settingsReturnPage is AppPage.Chat or AppPage.Graph
            ? _settingsReturnPage : _lastWorkMode;

    private bool DockPageAllowedInMode(string id) => _graphEntryPending
        ? id is "overview" or "notifications" or "perf"
        : !id.StartsWith("graph-", StringComparison.Ordinal) || GraphEnabled && _graphWorkspace is not null &&
            (id == "graph-minimap" || WorkMode == AppPage.Graph);

    private bool CanOpenDockPage(string id) => _workbenchPages.ContainsKey(id) && DockPageAllowedInMode(id);
    private IEnumerable<Views.WorkbenchPage> ModePages => _workbenchPages.Values.Where(page => DockPageAllowedInMode(page.Id))
        .Select(page => page.Id == "overview" && WorkMode == AppPage.Graph ? page with { Title = "项目管理" } : page);

    private string? ModeActionUnavailable(string id)
    {
        if (id == "session.new" && WorkMode == AppPage.Graph && !GraphEnabled) return "Graph 模式已停用";
        if (id == "session.side" && _graphEntryPending) return "请先选择图所属的项目";
        if (id.StartsWith("graph.", StringComparison.Ordinal) && _graphEntryPending)
            return "请先选择图所属的项目";
        if (id.StartsWith("graph.", StringComparison.Ordinal) && id != "graph.minimap" && WorkMode != AppPage.Graph)
            return "此操作仅在 Graph 模式可用";
        if (id.StartsWith("session.", StringComparison.Ordinal) && id is not ("session.new" or "session.side") && WorkMode == AppPage.Graph)
            return "此操作仅在 Chat 模式可用";
        return null;
    }

    internal void RefreshModeLayoutForTesting() => RenderDockPages();

    private void RefreshWorkModeSurfaces()
    {
        _customization?.NotifyPagesChanged();
        _actions.RefreshCapabilities();
        if (_customization is not null) BuildActionSurfaces(_customization.Current);
        RefreshSessionList();
        RenderOverview();
    }
}
