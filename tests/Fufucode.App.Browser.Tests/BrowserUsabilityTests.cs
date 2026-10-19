using System;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using Fufucode.App.Features.BrowserExperience;
using Fufucode.App.Infrastructure;
using Fufucode.App.Infrastructure.Browser;
using Fufucode.App.ViewModels;
using Fufucode.App.Views;

namespace Fufucode.App.Browser.Tests;

/// <summary>
/// BRW-2 定向测试：地址栏搜索回退、站点规则、收藏/历史落盘与候选下拉、真实设置下发到页签。
/// 全部使用临时目录，不触碰用户数据。
/// </summary>
internal static class BrowserUsabilityTests
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-brw2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            SearchFallback(check);
            SiteRules(check);
            SettingsPropagation(check, root);
            Bookmarks(check, root);
            History(check, root);
            AgentApprovalGate(check, root);
            ExtensionDirectory(check, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void AgentApprovalGate(Action<bool, string> check, string root)
    {
        var dir = Path.Combine(root, "approval");
        var store = new BrowserSettingsStore(dir);
        var vm = new BrowserSettingsViewModel();
        vm.Attach(dir);
        var view = new BrowserView();
        try
        {
            view.ApplyAgentControl(store.Current with
            {
                BrowserUseEnabled = true,
                DefaultBrowsePermission = "approval",
                AllowedSites = new[] { new BrowserSitePermission("blocked.example", "block") },
            }, store, vm);
            var asked = 0;
            view.SetAgentApprovalHandler((request, _) =>
            {
                asked++;
                check(request.Action == BrowserUseActionNames.Navigate && request.Host == "other.example",
                    "审批请求带上动作与目标主机");
                return Task.FromResult(false);
            });
            var allowed = view.BridgeForTesting!.NavigateAsync("https://other.example/x").GetAwaiter().GetResult();
            check(!allowed && asked == 1, "需要批准的站点在用户拒绝时不会导航");

            view.SetAgentApprovalHandler((_, _) => Task.FromResult(true));
            check(view.BridgeForTesting!.NavigateAsync("https://other.example/x").GetAwaiter().GetResult(),
                "用户允许后导航照常执行");

            var blockedAsked = 0;
            view.SetAgentApprovalHandler((_, _) => { blockedAsked++; return Task.FromResult(true); });
            check(!view.BridgeForTesting!.NavigateAsync("https://blocked.example/x").GetAwaiter().GetResult() &&
                  blockedAsked == 0, "被禁止的站点直接拒绝，不打扰用户确认");

            view.ApplyAgentControl(store.Current with
            {
                BrowserUseEnabled = true,
                DefaultBrowsePermission = "approval",
                ScreenshotMode = "never",
            }, store, vm);
            var screenshot = view.BridgeForTesting!.ScreenshotAsync("png").GetAwaiter().GetResult();
            check(screenshot.Contains("截图策略为“从不”", StringComparison.Ordinal), "截图策略为从不时 agent 截图被拒绝");

            view.ApplyAgentControl(store.Current with { BrowserUseEnabled = true, DefaultBrowsePermission = "approval" }, store, vm);
            view.SetAgentApprovalHandler(null);
            var noChannel = view.BridgeForTesting!.ClickAsync("#submit").GetAwaiter().GetResult();
            check(noChannel.Contains("\"error\"", StringComparison.Ordinal), "没有确认通道时按拒绝处理，不静默放行");
        }
        finally { view.Dispose(); }
    }

    private static void ExtensionDirectory(Action<bool, string> check, string root)
    {
        var dir = Path.Combine(root, "extensions");
        var webview = BrowserUserDataLayout.Resolve(dir);
        var folder = BrowserExtensionLoader.EnsureDirectory(webview);
        check(Directory.Exists(folder) && Path.GetFileName(folder) == BrowserExtensionLoader.FolderName,
            "扩展目录位于应用数据根下并与 WebView 缓存分离");
        var settings = new BrowserSettingsViewModel();
        settings.Attach(dir);
        check(settings.ExtensionsDirectory == folder, "设置页展示真实扩展目录");
    }

    private static void SearchFallback(Action<bool, string> check)
    {
        var words = BrowserAddressResolver.Resolve("hello world");
        check(words.Kind == BrowserTargetKind.Search && words.IsNavigable && words.Query == "hello world",
            "含空格的普通文字转为搜索而不是拒绝");
        check(words.Address!.Contains("hello%20world", StringComparison.Ordinal), "搜索词被正确转义");

        var chinese = BrowserAddressResolver.Resolve("如何写好 Avalonia");
        check(chinese.Kind == BrowserTargetKind.Search, "中文搜索词也能识别");

        var host = BrowserAddressResolver.Resolve("example.com/path?q=1");
        check(host.Kind == BrowserTargetKind.Web && host.Address == "https://example.com/path?q=1",
            "裸域名仍按 https 导航，不受搜索回退影响");

        var local = BrowserAddressResolver.Resolve("localhost:3000");
        check(local.Kind == BrowserTargetKind.Web && local.Address == "https://localhost:3000",
            "host:port 仍按网址处理");

        var script = BrowserAddressResolver.Resolve("javascript:alert(1)");
        check(script.Kind == BrowserTargetKind.Rejected, "脚本协议仍被拒绝，不会转成搜索");

        var custom = BrowserAddressResolver.Resolve("rust guide", null, "https://www.bing.com/search?q={0}");
        check(custom.Address == "https://www.bing.com/search?q=rust%20guide", "自定义搜索模板生效");

        var broken = BrowserAddressResolver.Resolve("rust guide", null, "not-a-template");
        check(broken.Address?.StartsWith("https://www.google.com/search", StringComparison.Ordinal) == true,
            "非法模板回退默认搜索");
    }

    private static void SiteRules(Action<bool, string> check)
    {
        var settings = BrowserSettings.Default with
        {
            DefaultBrowsePermission = "approval",
            DefaultDownloadPermission = "block",
            AllowedSites = new[]
            {
                new BrowserSitePermission("allowed.example", "allow"),
                new BrowserSitePermission("Blocked.Example", "block"),
            },
        };
        var rules = new BrowserSiteRules(settings);
        check(rules.PermissionFor("https://allowed.example/x") == "allow", "白名单站点使用自身权限（忽略大小写与路径）");
        check(rules.PermissionFor("https://blocked.example/") == "block", "被阻止站点返回 block");
        check(rules.PermissionFor("https://other.example/") == "approval", "未列出的站点使用全网默认权限");
        check(rules.PermissionFor("javascript:alert(1)") == "block", "非网页来源一律 block");
        check(rules.CanNavigate("https://blocked.example/") is false && rules.CanNavigate("https://other.example/") is true,
            "导航可用性使用同一套规则");
        check(rules.DownloadPermissionFor("file:///C:/tmp/a.html") == "allow", "本地来源下载不受站点规则限制");

        check(rules.PermissionDecision("allowed.example", "Camera") is null, "首次权限请求没有记忆");
        rules.RememberPermission("Allowed.Example", "Camera", true);
        check(rules.PermissionDecision("allowed.example", "Camera") is true, "记住的允许决定按主机名大小写不敏感命中");
        check(rules.PermissionDecision("allowed.example", "Microphone") is null, "不同权限种类各自记忆");

        var system = new BrowserSiteRules(BrowserSettings.Default with { WebUrlOpenTarget = "system", LocalUrlOpenTarget = "builtin" });
        check(system.OpensWebExternally && !system.OpensLocalExternally, "URL 打开位置设置被规则层读取");
    }

    private static void SettingsPropagation(Action<bool, string> check, string root)
    {
        var dir = Path.Combine(root, "propagate");
        var store = new BrowserSettingsStore(dir);
        var saved = store.Save(store.Current with { SearchUrlTemplate = "https://duckduckgo.com/?q={0}", UserAgent = "FufuTest/1.0" });
        check(saved.SearchUrlTemplate.StartsWith("https://duckduckgo.com", StringComparison.Ordinal), "搜索模板落盘往返");

        store.Save(store.Current with { SearchUrlTemplate = "no-placeholder" });
        check(store.Current.SearchUrlTemplate.StartsWith("https://www.google.com/search", StringComparison.Ordinal),
            "非法搜索模板在保存时收敛回默认值");

        var view = new BrowserView();
        try
        {
            var vm = new BrowserSettingsViewModel();
            vm.Attach(dir);
            view.ApplyAgentControl(store.Current with { SearchUrlTemplate = "https://search.example/?q={0}" }, store, vm);
            var tab = view.ViewModel.ActiveTab;
            tab.AddressText = "unique query";
            tab.NavigateCommand.Execute(null);
            check(tab.NavigationAddress == "https://search.example/?q=unique%20query",
                "设置里的搜索模板真实下发到地址栏导航");

            view.ApplyAgentControl(store.Current with { ShowFullUrl = false }, store, vm);
            tab.ApplyNavigationState(false, false, false, "https://deep.example/a/b?c=1");
            check(tab.DisplayAddress == "deep.example", "显示完整网址设置真实生效到页签");

            view.ViewModel.NewTabCommand.Execute(null);
            check(view.ViewModel.ActiveTab.ShowFullUrl is false, "新页签继承当前显示策略");
        }
        finally { view.Dispose(); }
    }

    private static void Bookmarks(Action<bool, string> check, string root)
    {
        var dir = Path.Combine(root, "bookmarks");
        var bookmarks = BrowserBookmarkStore.ForRoot(dir);
        check(BrowserBookmarkStore.ForRoot(dir) == bookmarks, "同一数据根复用同一收藏实例");
        check(!bookmarks.Contains("https://a.example/"), "初始没有收藏");

        var history = BrowserHistoryStore.ForRoot(dir);
        var view = new BrowserView(bookmarks, history);
        try
        {
            var tab = view.ViewModel.ActiveTab;
            view.ViewModel.ToggleBookmarkCommand.Execute(null);
            check(!view.ViewModel.IsCurrentBookmarked, "无真实地址时不写入收藏");

            tab.ApplyNavigationState(false, false, false, "https://a.example/page");
            view.ViewModel.ToggleBookmarkCommand.Execute(null);
            check(view.ViewModel.IsCurrentBookmarked && bookmarks.Contains("https://a.example/page"), "收藏真实导航地址并落盘");
            check(File.Exists(bookmarks.Path), "收藏文件真实写出");
            view.ViewModel.CloseSuggestions();

            var reopened = new BrowserBookmarkStore(dir);
            check(reopened.Contains("https://a.example/page"), "重开后收藏仍存在");

            reopened.UpdateTitle("https://a.example/page", "A 页面");
            check(reopened.Entries[0].Title == "A 页面", "导航完成后补全收藏标题");

            view.ViewModel.ToggleBookmarkCommand.Execute(null);
            check(!new BrowserBookmarkStore(dir).Contains("https://a.example/page"), "再次切换即取消收藏并落盘");
        }
        finally { view.Dispose(); }
    }

    private static void History(Action<bool, string> check, string root)
    {
        var dir = Path.Combine(root, "history");
        var history = BrowserHistoryStore.ForRoot(dir);
        history.Record("https://one.example/a", "One");
        history.Record("file:///C:/tmp/x.html", "Local");
        history.Record("https://two.example/b", "Two");
        history.Record("https://one.example/a", "One");
        var entries = history.Entries;
        check(entries.Count == 2 && entries.All(e => e.Address.StartsWith("https://", StringComparison.Ordinal)),
            "只记录 http/https，同一地址合并");
        check(entries[0].Address == "https://one.example/a" && entries[0].VisitCount == 2, "重复访问移到最前并累计次数");

        var reopened = new BrowserHistoryStore(dir);
        check(reopened.Entries.Count == 2, "历史重开后仍存在");
        check(reopened.Search("two").Single().Title == "Two", "按标题搜索命中");
        check(reopened.Search("").First().Address == "https://one.example/a", "空查询返回最近访问");

        var bookmarks = BrowserBookmarkStore.ForRoot(Path.Combine(root, "history-bookmarks"));
        bookmarks.Toggle("https://book.example/", "Bookmark");
        var view = new BrowserView(bookmarks, reopened);
        try
        {
            view.ViewModel.RefreshSuggestions(string.Empty);
            check(view.ViewModel.IsSuggestionsOpen && view.ViewModel.Suggestions.Count >= 3, "地址栏候选同时包含收藏与历史");
            check(view.ViewModel.Suggestions.First().IsBookmark, "收藏排在历史之前");
            check(view.ViewModel.Suggestions.Select(s => s.Address).Distinct().Count() == view.ViewModel.Suggestions.Count,
                "候选地址不重复");

            view.ViewModel.RefreshSuggestions("book");
            check(view.ViewModel.Suggestions.Any(s => s.Address == "https://book.example/"), "按文字过滤候选");
            view.ViewModel.MoveSuggestion(1);
            check(view.ViewModel.SelectedSuggestion is not null, "键盘可在候选间移动");
        }
        finally { view.Dispose(); }

        history.Clear();
        check(history.Entries.Count == 0 && new BrowserHistoryStore(dir).Entries.Count == 0, "清空历史真实落盘");
    }
}
