using System;
using System.Linq;
using Avalonia.Threading;
using Fufucode.App.Features.Plugins;
using Fufucode.App.Infrastructure;
using Fufucode.App.Integration;
using Fufucode.App.ViewModels;

namespace Fufucode.App;

public partial class MainWindow
{
    /// <summary>
    /// 在共享外壳就绪后挂接可选插件页面。终端等页面不再由 App 静态创建：
    /// 插件未安装/未启用时页面不存在，动作会给出真实原因。
    /// </summary>
    private void InitializePluginPages()
    {
        var root = Program.SelfTestMode
            ? Program.SelfTestDataRoot ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fufucode-selftest-appdata")
            : _options.CreateAppDataLayout().Root;
        _pluginPageHost = new PluginWorkspacePageHost(root, _workbenchPages,
            () => _options.Mode == AppSessionMode.Mock);
        _pluginPageHost.PagesChanged += OnPluginPagesChanged;
        _pluginPageHost.Start(_layoutContext);
        SyncTerminalPageReference();
        PruneDockLayout();
        RenderDockPages();
    }

    private void OnPluginPagesChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(OnPluginPagesChanged); return; }
        SyncTerminalPageReference();
        PruneDockLayout();
        RenderDockPages();
        _actions.RefreshCapabilities();
    }

    /// <summary>解析当前终端页面并接线快捷键匹配；插件启停时由页面变化事件重新调用。</summary>
    private void SyncTerminalPageReference()
    {
        var next = _pluginPageHost?.TryGet("terminal", out var page) == true ? page : null;
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
