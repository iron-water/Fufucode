using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Fufucode.App.Features.Shortcuts;

/// <summary>录制结果。</summary>
public enum ShortcutCaptureOutcome
{
    /// <summary>还没按到可用的组合，继续录制。</summary>
    Pending,
    /// <summary>只按了修饰键，等待主键。</summary>
    ModifiersOnly,
    /// <summary>拿到一个可用组合。</summary>
    Captured,
    /// <summary>用户按 Escape 取消。</summary>
    Cancelled,
    /// <summary>输入法组合进行中，本次按键已忽略。</summary>
    IgnoredDuringComposition,
}

public sealed record ShortcutCaptureResult(ShortcutCaptureOutcome Outcome, ShortcutGesture? Gesture, string? Reason);

/// <summary>
/// 录制器：把按下的键转成规范化手势。四条硬规则在这里落地——
/// 组合期间的按键一律忽略（中文输入法不误触）、修饰键本身不作为绑定、Escape 取消录制，
/// 以及**修饰键状态以当前实际按键为准**：Ctrl 松开后按 K 不会录成 Ctrl+K。
/// </summary>
public sealed class ShortcutRecorder
{
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);

    public bool IsRecording { get; private set; }

    /// <summary>录制期间当前仍按住的修饰键文本（用于界面提示）。</summary>
    public string PendingModifiers => _held.Count == 0
        ? string.Empty
        : string.Join("+", new[] { "Ctrl", "Alt", "Shift", "Meta" }.Where(_held.Contains));

    public void Start()
    {
        IsRecording = true;
        _held.Clear();
    }

    public void Stop()
    {
        IsRecording = false;
        _held.Clear();
    }

    /// <summary>
    /// 处理一次 KeyDown。<paramref name="imeComposing"/> 为 true（输入法组合进行中）时
    /// 只回报忽略，绝不落定绑定。<paramref name="heldModifiers"/> 是当前实际按住的修饰键，
    /// 它是形成手势的唯一来源；本方法不接受「历史累计」语义。
    /// </summary>
    public ShortcutCaptureResult KeyDown(Key key, KeyModifiers heldModifiers, bool imeComposing)
    {
        if (!IsRecording) return new(ShortcutCaptureOutcome.Pending, null, null);
        if (imeComposing)
            return new(ShortcutCaptureOutcome.IgnoredDuringComposition, null, ShortcutRules.ImeReservedReason);

        if (key == Key.Escape)
        {
            Stop();
            return new(ShortcutCaptureOutcome.Cancelled, null, "已取消录制");
        }

        // 只按修饰键：按当前实际状态记录，等主键。
        if (ModifierNameOf(key) is { } modifier)
        {
            if (heldModifiers.HasFlag(ModifierFlagOf(modifier))) _held.Add(modifier);
            else _held.Remove(modifier);
            return new(ShortcutCaptureOutcome.ModifiersOnly, null, null);
        }

        if (key == Key.None) return new(ShortcutCaptureOutcome.Pending, null, null);

        // 以当前实际按住的修饰键为准，不与历史状态取并集。
        var effective = heldModifiers & SupportedModifiers;
        var gesture = new ShortcutGesture(key, effective);
        Stop();
        return new(ShortcutCaptureOutcome.Captured, gesture, null);
    }

    /// <summary>
    /// 处理一次 KeyUp：修饰键释放后必须从当前状态移除，
    /// 否则后续裸键会被错误地记成「已释放修饰键 + 主键」。
    /// </summary>
    public void KeyUp(Key key)
    {
        if (!IsRecording) return;
        if (ModifierNameOf(key) is { } modifier) _held.Remove(modifier);
    }

    /// <summary>窗口失活或页面离开时调用：不结束录制，但清掉已过期的按键状态。</summary>
    public void ReleaseAllHeld()
    {
        if (!IsRecording) return;
        _held.Clear();
    }

    private const KeyModifiers SupportedModifiers =
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta;

    private static string? ModifierNameOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftAlt or Key.RightAlt => "Alt",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LWin or Key.RWin => "Meta",
        _ => null,
    };

    private static KeyModifiers ModifierFlagOf(string name) => name switch
    {
        "Ctrl" => KeyModifiers.Control,
        "Alt" => KeyModifiers.Alt,
        "Shift" => KeyModifiers.Shift,
        _ => KeyModifiers.Meta,
    };
}
