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
/// </remarks>
public sealed class ImeEnterGuard
{
    private static readonly TimeSpan SwallowWindow = TimeSpan.FromMilliseconds(80);

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
    public bool ShouldSwallowEnter()
    {
        if (!string.IsNullOrEmpty(_preedit))
        {
            return true;
        }

        return _lastCompositionEnd != DateTimeOffset.MinValue
               && _clock() - _lastCompositionEnd < SwallowWindow;
    }
}
