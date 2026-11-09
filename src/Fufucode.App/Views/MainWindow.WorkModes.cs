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

    private bool DockPageAllowedInMode(string id)
    {
        if (id == "graph-structure") return false;
        if (_graphEntryPending) return id is "overview" or "notifications";
        if (WorkMode == AppPage.Graph)
            return id is "overview" or "notifications" or "sidechat" ||
                GraphEnabled && _graphWorkspace is not null && (id is "graph-properties" or "graph-tickets" or "graph-history");
        return !id.StartsWith("graph-", StringComparison.Ordinal) ||
            GraphEnabled && _graphWorkspace is not null && id == "graph-minimap";
    }

    private bool CanOpenDockPage(string id) => _workbenchPages.ContainsKey(id) && DockPageAllowedInMode(id);
    private IEnumerable<Views.WorkbenchPage> ModePages => _workbenchPages.Values.Where(page => DockPageAllowedInMode(page.Id));

    private static string DockPageActionId(string id) => id switch
    {
        "graph.properties" => "graph-properties", "graph.tickets" => "graph-tickets",
        "graph.history" => "graph-history", "graph.minimap" => "graph-minimap",
        _ => id.StartsWith("page.", StringComparison.Ordinal) ? id[5..] : id.Split('.')[0]
    };
    private bool IsDockPageAction(string id) => _workbenchPages.KnownPageIds.Contains(DockPageActionId(id));

    private string? ModeActionUnavailable(string id)
    {
        if (IsDockPageAction(id) && !DockPageAllowedInMode(DockPageActionId(id))) return "此页面不属于当前工作模式";
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
