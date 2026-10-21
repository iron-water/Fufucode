using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Fufucode.App.Views;
using Fufucode.Core;
using Fufucode.Core.Plugins;

namespace Fufucode.App.Features.Plugins;

/// <summary>
/// 把插件市场里"已安装且已启用"的插件工作区页面接入停靠区。
/// 宿主不静态引用任何页面插件；页面由 <see cref="IPluginPackage.CreatePageContribution"/> 提供，
/// 未安装/未启用/加载失败时页面不存在，而不是显示一个空壳。
/// </summary>
internal sealed class PluginWorkspacePageHost : IDisposable
{
    private readonly PluginPackageManager _manager;
    private readonly WorkbenchPageRegistry _pages;
    private readonly Dictionary<string, IPluginWorkspacePage> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _loadErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<bool> _useMock;
    private string? _projectPath;
    private bool _started;
    private bool _disposed;

    /// <summary>性能监控插件 id：页面由宿主包装（持久化 sink、设置页内容、导出动作）。</summary>
    public const string PerformancePluginId = "performance.monitor";

    public PluginWorkspacePageHost(string dataRoot, WorkbenchPageRegistry pages, Func<bool> useMock,
        PluginPackageManager? manager = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _pages = pages ?? throw new ArgumentNullException(nameof(pages));
        _useMock = useMock ?? throw new ArgumentNullException(nameof(useMock));
        if (manager is not null)
        {
            _manager = manager;
        }
        else
        {
            var layout = new AppDataLayout(dataRoot);
            var gateway = new PluginCapabilityGateway(new PluginStateStore(layout));
            _manager = new PluginPackageManager(layout, new PluginToolRegistry(gateway));
        }
        _manager.PageContributionsChanged += OnContributionsChanged;
    }

    /// <summary>页面集合变化（插件启停）。在 UI 线程触发；宿主应重新渲染停靠区并清理布局。</summary>
    public event Action? PagesChanged;

    public IReadOnlyDictionary<string, IPluginWorkspacePage> ActivePages => _active;

    /// <summary>底层插件管理器（宿主组合根据此注入服务解析器）。</summary>
    public PluginPackageManager Manager => _manager;

    /// <summary>插件加载失败原因（按插件 ID）；成功时不存在。</summary>
    public IReadOnlyDictionary<string, string> LoadErrors => _loadErrors;

    public bool TryGet(string pageId, out IPluginWorkspacePage page)
    {
        foreach (var item in _active.Values)
        {
            if (string.Equals(item.PageId, pageId, StringComparison.Ordinal))
            {
                page = item;
                return true;
            }
        }
        page = null!;
        return false;
    }

    public void Start(string? projectPath)
    {
        if (_started || _disposed) return;
        _started = true;
        _projectPath = projectPath;
        _manager.InitializeInstalledPlugins(projectPath);
        Sync();
    }

    public void SetProject(string? projectPath, bool useMock)
    {
        _projectPath = projectPath;
        foreach (var page in _active.Values) page.SetProject(projectPath, useMock);
    }

    private void OnContributionsChanged()
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) Sync();
        else Dispatcher.UIThread.Post(Sync);
    }

    private void Sync()
    {
        if (_disposed) return;
        var contributions = _manager.PageContributions;
        var removed = false;
        foreach (var pluginId in _active.Keys.ToArray())
            if (!contributions.ContainsKey(pluginId)) { Remove(pluginId); removed = true; }

        var added = false;
        foreach (var (pluginId, contribution) in contributions)
        {
            if (_active.ContainsKey(pluginId)) continue;
            // 内建页壳可以保留同一页面 ID（automation 的页面容器由 App 持有，插件只提供内容）。
            // 这类贡献不注册新页，由专用宿主消费，避免把正常协作记成加载失败。
            if (_pages.KnownPageIds.Contains(contribution.PageId, StringComparer.Ordinal)) continue;
            try
            {
                var created = contribution.CreateView();
                // 插件可以返回完整契约对象，也可以返回页面提供者（PluginPageView：不引用 Fufucode.App 的插件走这条）。
                if (created is IPluginPageProvider provider) created = provider.CreatePluginPage();
                if (created is not IPluginWorkspacePage page)
                    throw new InvalidOperationException("页面贡献没有实现宿主工作区页面契约。");
                page.SetProject(_projectPath, _useMock());
                try
                {
                    _pages.RegisterPlugin(contribution.PageId, contribution.Title, contribution.IconKey,
                        contribution.ModuleId, page.View, _ => page.Dispose());
                }
                catch (InvalidOperationException error) when (error.Message.Contains("already reserved", StringComparison.Ordinal))
                {
                    // 内建页壳可以预留同一页面 ID（例如 automation），插件只负责提供内容；
                    // 不能让插件注册失败路径删除或破坏核心页注册。
                    _loadErrors[pluginId] = "页面 ID 已由内建宿主保留：" + contribution.PageId;
                    page.Dispose();
                    continue;
                }
                _active[pluginId] = page;
                page.StateChanged += OnPageStateChanged;
                added = true;
            }
            catch (Exception error)
            {
                // 载荷存在但页面构造失败：记录原因并且不留半成品页面。
                _pages.Unregister(contribution.PageId);
                _loadErrors[pluginId] = error.GetBaseException().Message;
            }
        }
        if (added || removed) PagesChanged?.Invoke();
    }

    private void OnPageStateChanged(object? sender, EventArgs e) => PagesChanged?.Invoke();

    private void Remove(string pluginId)
    {
        if (!_active.Remove(pluginId, out var page)) return;
        page.StateChanged -= OnPageStateChanged;
        _pages.Unregister(page.PageId); // release 回调负责 Dispose
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _manager.PageContributionsChanged -= OnContributionsChanged;
        foreach (var pluginId in _active.Keys.ToArray()) Remove(pluginId);
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
