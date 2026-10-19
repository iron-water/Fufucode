using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 可选插件贡献的工作区页面在宿主侧的最小契约。宿主不静态引用插件程序集；
/// 插件页面由 <see cref="Fufucode.Core.Plugins.PluginPageContribution.CreateView"/> 创建，
/// 若同时实现本接口，宿主会把它接入停靠页、全局动作与快捷键作用域。
/// </summary>
public interface IPluginWorkspacePage : IDisposable
{
    /// <summary>页面标识，与 <c>PluginPageContribution.PageId</c> 一致。</summary>
    string PageId { get; }

    /// <summary>可放入停靠页的内容控件。</summary>
    Control View { get; }

    /// <summary>页面可见性或能力变化（例如选中页签、是否有选区）。</summary>
    event EventHandler? StateChanged;

    bool IsVisible { get; }
    void Show();
    void Hide();

    /// <summary>切换项目；插件按项目路径保留自己的页面状态。</summary>
    void SetProject(string? projectPath, bool useMock);

    /// <summary>终端作用域快捷键匹配；没有该概念的可选页面返回 null。</summary>
    Func<string, KeyEventArgs, bool>? ShortcutMatches { get; set; }

    /// <summary>把当前快捷键绑定写进页面提示。</summary>
    void UpdateShortcutLabels(Func<string, string> binding);

    bool HasTabs { get; }
    bool HasRunningTab { get; }
    bool HasOutputSelection { get; }

    /// <summary>页面内的命令输入框；用于“复制或中断”快捷键在文本选择时放行编辑器复制。</summary>
    Control? CommandInput { get; }

    Task CopySelectionAsync();
    Task PasteToDraftAsync();
    void FindOutput();
    void ReturnToBottom();
    Task CopyOrInterruptAsync();
    void Interrupt();
    void CreateTerminal();
    Task CloseSelectedAsync();
}
