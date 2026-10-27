using System;
using Fufucode.Core;

namespace Fufucode.App.Integration;

/// <summary>
/// 当前生效的内存优化开关。设置页写入后立即更新；各子系统只读这里，
/// 不直接读磁盘，避免热路径反复解析 settings.json。
/// 默认全部关闭 = 高性能模式。
/// </summary>
public static class MemoryProfile
{
    private static volatile PerformanceSettings _current = new();

    public static PerformanceSettings Current => _current;
    public static bool SoftwareRendering => _current.SoftwareRendering;
    public static bool DisableVisualEffects => _current.DisableVisualEffects;
    public static bool ReduceTerminalScrollback => _current.ReduceTerminalScrollback;
    public static bool ReduceBackgroundBrowserMemory => _current.ReduceBackgroundBrowserMemory;

    internal static event Action? VisualEffectsChanged;

    public static void Apply(PerformanceSettings? settings)
    {
        var previous = DisableVisualEffects;
        _current = settings ?? new PerformanceSettings();
        if (previous != DisableVisualEffects) VisualEffectsChanged?.Invoke();
    }
}
