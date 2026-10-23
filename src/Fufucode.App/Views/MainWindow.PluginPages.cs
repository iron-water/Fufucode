using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Fufucode.App.Features.Plugins;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;
using Fufucode.Core;
using Fufucode.Core.Plugins;
using Fufucode.App.Views;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>由插件页自动注册的动作：pageId → actionId。纯代码贡献的页面也能进入统一动作面。</summary>
    private readonly Dictionary<string, string> _pluginPageActions = new(StringComparer.Ordinal);

    internal PluginPackageManager? PluginManager => _pluginPageHost?.Manager;
    internal ChatPage ChatPageForPlugins => _chatPage;
    /// <summary>
    /// 在共享外壳就绪后挂接可选插件页面。终端等页面不再由 App 静态创建：
    /// 插件未安装/未启用时页面不存在，动作会给出真实原因。
    /// </summary>
    private void InitializePluginPages()
    {
        var root = Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;
        if (Program.SelfTestMode)
        {
            SelfTest.SelfTestTerminalPlugin.EnsureInstalled(root);
            SelfTest.SelfTestOptionalPlugins.EnsureInstalled(root);
        }

        // 默认随包插件先把载荷同步到数据根，页面宿主的激活扫描才能找到它。
        PluginBootstrapper.EnsureBundled(new AppDataLayout(root));
        _pluginPageHost = new PluginWorkspacePageHost(root, _workbenchPages,
            () => _options.Mode == AppSessionMode.Mock);
        _chatPage.ComposerPluginHost = new Features.Plugins.PluginComposerHost(this);
        _pluginPageHost.ComposerContributionsChanged += _chatPage.RefreshComposerPluginMenu;
        // 浏览器页面由 workspace.browser 插件贡献；宿主只向插件暴露受控的页面工厂。
        var priorResolver = _pluginPageHost.Manager.ServiceResolver;
        _pluginPageHost.Manager.ServiceResolver = type =>
            type == typeof(IBrowserPageFactory) || type == typeof(ILibraryPageFactory) ||
            type == typeof(ILocalReviewPageFactory)
                ? this
                : type == typeof(Fufucode.Core.Plugins.IComputerUseCursorRegistry)
                    ? _computerCursors
                    : priorResolver?.Invoke(type);
        _pluginPageHost.PagesChanged += OnPluginPagesChanged;
        // 性能监控插件页需要宿主服务（页面工厂、设置注册），必须在 InitializeInstalledPlugins 之前接好。
        InitializePerformance(root);
        if (_perfWiring is { } wiring)
        {
            wiring.Attach(_pluginPageHost.Manager);
            // 插件可能在服务就位前已激活（没有页面贡献）：让管理器重新构造贡献后再由 Start 统一同步。
            wiring.PrepareForResync();
            _pluginPageHost.Manager.RequestPageResync();
        }
        _pluginPageHost.Start(_layoutContext);
        SyncTerminalPageReference();
        SyncPerformanceSettings();
        PruneDockLayout();
        RenderDockPages();
        SyncPluginPageActions();
        _customization.SetActions(_actions.Actions);
        _customization.NotifyPagesChanged();
        _perfWiring?.OnPluginsSynced();
    }

    /// <summary>
    /// 为没有专用动作的插件页自动生成“打开 X”动作，并移除已消失插件的动作。
    /// 已有专用动作（terminal/perf 等）不被覆盖，保证动作语义仍由公共目录定义。
    /// </summary>
    private void SyncPluginPageActions()
    {
        if (_pluginPageHost is null) return;
        var active = _pluginPageHost.ActivePages.Values
            .GroupBy(page => page.PageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var (pageId, actionId) in _pluginPageActions.ToArray())
        {
            if (active.ContainsKey(pageId)) continue;
            _actions.Unregister(actionId);
            _pluginPageActions.Remove(pageId);
        }

        foreach (var page in active.Values)
        {
            if (_actions.Contains(page.PageId) || _actions.Contains("page." + page.PageId)) continue;
            var actionId = "page." + page.PageId;
            if (_pluginPageActions.Values.Contains(actionId, StringComparer.Ordinal)) continue;
            var title = _workbenchPages.ContainsKey(page.PageId) ? _workbenchPages[page.PageId].Title : page.PageId;
            _actions.Register(new AppActionDefinition(actionId, "打开" + title, "工作台", "",
                "打开插件贡献的工作区页面",
                () => { OpenDockPage(page.PageId, DockZone.Right); return Task.CompletedTask; },
                () => _workbenchPages.ContainsKey(page.PageId) ? null : "插件页面当前不可用。"));
            _pluginPageActions[page.PageId] = actionId;
        }
    }

    private void OnPluginPagesChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(OnPluginPagesChanged); return; }
        SyncTerminalPageReference();
        SyncPerformanceSettings();
        PruneDockLayout();
        RenderDockPages();
        SyncPluginPageActions();
        _actions.RefreshCapabilities();
        _chatPage.RefreshComposerPluginMenu();
        _customization.SetActions(_actions.Actions);
        _customization.NotifyPagesChanged();
        _perfWiring?.OnPluginsSynced();
    }

    /// <summary>解析当前终端页面并接线快捷键匹配；插件启停时由页面变化事件重新调用。</summary>
    private void SyncTerminalPageReference()
    {
        var next = _pluginPageHost?.TryGet("terminal", out var page) == true ? page : null;
        var gained = _terminalPage is null && next is not null;
        if (!ReferenceEquals(next, _terminalPage))
        {
            if (_terminalPage is not null) _terminalPage.StateChanged -= OnPluginPageStateChanged;
            _terminalPage = next;
            if (_terminalPage is not null)
            {
                _terminalPage.StateChanged += OnPluginPageStateChanged;
                _terminalPage.SetProject(_layoutContext, _options.Mode == AppSessionMode.Mock);
                _terminalPage.ShortcutMatches = (id, e) => _shortcutActions.FirstOrDefault(action => action.Id == id) is { } action &&
                    _shortcuts.EffectiveGestures(action).Any(gesture => gesture.Key == e.Key && gesture.Modifiers == e.KeyModifiers);
            }
        }
        // 默认不预置终端页签；插件安装/启用后才把它放进底部停靠区。
        // 只在“从无到有”时打开一次，用户之后手动关闭不再强行弹回。
        if (gained && _dockLayout.Find("terminal") is null) OpenDockPage("terminal", DockZone.Bottom);
        else _terminalPage?.SetProject(_layoutContext, _options.Mode == AppSessionMode.Mock);
        if (_shortcuts is not null) UpdateShortcutLabels();
        _actions.RefreshCapabilities();
    }

    /// <summary>移除布局里已不存在的页面（插件未安装/被停用），避免留下死页签或空停靠区。</summary>
    private void PruneDockLayout()
    {
        foreach (var zone in new[] { DockZone.Left, DockZone.Right, DockZone.Bottom })
        {
            var pages = _dockLayout.Pages[zone];
            pages.RemoveAll(id => !_workbenchPages.ContainsKey(id));
            if (_dockLayout.Selected[zone] is { } selected && !_workbenchPages.ContainsKey(selected))
                _dockLayout.Selected[zone] = pages.FirstOrDefault();
            if (pages.Count == 0) _dockLayout.Hidden[zone] = true;
        }
        var known = _workbenchPages.KnownPageIds.ToHashSet(StringComparer.Ordinal);
        foreach (var floating in _dockLayout.Floating)
        {
            floating.Pages.RemoveAll(id => !known.Contains(id));
            if (floating.Selected is { } selected && !known.Contains(selected)) floating.Selected = floating.Pages.FirstOrDefault();
        }
        _dockLayout.Floating.RemoveAll(floating => floating.Pages.Count == 0);
    }
}
