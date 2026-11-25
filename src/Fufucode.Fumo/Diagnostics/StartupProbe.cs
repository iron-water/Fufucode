using System;

namespace Fufucode.Core.Diagnostics;

/// <summary>
/// 启动里程碑探针：核心与组合根（Core 不引用 App）通过它上报启动阶段，
/// App 在进程启动时把 <see cref="Handler"/> 接到 <c>StartupTrace</c>。未接线时为零成本。
/// </summary>
public static class StartupProbe
{
    /// <summary>启动里程碑回调：(名称, 细节)。</summary>
    public static Action<string, string?>? Handler { get; set; }

    public static void Mark(string name, string? detail = null) => Handler?.Invoke(name, detail);
}
