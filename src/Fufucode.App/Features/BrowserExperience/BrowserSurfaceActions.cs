using System;
using System.Collections.Generic;
using Fufucode.App.Views;

namespace Fufucode.App.Features.BrowserExperience;

/// <summary>
/// 浏览器页面可提供的动作/键盘匹配接缝。本包只提供目录与执行入口，不改共享动作表/快捷键表；
/// M 可据此把动作 ID 接进公共动作目录与 <c>ShortcutScope.Browser</c>，或保留本模块的独立回退。
/// 每个 ID 都对应真实实现，不存在“按钮接空函数”。
/// </summary>
internal sealed record BrowserSurfaceActionDescriptor(
    string Id,
    string Title,
    string Scope,
    string DefaultGesture,
    string Description);

internal static class BrowserSurfaceActions
{
    /// <summary>与公共快捷键范围一致的唯一作用域名。</summary>
    public const string ScopeName = "浏览器";

    public const string NewTab = "browser.new-tab";
    public const string CloseTab = "browser.close-tab";
    public const string Back = "browser.back";
    public const string Forward = "browser.forward";
    public const string Reload = "browser.reload";
    public const string Stop = "browser.stop";
    public const string Find = "browser.find";
    public const string ZoomIn = "browser.zoom-in";
    public const string ZoomOut = "browser.zoom-out";
    public const string ZoomReset = "browser.zoom-reset";
    public const string CopyAddress = "browser.copy-address";
    public const string OpenExternal = "browser.open-external";
    public const string DevTools = "browser.devtools";

    public static IReadOnlyList<BrowserSurfaceActionDescriptor> Catalog { get; } = new[]
    {
        new BrowserSurfaceActionDescriptor(NewTab, "新建浏览器页签", ScopeName, "Ctrl+T", "在浏览器页新建独立页签"),
        new BrowserSurfaceActionDescriptor(CloseTab, "关闭当前浏览器页签", ScopeName, "Ctrl+W", "只关闭当前页签并释放其原生宿主"),
        new BrowserSurfaceActionDescriptor(Back, "浏览器后退", ScopeName, "Alt+Left", "按真实历史后退"),
        new BrowserSurfaceActionDescriptor(Forward, "浏览器前进", ScopeName, "Alt+Right", "按真实历史前进"),
        new BrowserSurfaceActionDescriptor(Reload, "浏览器刷新", ScopeName, "Ctrl+R", "刷新当前页签"),
        new BrowserSurfaceActionDescriptor(Stop, "浏览器停止", ScopeName, "Esc", "仅加载中可用"),
        new BrowserSurfaceActionDescriptor(Find, "浏览器页内查找", ScopeName, "Ctrl+F", "打开/关闭页内查找条"),
        new BrowserSurfaceActionDescriptor(ZoomIn, "浏览器放大", ScopeName, "Ctrl+OemPlus", "放大当前页签（上限 200%）"),
        new BrowserSurfaceActionDescriptor(ZoomOut, "浏览器缩小", ScopeName, "Ctrl+OemMinus", "缩小当前页签（下限 50%）"),
        new BrowserSurfaceActionDescriptor(ZoomReset, "浏览器缩放重置", ScopeName, "Ctrl+D0", "恢复 100%"),
        new BrowserSurfaceActionDescriptor(CopyAddress, "复制当前地址", ScopeName, "", "复制真实导航地址，而非草稿"),
        new BrowserSurfaceActionDescriptor(OpenExternal, "在默认浏览器打开", ScopeName, "", "显式外部打开当前地址"),
        new BrowserSurfaceActionDescriptor(DevTools, "打开开发者工具", ScopeName, "F12", "真实调用运行时，仅用户操作触发"),
    };

    /// <summary>动作在当前状态下不可用的原因；null 表示可用。</summary>
    public static string? GetDisabledReason(BrowserView? view, string actionId)
    {
        if (view is null) return "浏览器页面未打开。";
        var vm = view.ViewModel;
        return actionId switch
        {
            CloseTab => vm.CanCloseTab ? null : "至少保留一个页签。",
            Back => vm.CanGoBack ? null : "没有可后退的历史。",
            Forward => vm.CanGoForward ? null : "没有可前进的历史。",
            Stop => vm.CanStop ? null : "当前没有正在进行的加载。",
            CopyAddress => string.IsNullOrWhiteSpace(vm.ActiveTab.NavigationAddress) ? "当前没有可复制的真实地址。" : null,
            OpenExternal => string.IsNullOrWhiteSpace(vm.ActiveTab.NavigationAddress) ? "当前没有可外部打开的地址。" : null,
            DevTools => view.HostForTabForTesting(vm.ActiveTab).IsReady ? null : "浏览器运行时尚未就绪。",
            Find => view.HostForTabForTesting(vm.ActiveTab).PageFindAvailable ? null : "当前 WebView2 运行时不支持页内查找。",
            _ => null,
        };
    }

    /// <summary>执行动作；返回是否已受理。未接入共享动作表时，本入口可作为独立回退。</summary>
    public static bool TryExecute(BrowserView? view, string actionId)
    {
        if (view is null) return false;
        if (GetDisabledReason(view, actionId) is not null) return false;
        var vm = view.ViewModel;
        switch (actionId)
        {
            case NewTab: vm.NewTabCommand.Execute(null); return true;
            case CloseTab: vm.CloseTabByIdCommand.Execute(vm.ActiveTab); return true;
            case Back: vm.BackCommand.Execute(null); return true;
            case Forward: vm.ForwardCommand.Execute(null); return true;
            case Reload: vm.ReloadCommand.Execute(null); return true;
            case Stop: vm.StopCommand.Execute(null); return true;
            case Find: vm.ToggleFindCommand.Execute(null); return true;
            case ZoomIn: vm.ZoomInCommand.Execute(null); return true;
            case ZoomOut: vm.ZoomOutCommand.Execute(null); return true;
            case ZoomReset: vm.ZoomResetCommand.Execute(null); return true;
            case CopyAddress: vm.CopyAddressCommand.Execute(null); return true;
            case OpenExternal: vm.OpenExternalCommand.Execute(null); return true;
            case DevTools: vm.OpenDevToolsCommand.Execute(null); return true;
            default: return false;
        }
    }
}
