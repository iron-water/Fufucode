namespace Fufucode.Core.Backend;

/// <summary>Dsh 的模型目录与通用 CLI 状态投影。</summary>
public sealed class DshSessionCoordinator(DshChatSession chat) : CliSessionCoordinator(chat)
{
    public override BackendId Backend => BackendId.Dsh;
    protected override string ModelProvider => "deepseek";
    protected override (string Id, string Name)[] Models =>
        [("deepseek-coder", "DeepSeek Coder"), ("deepseek-chat", "DeepSeek Chat"),
         ("deepseek-reasoner", "DeepSeek Reasoner")];
}
