using System;
using System.Linq;
using Fufucode.App.Features.BrowserExperience;

namespace Fufucode.App.Browser.Tests;

/// <summary>内存治理纯逻辑：热池 LRU、后台挂起/丢弃、预算触发，不依赖 WebView2。</summary>
internal static class BrowserMemoryGovernorTests
{
    public static void Run(Action<bool, string> check)
    {
        var now = DateTimeOffset.UtcNow;
        var tabs = new[]
        {
            Info("a", BrowserTabLifecycle.Active, now.AddMinutes(-1), true),
            Info("b", BrowserTabLifecycle.Warm, now.AddMinutes(-5), false),
            Info("c", BrowserTabLifecycle.Warm, now.AddMinutes(-4), false),
            Info("d", BrowserTabLifecycle.Warm, now.AddMinutes(-3), false),
        };

        var governor = new BrowserMemoryGovernor { HotLimit = 3, BudgetBytes = 1000 };
        var hot = governor.Evaluate(tabs, 100, now, background: false, backgroundSince: null);
        check(hot.Discard.Count == 1 && hot.Discard[0] == "b",
            "前台热池超限按 LRU 丢弃最旧的非活动页签");
        check(hot.Suspend.Count == 0 && !hot.DiscardActive, "前台热池治理不挂起、不丢当前页签");

        var budget = governor.Evaluate(tabs, 2000, now, background: false, backgroundSince: null);
        check(budget.Discard.OrderBy(id => id).SequenceEqual(new[] { "b", "c", "d" }),
            "前台超预算丢弃全部非活动页签");
        check(!budget.DiscardActive, "前台超预算不丢当前页签");

        var backgrounded = now.AddMinutes(-3);
        var suspend = governor.Evaluate(tabs, 100, now, background: true, backgroundSince: backgrounded);
        check(suspend.Suspend.OrderBy(id => id).SequenceEqual(new[] { "b", "c", "d" }) && suspend.Discard.Count == 0,
            "后台 2 分钟后挂起非活动页签");

        backgrounded = now.AddMinutes(-6);
        var discard = governor.Evaluate(tabs, 100, now, background: true, backgroundSince: backgrounded);
        check(discard.Discard.OrderBy(id => id).SequenceEqual(new[] { "b", "c", "d" }) && !discard.DiscardActive,
            "后台 5 分钟后丢弃非活动页签");

        backgrounded = now.AddMinutes(-11);
        var discardAll = governor.Evaluate(tabs, 100, now, background: true, backgroundSince: backgrounded);
        check(discardAll.DiscardActive && discardAll.Discard.Count == 4,
            "后台 10 分钟后连当前页签一起丢弃");

        var suspended = new[]
        {
            Info("a", BrowserTabLifecycle.Active, now, true),
            Info("b", BrowserTabLifecycle.Suspended, now.AddMinutes(-1), false),
            Info("c", BrowserTabLifecycle.Discarded, now.AddMinutes(-2), false),
        };
        var noDouble = governor.Evaluate(suspended, 100, now, background: true, backgroundSince: now.AddMinutes(-3));
        check(noDouble.Suspend.Count == 0 && noDouble.Discard.Count == 0,
            "已挂起/已丢弃页签不重复处理");

        var tab = new BrowserTab("life");
        check(tab.Lifecycle == BrowserTabLifecycle.Discarded && tab.IsDiscarded, "新页签初始为 Discarded");
        tab.ApplyLifecycle(BrowserTabLifecycle.Warm);
        check(!tab.IsDiscarded && tab.LifecycleText == "后台", "生命周期切换更新展示状态");
        tab.Touch();
        check(tab.LastAccessUtc <= DateTimeOffset.UtcNow, "Touch 更新 LRU 时间");
    }

    private static BrowserTabMemoryInfo Info(string id, BrowserTabLifecycle state, DateTimeOffset access, bool active) =>
        new(id, state, access, active);
}
