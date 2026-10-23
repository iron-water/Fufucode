using System;
using System.Collections.Generic;
using System.Linq;

namespace Fufucode.Core.Plugins;

/// <summary>电脑操控路由：决定使用哪个能力层。</summary>
public enum ComputerUseRouteMode
{
    /// <summary>遵守 deliveryMode；后台优先 CUA，前台优先 Sky/Win32。</summary>
    Auto,

    /// <summary>优先宿主注入的 Sky 高成功率路径，默认允许短暂前台。</summary>
    Sky,

    /// <summary>严格使用 CUA Driver。</summary>
    CuaDriver,

    /// <summary>只使用本机 Win32 兼容实现。</summary>
    Win32,

    /// <summary>可见桌面操作：优先 Sky，必要时使用前台兼容路径。</summary>
    Visible,
}

/// <summary>虚拟光标在屏幕上的一个位置。</summary>
public sealed record ComputerUseCursorState(
    string AgentId,
    double X,
    double Y,
    string Color,
    string Label,
    bool IsVisible = true,
    bool IsPressed = false,
    string? Backend = null,
    string? TargetWindow = null,
    DateTimeOffset? UpdatedUtc = null)
{
    public DateTimeOffset EffectiveUpdatedUtc => UpdatedUtc ?? DateTimeOffset.UtcNow;
}

/// <summary>光标注册表：UI Overlay 和电脑操控插件共享的轻量状态源。</summary>
public interface IComputerUseCursorRegistry
{
    IReadOnlyList<ComputerUseCursorState> Snapshot { get; }
    event Action? Changed;
    void Update(ComputerUseCursorState state);
    void Remove(string agentId);
    void Clear();
}

/// <summary>
/// 进程内的虚拟光标状态源。它只维护绘制状态，不调用 SendInput、UIA 或任何系统输入 API。
/// </summary>
public sealed class ComputerUseVirtualCursorRegistry : IComputerUseCursorRegistry
{
    public static ComputerUseVirtualCursorRegistry Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, ComputerUseCursorState> _states =
        new(StringComparer.Ordinal);

    public IReadOnlyList<ComputerUseCursorState> Snapshot
    {
        get
        {
            lock (_gate) return _states.Values.OrderBy(item => item.AgentId, StringComparer.Ordinal).ToArray();
        }
    }

    public event Action? Changed;

    public void Update(ComputerUseCursorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.AgentId))
            throw new ArgumentException("AgentId 不能为空。", nameof(state));
        if (double.IsNaN(state.X) || double.IsInfinity(state.X) ||
            double.IsNaN(state.Y) || double.IsInfinity(state.Y))
            throw new ArgumentOutOfRangeException(nameof(state), "光标坐标必须是有限数值。");

        lock (_gate) _states[state.AgentId] = state with { UpdatedUtc = DateTimeOffset.UtcNow };
        Changed?.Invoke();
    }

    public void Remove(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return;
        var removed = false;
        lock (_gate) removed = _states.Remove(agentId);
        if (removed) Changed?.Invoke();
    }

    public void Clear()
    {
        var hadValues = false;
        lock (_gate)
        {
            hadValues = _states.Count > 0;
            _states.Clear();
        }
        if (hadValues) Changed?.Invoke();
    }
}
