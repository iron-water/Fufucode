using Fufucode.Core;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>
    /// 新建聊天的后端：直接使用设置里的默认 Agent 内核，不再弹选择窗。
    /// 默认后端不可用时回退到 FUMO 原生内核，并返回一条如实的提示。
    /// 用户可在“设置 › 插件市场 › 后端内核”修改默认值。
    /// </summary>
    private (BackendId Backend, string? Note) ResolveNewSessionBackend()
    {
        var manager = _host!.Manager;
        var preferred = manager.PreferredBackend;
        if (manager.IsBackendAvailable(preferred))
        {
            return (preferred, null);
        }

        var name = BackendCapabilities.For(preferred).Name;
        return (BackendId.NativeCSharp,
            $"默认后端 {name} 当前不可用，本次新建使用 FUMO 原生内核。可在 设置 › 插件市场 › 后端内核 修改默认后端。");
    }
}
