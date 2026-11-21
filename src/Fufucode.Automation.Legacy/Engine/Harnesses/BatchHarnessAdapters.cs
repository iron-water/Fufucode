using Fufucode.App.Abstractions.Automation.Harnesses;
using Fufucode.Core.Automation.Harnesses;

namespace Fufucode.Plugins.AutoDev.Engine.Harnesses;

/// <summary>
/// 把内建自动开发领域的批量 harness 适配器登记到宿主契约 <see cref="HarnessAdapters"/>。
/// 只登记协议，不启动执行器或后台宿主。
/// </summary>
public static class BatchHarnessAdapters
{
    private static readonly (string Id, Func<IHarnessAdapter> Factory)[] All =
    [
        ("pi", () => new PiBatchAdapter()),
        ("omp", () => new OmpBatchAdapter()),
        ("codex", () => new CodexBatchAdapter()),
        ("dsh", () => new DshBatchAdapter()),
    ];

    /// <summary>登记全部适配器；重复登记幂等。</summary>
    public static void Register() { foreach (var (id, factory) in All) HarnessAdapters.Register(id, factory); }

    /// <summary>注销全部适配器；未登记时是空操作。</summary>
    public static void Unregister() { foreach (var (id, _) in All) HarnessAdapters.Unregister(id); }
}
