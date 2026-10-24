using System.Collections.Generic;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>
    /// 新建聊天的后端：主按钮使用设置里的默认 Agent 内核；会话管理页的
    /// 下拉菜单可以显式指定本次新建使用的后端（不弹模态小窗）。
    /// 指定/默认后端不可用时回退到 FUMO 原生内核，并返回一条如实的提示。
    /// </summary>
    private (BackendId Backend, string? Note) ResolveNewSessionBackend(BackendId? requested = null)
    {
        var manager = _host!.Manager;
        var preferred = requested ?? manager.PreferredBackend;
        if (manager.IsBackendAvailable(preferred))
        {
            return (preferred, null);
        }

        var name = BackendCapabilities.For(preferred).Name;
        var lead = requested is null ? "默认后端" : "所选后端";
        var tail = requested is null
            ? "可在 设置 › 插件市场 › 后端内核 修改默认后端。"
            : "请先在 设置 › 插件市场 › 后端内核 安装并启用该插件。";
        return (BackendId.NativeCSharp,
            $"{lead} {name} 当前不可用，本次新建使用 FUMO 原生内核。{tail}");
    }

    /// <summary>会话管理页“新建会话”下拉：六类后端全部列出，不可用的条目禁用，默认后端标记并排第一。</summary>
    private static IReadOnlyList<SidebarViewModel.NewSessionBackendOption> BuildNewSessionBackendOptions(
        RealSessionHost host)
    {
        var manager = host.Manager;
        var preferred = manager.PreferredBackend;
        var options = new System.Collections.Generic.List<SidebarViewModel.NewSessionBackendOption>
        {
            Option(BackendId.NativeCSharp, "FUMO"),
            Option(BackendId.Pi, "Pi"),
            Option(BackendId.Omp, "OMP"),
            Option(BackendId.Codex, "Codex"),
            Option(BackendId.Dsh, "DSH"),
            Option(BackendId.Claude, "Claude"),
        };
        // 默认后端排首位，其余保持声明顺序。
        var index = options.FindIndex(option => option.Key == BackendCapabilities.StorageId(preferred));
        if (index > 0)
        {
            var preferredOption = options[index];
            options.RemoveAt(index);
            options.Insert(0, preferredOption);
        }
        return options;

        SidebarViewModel.NewSessionBackendOption Option(BackendId backend, string label)
        {
            var available = manager.IsBackendAvailable(backend);
            return new(BackendCapabilities.StorageId(backend),
                available ? label : label + "（不可用）", available, backend == preferred);
        }
    }

    /// <summary>默认后端在设置里改动后刷新会话管理条上的当前后端显示。</summary>
    private void RefreshNewSessionBackends()
    {
        if (_host is { } host && _vm is { } vm) vm.Sidebar.SetBackends(BuildNewSessionBackendOptions(host));
    }
}
