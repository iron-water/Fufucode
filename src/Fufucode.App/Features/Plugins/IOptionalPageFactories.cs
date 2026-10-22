using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;

namespace Fufucode.App.Features.Plugins;

/// <summary>资料库页面由 App 组合根创建；插件只声明安装生命周期。</summary>
public interface ILibraryPageFactory
{
    IPluginWorkspacePage CreatePage();
}

/// <summary>本地审查页面由 App 组合根创建；插件只声明安装生命周期。</summary>
public interface ILocalReviewPageFactory
{
    IPluginWorkspacePage CreatePage();
}

/// <summary>把 App 内已有页面包装成插件页面契约，集中处理项目切换与释放。</summary>
internal sealed class PluginPageAdapter : IPluginWorkspacePage
{
    private readonly Control _view;
    private readonly Action _dispose;
    private readonly Action<string?>? _setProject;

    public PluginPageAdapter(string pageId, Control view, Action dispose, Action<string?>? setProject = null)
    {
        PageId = pageId;
        _view = view;
        _dispose = dispose;
        _setProject = setProject;
    }

    public string PageId { get; }
    public Control View => _view;
    public bool IsVisible => _view.IsEffectivelyVisible;
    public bool HasTabs => false;
    public bool HasRunningTab => false;
    public bool HasOutputSelection => false;
    public Control? CommandInput => null;
    public Func<string, KeyEventArgs, bool>? ShortcutMatches { get; set; }
    public event EventHandler? StateChanged;
    public void SetProject(string? projectPath, bool useMock) { _setProject?.Invoke(projectPath); StateChanged?.Invoke(this, EventArgs.Empty); }
    public void Show() => StateChanged?.Invoke(this, EventArgs.Empty);
    public void Hide() => StateChanged?.Invoke(this, EventArgs.Empty);
    public void UpdateShortcutLabels(Func<string, string> binding) { }
    public Task CopySelectionAsync() => Task.CompletedTask;
    public Task PasteToDraftAsync() => Task.CompletedTask;
    public void FindOutput() { }
    public void ReturnToBottom() { }
    public Task CopyOrInterruptAsync() => Task.CompletedTask;
    public void Interrupt() { }
    public void CreateTerminal() { }
    public Task CloseSelectedAsync() => Task.CompletedTask;
    public void Dispose() => _dispose();
}
