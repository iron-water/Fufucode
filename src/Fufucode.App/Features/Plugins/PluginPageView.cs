using System;
using Avalonia.Controls;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 非 Fufucode.App 程序集的插件通过本适配器贡献工作区页面：
/// 只需要提供标题、图标、页面工厂和项目切换/释放回调，不实现宿主内部的 <see cref="IPluginWorkspacePage"/>。
/// 页面工厂可以返回宿主页契约，也可以返回裸 Avalonia 控件（后者由适配器包装）。
/// 终端仍实现完整契约；新插件用本适配器即可。
/// </summary>
public sealed class PluginPageView : IPluginPageProvider
{
    private readonly Func<object?> _createPluginPage;
    private readonly Action<string?> _setProject;
    private readonly Action? _onDetached;

    public PluginPageView(string pageId, string title, string iconKey, string moduleId,
        Func<object?> createPluginPage, Action<string?>? setProject = null, Action? onDetached = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        PageId = pageId;
        Title = title;
        IconKey = iconKey;
        ModuleId = moduleId;
        _createPluginPage = createPluginPage ?? throw new ArgumentNullException(nameof(createPluginPage));
        _setProject = setProject ?? (_ => { });
        _onDetached = onDetached;
    }

    public string PageId { get; }
    public string Title { get; }
    public string IconKey { get; }
    public string ModuleId { get; }

    public PluginPageContribution Contribution => new(
        PageId, Title, IconKey, ModuleId, CreatePluginPage, DetachView: _ => _onDetached?.Invoke());

    /// <summary>创建插件页面对象：宿主契约直接使用，裸控件包装成契约。</summary>
    public object CreatePluginPage()
    {
        var created = _createPluginPage();
        return created switch
        {
            IPluginWorkspacePage page => page,
            Control control => new AdaptedPage(this, control),
            _ => throw new InvalidOperationException(
                $"页面插件 '{ModuleId}' 返回了既不是控件也不是宿主页面契约的对象。"),
        };
    }

    /// <summary>把裸控件包装成宿主页面契约；放置与布局完全由宿主停靠区负责。</summary>
    internal sealed class AdaptedPage(PluginPageView source, Control view) : IPluginWorkspacePage
    {
        public string PageId => source.PageId;
        public Control View { get; } = view;
        public bool IsVisible { get; private set; } = true;

        public event EventHandler? StateChanged { add { } remove { } }

        public void Show() => IsVisible = true;
        public void Hide() => IsVisible = false;
        public void SetProject(string? projectPath, bool useMock) => source._setProject(projectPath);

        public Func<string, Avalonia.Input.KeyEventArgs, bool>? ShortcutMatches { get; set; }
        public void UpdateShortcutLabels(Func<string, string> formatter) { }
        public bool HasTabs => false;
        public bool HasRunningTab => false;
        public bool HasOutputSelection => false;
        public Control? CommandInput => null;
        public System.Threading.Tasks.Task CopySelectionAsync() => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task PasteToDraftAsync() => System.Threading.Tasks.Task.CompletedTask;
        public void FindOutput() { }
        public void ReturnToBottom() { }
        public System.Threading.Tasks.Task CopyOrInterruptAsync() => System.Threading.Tasks.Task.CompletedTask;
        public void Interrupt() { }
        public void CreateTerminal() { }
        public System.Threading.Tasks.Task CloseSelectedAsync() => System.Threading.Tasks.Task.CompletedTask;

        public void Dispose()
        {
            if (View is IDisposable disposable) disposable.Dispose();
        }
    }
}
