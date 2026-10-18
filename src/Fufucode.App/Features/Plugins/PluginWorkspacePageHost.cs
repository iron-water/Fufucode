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
            try
            {
                if (contribution.CreateView() is not IPluginWorkspacePage page)
                    throw new InvalidOperationException("页面贡献没有实现宿主工作区页面契约。");
                page.SetProject(_projectPath, _useMock());
                _active[pluginId] = page;
                _pages.RegisterPlugin(contribution.PageId, contribution.Title, contribution.IconKey,
                    contribution.ModuleId, page.View, _ => page.Dispose());
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
