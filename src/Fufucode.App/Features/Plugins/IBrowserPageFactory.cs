using Avalonia.Controls;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 浏览器页面由 App 组合根创建，插件只负责声明页面贡献和生命周期。
/// 这样 WebView2、设置桥和聊天引用仍由宿主接线，插件不必复制产品 UI。
/// </summary>
public interface IBrowserPageFactory
{
    IPluginWorkspacePage CreatePage();
}

/// <summary>把现有 BrowserView 包装成插件页面契约；SetProject/Show/Hide 由 BrowserView 自己管理。</summary>
internal sealed class BrowserPluginPage : IPluginWorkspacePage
{
    private readonly Control _view;
    private readonly System.Action _dispose;

    public BrowserPluginPage(Control view, System.Action dispose)
    {
        _view = view;
        _dispose = dispose;
    }

    public string PageId => "browser";
    public Control View => _view;
    public bool IsVisible => _view.IsEffectivelyVisible;
    public bool HasTabs => false;
    public bool HasRunningTab => false;
    public bool HasOutputSelection => false;
    public Control? CommandInput => null;
    public System.Func<string, Avalonia.Input.KeyEventArgs, bool>? ShortcutMatches { get; set; }
    public event System.EventHandler? StateChanged;
    public void SetProject(string? projectPath, bool useMock) => StateChanged?.Invoke(this, System.EventArgs.Empty);
    public void Show() => StateChanged?.Invoke(this, System.EventArgs.Empty);
    public void Hide() => StateChanged?.Invoke(this, System.EventArgs.Empty);
    public void UpdateShortcutLabels(System.Func<string, string> binding) { }
    public System.Threading.Tasks.Task CopySelectionAsync() => System.Threading.Tasks.Task.CompletedTask;
    public System.Threading.Tasks.Task PasteToDraftAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void FindOutput() { }
    public void ReturnToBottom() { }
    public System.Threading.Tasks.Task CopyOrInterruptAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void Interrupt() { }
    public void CreateTerminal() { }
    public System.Threading.Tasks.Task CloseSelectedAsync() => System.Threading.Tasks.Task.CompletedTask;
    public void Dispose() => _dispose();
}
