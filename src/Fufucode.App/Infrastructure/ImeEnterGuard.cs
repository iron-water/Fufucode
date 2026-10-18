using System;

namespace Fufucode.App.Infrastructure;

/// <summary>
/// 输入法组合期间的回车守卫（计划 §9：中文输入法组合期间 Enter 不误发送）。
/// </summary>
/// <remarks>
/// 信号取自 TextBox 模板内 TextPresenter 的真实组合状态（PreeditText）：
/// 组合进行中（PreeditText 非空）的回车一律吞掉；组合刚结束（非空 → 空跳变）的
/// 80ms 内仍吞回车——IME 用回车提交组合时，提交回车与组合收尾在同一个消息批次内到达。
/// 普通文本变化（任何形状）都不会触发吞回车。时钟可注入，便于无头测试。
///
/// UI-1 双重互锁：除组合态时间窗外，还叠加「输入法合成键」维度——对齐 Web 侧
/// <c>!(isComposing || keyCode === 229)</c>。在 Avalonia 里 VK 229 由
/// <see cref="Avalonia.Input.Key.ImeProcessed"/> 表达，凡该键一律吞掉，绝不发送。
/// </remarks>
public sealed class ImeEnterGuard
{
    private static readonly TimeSpan SwallowWindow = TimeSpan.FromMilliseconds(80);
    /// <summary>Windows 输入法合成键虚拟键码（VK_PROCESSKEY / 229），Web 侧 isComposing 之外的第二重互锁信号。</summary>
    public const int ImeProcessKeyCode = 229;

    private readonly Func<DateTimeOffset> _clock;
    private string? _preedit;
    private DateTimeOffset _lastCompositionEnd = DateTimeOffset.MinValue;

    public ImeEnterGuard(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>由视图在输入框 TextPresenter 的 PreeditText 变化时调用。</summary>
    public void OnPreeditChanged(string? preedit)
    {
        if (string.IsNullOrEmpty(preedit))
        {
            if (!string.IsNullOrEmpty(_preedit))
            {
                _lastCompositionEnd = _clock();
            }
        }

        _preedit = preedit;
    }

    /// <summary>返回 true 表示本次回车必须吞掉（不发送）。</summary>
    /// <remarks>仅基于组合态时间窗；不校验按键码，便于无头测试与视图层复用。</remarks>
    public bool ShouldSwallowEnter()
    {
        if (!string.IsNullOrEmpty(_preedit))
        {
            return true;
        }

        return _lastCompositionEnd != DateTimeOffset.MinValue
               && _clock() - _lastCompositionEnd < SwallowWindow;
    }

    /// <summary>
    /// UI-1 CJK 输入法双重互锁的纯判定：<c>CanSubmit = key==Enter ∧ ¬Shift ∧ ¬isComposing ∧ keyCode≠229</c>。
    /// 本方法只表达「这一击是否被输入法互锁拦下」——组合态时间窗命中、或按键是合成键（229/ImeProcessed）时返回 true。
    /// </summary>
    /// <param name="keyCode">平台按键码；Web 侧为 <c>keyCode</c>，229 表示输入法合成。传 0 表示不校验按键码。</param>
    /// <param name="isComposing">输入法组合态标志（对齐 Web <c>e.nativeEvent.isComposing</c>）。</param>
    public bool ShouldSwallowEnter(int keyCode, bool isComposing)
    {
        if (isComposing || keyCode == ImeProcessKeyCode)
        {
            return true;
        }

        return ShouldSwallowEnter();
    }
}
