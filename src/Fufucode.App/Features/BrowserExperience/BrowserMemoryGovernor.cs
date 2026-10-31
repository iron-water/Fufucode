using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.App.Features.BrowserExperience;

/// <summary>
/// 页签生命周期。Discarded 不持有 WebView2 controller；Warm 持有但低内存；Suspended 已调用 TrySuspendAsync。
/// </summary>
public enum BrowserTabLifecycle
{
    Discarded,
    Warm,
    Suspended,
    Active,
    Closed,
}

/// <summary>治理器看到的页签快照；不引用 WebView2，便于无头断言。</summary>
public sealed record BrowserTabMemoryInfo(
    string Id,
    BrowserTabLifecycle State,
    DateTimeOffset LastAccessUtc,
    bool IsActive);

/// <summary>一次治理决策：挂起、丢弃哪些页签，以及是否连当前页签一起丢弃。</summary>
public sealed record BrowserMemoryDecision(
    IReadOnlyList<string> Suspend,
    IReadOnlyList<string> Discard,
    bool DiscardActive)
{
    public static BrowserMemoryDecision Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), false);

    public bool IsEmpty => Suspend.Count == 0 && Discard.Count == 0 && !DiscardActive;
}

/// <summary>
/// 纯内存治理逻辑：热池上限、后台挂起/丢弃、进程预算。所有输入都是快照，便于测试与后续接入全局预算。
/// </summary>
public sealed class BrowserMemoryGovernor
{
    public const long DefaultBudgetBytes = 500L * 1024 * 1024;

    /// <summary>最多保留的已加载 controller 数（含当前页签）。</summary>
    public int HotLimit { get; set; } = 3;

    /// <summary>全部 WebView2 子进程 PrivateMemorySize64 总和上限。</summary>
    public long BudgetBytes { get; set; } = DefaultBudgetBytes;

    public TimeSpan BackgroundSuspendAfter { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan BackgroundDiscardAfter { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan BackgroundDiscardAllAfter { get; set; } = TimeSpan.FromMinutes(10);

    public BrowserMemoryDecision Evaluate(
        IReadOnlyList<BrowserTabMemoryInfo> tabs,
        long totalProcessBytes,
        DateTimeOffset now,
        bool background,
        DateTimeOffset? backgroundSince)
    {
        if (tabs.Count == 0) return BrowserMemoryDecision.Empty;
        var active = tabs.FirstOrDefault(tab => tab.IsActive);
        var nonActive = tabs
            .Where(tab => !tab.IsActive && tab.State is not (BrowserTabLifecycle.Discarded or BrowserTabLifecycle.Closed))
            .OrderBy(tab => tab.LastAccessUtc)
            .ToList();
        var suspend = new List<string>();
        var discard = new List<string>();
        var discardActive = false;

        if (background && backgroundSince is { } since)
        {
            var elapsed = now - since;
            if (elapsed >= BackgroundDiscardAllAfter)
            {
                discard.AddRange(tabs.Where(tab => tab.State is not BrowserTabLifecycle.Closed)
                    .Select(tab => tab.Id));
                discardActive = active is not null;
                return new BrowserMemoryDecision(suspend, discard, discardActive);
            }
            if (elapsed >= BackgroundDiscardAfter)
            {
                discard.AddRange(nonActive.Select(tab => tab.Id));
            }
            else if (elapsed >= BackgroundSuspendAfter)
            {
                foreach (var tab in nonActive.Where(tab => tab.State == BrowserTabLifecycle.Warm))
                    suspend.Add(tab.Id);
            }
        }

        // 热池上限只在前台执行；后台由挂起/丢弃时间线接管，避免 2 分钟策略被热池提前丢弃。
        if (!background)
        {
            var loaded = nonActive.Count + (active?.State is BrowserTabLifecycle.Warm or BrowserTabLifecycle.Suspended or BrowserTabLifecycle.Active ? 1 : 0);
            var overHot = Math.Max(0, loaded - Math.Max(1, HotLimit));
            foreach (var tab in nonActive.Where(tab => !discard.Contains(tab.Id)).Take(overHot))
                discard.Add(tab.Id);
        }

        // 进程预算：无法准确归属到单个页签，超预算先丢全部非活动页签；后台仍超则连当前页签一起丢。
        if (totalProcessBytes > BudgetBytes)
        {
            foreach (var tab in nonActive.Where(tab => !discard.Contains(tab.Id)))
                discard.Add(tab.Id);
            if (background && active is not null) discardActive = true;
        }

        // 后台挂起与丢弃互斥：已经决定丢弃的页签不再挂起。
        suspend.RemoveAll(id => discard.Contains(id));
        return new BrowserMemoryDecision(suspend, discard, discardActive);
    }
}
