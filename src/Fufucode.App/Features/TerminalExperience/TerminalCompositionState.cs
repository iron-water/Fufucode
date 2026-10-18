using System;

namespace Fufucode.App.Features.TerminalExperience;

/// <summary>
/// IME 组合（Composition）期间的临时预览状态。视图把输入框模板内 TextPresenter 的真实
/// <c>PreeditText</c> 变化送进来：组合未提交时给出清晰临时预览，提交后由输入框自身把完整
/// 文本并入命令草稿，不会重复写入。
/// </summary>
/// <remarks>
/// 组合文本可能逐字符到达；预览永不渲染尾部孤立的代理项（surrogate），
/// 因此中文与 emoji 组合过程中不会出现半个字符的乱码或字符分裂。
/// </remarks>
public sealed class TerminalCompositionState
{
    private string _preedit = string.Empty;

    /// <summary>组合文本变化时触发；视图据此刷新预览元素。</summary>
    public event Action? Changed;

    /// <summary>当前未提交的组合文本，原样保留（含可能尚未配对的尾部代理项）。</summary>
    public string Preedit => _preedit;

    public bool IsComposing => _preedit.Length > 0;

    /// <summary>可直接显示的预览文本；组合未开始时为空。</summary>
    public string PreviewText
    {
        get
        {
            if (_preedit.Length == 0) return string.Empty;
            // 尾部孤立高代理项先不渲染，等低代理项到达后整体显示，避免预览出现半个字符。
            return char.IsHighSurrogate(_preedit[^1]) ? _preedit[..^1] : _preedit;
        }
    }

    /// <summary>由视图在 <c>TextPresenter.PreeditText</c> 变化时调用。</summary>
    public void Update(string? preedit)
    {
        var next = preedit ?? string.Empty;
        if (string.Equals(next, _preedit, StringComparison.Ordinal)) return;
        _preedit = next;
        Changed?.Invoke();
    }

    /// <summary>组合结束（提交或取消）。</summary>
    public void Clear() => Update(null);
}
