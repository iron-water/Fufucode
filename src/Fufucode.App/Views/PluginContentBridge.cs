using System;
using System.Threading.Tasks;

namespace Fufucode.App.Views;

/// <summary>
/// 富内容插件与当前会话之间的窄接口。
///
/// 插件控件不直接持有 ViewModel：它只知道"提交这段文本"和"填进输入框"。
/// 回调由 ChatPage 在其 DataContext 就绪时注入；没有会话时提交返回 false，
/// 插件应当保持界面状态而不是假装已发送。
/// </summary>
public static class PluginContentBridge
{
    public static Func<string, Task<bool>> SubmitAsync { get; set; } = _ => Task.FromResult(false);

    public static Action<string> InsertDraft { get; set; } = _ => { };

    /// <summary>会话切换或页面卸载时恢复为空实现，避免回调指向已失效的 ViewModel。</summary>
    public static void Reset()
    {
        SubmitAsync = _ => Task.FromResult(false);
        InsertDraft = _ => { };
    }
}
