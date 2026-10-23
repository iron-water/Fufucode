using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Plugins;

/// <summary>屏幕截图区域（像素）；null 表示截取全部虚拟屏幕。</summary>
public readonly record struct ScreenRect(int? Left, int? Top, int? Width, int? Height)
{
    public bool IsFullScreen => Left is null && Top is null && Width is null && Height is null;

    public static ScreenRect Of(int? left, int? top, int? width, int? height)
    {
        if (width is <= 0 || height is <= 0) return default;
        return new ScreenRect(left, top, width, height);
    }
}

/// <summary>一次屏幕截图的结果。</summary>
public sealed record ScreenCaptureResult(bool Success, byte[]? PngBytes, int? Width, int? Height, string? Error);

/// <summary>屏幕截图提供者；生产实现走 Win32，自测可注入替身。</summary>
public interface IComputerScreenCapture
{
    Task<ScreenCaptureResult> CaptureAsync(ScreenRect bounds, CancellationToken cancellationToken = default);
}

/// <summary>鼠标按键。</summary>
public enum MouseButton
{
    Left,
    Right,
    Middle,
}

/// <summary>兼容旧版前台输入实现的最小接口。</summary>
public interface IComputerInput
{
    bool Move(int x, int y);
    bool Click(int x, int y, MouseButton button);
    bool DoubleClick(int x, int y);
    bool Scroll(int delta);
    bool TypeText(string text);
    bool PressKey(string key);
    bool Hotkey(IReadOnlyList<string> keys);
}

/// <summary>电脑操控投递模式。</summary>
public enum ComputerUseDeliveryMode
{
    /// <summary>按目标窗口后台投递，不改变用户前台窗口或真实鼠标。</summary>
    Background,

    /// <summary>允许短暂切换到目标窗口后恢复原前台窗口。</summary>
    Foreground,
}

/// <summary>电脑操控后端身份。</summary>
public enum ComputerUseBackendKind
{
    Sky,
    CuaDriver,
    Win32,
}

/// <summary>
/// 一个可精确寻址的窗口目标。后台投递必须携带进程和窗口身份，
/// element token 只在对应后端最近一次观察结果仍有效时使用。
/// </summary>
public sealed record ComputerUseTarget(
    int? ProcessId = null,
    long? WindowId = null,
    string? ElementToken = null,
    string? Session = null)
{
    public bool HasWindow => ProcessId is not null && WindowId is not null;

    public string ResourceKey => HasWindow
        ? $"window:{ProcessId}:{WindowId}"
        : "desktop";
}

/// <summary>单次电脑操控共用上下文。</summary>
public sealed record ComputerUseActionContext(
    ComputerUseDeliveryMode DeliveryMode = ComputerUseDeliveryMode.Background,
    ComputerUseTarget? Target = null);

/// <summary>截图后端请求。</summary>
public sealed record ComputerUseCaptureRequest(
    ScreenRect Bounds,
    bool ReturnBase64,
    ComputerUseActionContext Context);

/// <summary>鼠标后端请求。</summary>
public sealed record ComputerUseMouseRequest(
    string Action,
    int? X,
    int? Y,
    int Delta,
    ComputerUseActionContext Context);

/// <summary>键盘后端请求。</summary>
public sealed record ComputerUseKeyboardRequest(
    string Action,
    string? Text,
    string? Key,
    IReadOnlyList<string> Keys,
    ComputerUseActionContext Context);

/// <summary>统一后端执行结果；后台不可用时保留可解释错误码。</summary>
public sealed record ComputerUseBackendResult(
    bool Success,
    string Message,
    byte[]? PngBytes = null,
    int? Width = null,
    int? Height = null,
    string? ErrorCode = null,
    bool RecommendedForeground = false,
    string? RawJson = null);

/// <summary>
/// 可插拔电脑操控后端。Sky 与 CUA Driver 都实现此契约；Win32 仅作为显式前台兼容路径。
/// </summary>
public interface IComputerUseBackend
{
    ComputerUseBackendKind Kind { get; }
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    bool SupportsBackground { get; }
    bool SupportsForeground { get; }

    Task<ComputerUseBackendResult> CaptureAsync(
        ComputerUseCaptureRequest request,
        CancellationToken cancellationToken = default);

    Task<ComputerUseBackendResult> MouseAsync(
        ComputerUseMouseRequest request,
        CancellationToken cancellationToken = default);

    Task<ComputerUseBackendResult> KeyboardAsync(
        ComputerUseKeyboardRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Sky 宿主桥的标记接口，实际桥接由带有 node_repl/Sky 权限的宿主注入。</summary>
public interface ISkyComputerUseBackend : IComputerUseBackend
{
}

/// <summary>
/// 电脑操控资源闸门。输入动作共享 input 锁，避免多线程交错按键/鼠标事件；
/// 截图可按目标窗口独立加锁，不阻塞其它窗口的观察。
/// </summary>
public interface IComputerUseOperationGate
{
    ValueTask<IAsyncDisposable> EnterAsync(string resourceKey, CancellationToken cancellationToken = default);
}

public sealed class ComputerUseOperationGate : IComputerUseOperationGate
{
    public static ComputerUseOperationGate Shared { get; } = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores =
        new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> EnterAsync(
        string resourceKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceKey))
            throw new ArgumentException("资源锁键不能为空。", nameof(resourceKey));

        var semaphore = _semaphores.GetOrAdd(resourceKey, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>非 Windows 平台的截图实现：如实报告平台不支持。</summary>
public sealed class UnsupportedScreenCapture : IComputerScreenCapture
{
    public static readonly UnsupportedScreenCapture Instance = new();

    public Task<ScreenCaptureResult> CaptureAsync(ScreenRect bounds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ScreenCaptureResult(false, null, null, null, "当前平台不支持屏幕截图。"));
    }
}

/// <summary>非 Windows 平台的输入模拟实现：如实报告平台不支持。</summary>
public sealed class UnsupportedInput : IComputerInput
{
    public static readonly UnsupportedInput Instance = new();

    public bool Move(int x, int y) => false;
    public bool Click(int x, int y, MouseButton button) => false;
    public bool DoubleClick(int x, int y) => false;
    public bool Scroll(int delta) => false;
    public bool TypeText(string text) => false;
    public bool PressKey(string key) => false;
    public bool Hotkey(IReadOnlyList<string> keys) => false;
}
