using System;
using System.Runtime.Loader;
using Fufucode.Core.Diagnostics;
using Fufucode.Core.Diagnostics.Perf;

namespace Fufucode.Core.Diagnostics.Perf;

/// <summary>
/// 进程级追踪器只允许一个接收端。插件管理器在页面宿主与工具宿主里各持一份加载上下文，
/// 两份插件程序集分别调用 <see cref="PerfTracer.Attach"/>，因此这里按“程序集身份”裁决：
/// 第一个接收端获胜；后来来自**其它**程序集的接收端被拒绝并给出明确原因，
/// 同一程序集重复安装则视为已激活（允许幂等调用）。
/// 比较按 <see cref="IPerfSink"/> 的 Type 程序集身份进行，不依赖加载上下文里的强转。
/// </summary>
public static class PerfOwnership
{
    private static readonly object Gate = new();

    /// <summary>当前接收端所属程序集的完整路径；未启用时为 null。</summary>
    public static string? OwnerAssemblyPath => Volatile.Read(ref s_ownerPath);

    private static string? s_ownerPath;

    /// <summary>
    /// 尝试安装接收端。返回 null 表示成功；否则返回可读的失败原因。
    /// </summary>
    public static string? TryInstall(IPerfSink sink, IPerfModeProvider? modeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        var path = sink.GetType().Assembly.Location;
        lock (Gate)
        {
            var existing = PerfTracer.Sink;
            if (existing is null)
            {
                PerfTracer.Attach(sink, modeProvider);
                Volatile.Write(ref s_ownerPath, path);
                return null;
            }
            var existingPath = existing.GetType().Assembly.Location;
            if (string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
                return null;
            return $"已有性能接收端处于激活状态（来自 {existingPath}），性能监控插件不重复安装。";
        }
    }

    /// <summary>若当前接收端属于给定程序集则移除；返回是否移除。</summary>
    public static bool TryUninstall(IPerfSink sink)
    {
        lock (Gate)
        {
            var existing = PerfTracer.Sink;
            if (existing is null) return false;
            var sameAssembly = string.Equals(existing.GetType().Assembly.Location, sink.GetType().Assembly.Location,
                StringComparison.OrdinalIgnoreCase);
            if (!sameAssembly) return false;
            var removed = PerfTracer.Detach(existing);
            if (removed) Volatile.Write(ref s_ownerPath, null);
            return removed;
        }
    }
}
