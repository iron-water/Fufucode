namespace Fufucode.Core.Backend;

/// <summary>Codex 的模型目录与通用 CLI 状态投影。</summary>
public sealed class CodexSessionCoordinator(CodexChatSession chat) : CliSessionCoordinator(chat)
{
    public override BackendId Backend => BackendId.Codex;
    protected override string ModelProvider => "openai";
    protected override string DefaultThinkingLevel => "medium";
    protected override (string Id, string Name)[] Models =>
        [("o3-mini", "o3-mini"), ("o3", "o3"), ("o1", "o1"), ("gpt-4o", "gpt-4o")];
}
