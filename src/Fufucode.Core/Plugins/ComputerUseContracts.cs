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

/// <summary>鼠标与键盘输入模拟接缝；生产实现走 Win32 SendInput。</summary>
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
