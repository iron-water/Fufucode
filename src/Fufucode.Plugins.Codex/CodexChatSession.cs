using System.Text.Json;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Backend;

/// <summary>Codex 的参数与 JSONL 协议；进程生命周期由共享基类负责。</summary>
public sealed class CodexChatSession(
    string executablePath, string workingDirectory, string? persistFilePath = null,
    string? initialThreadId = null, string? model = null)
    : CliChatSession(executablePath, workingDirectory, persistFilePath, initialThreadId, model)
{
    public override string BackendId => "codex";
    public override string BackendName => "Codex";
    public override BackendPresentationProfile Presentation => BackendPresentationProfile.Codex;
    public override string? ApprovalDiagnostic => "Codex CLI 以 bypass 模式运行；审批桥尚未接入。";

    protected override IReadOnlyList<string> CreateArguments(string text, string? sessionId)
    {
        var arguments = new List<string> { "exec" };
        if (!string.IsNullOrEmpty(sessionId)) arguments.AddRange(["resume", sessionId]);
        arguments.AddRange(["--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox", "--json"]);
        if (!string.IsNullOrWhiteSpace(PersistedModel)) arguments.AddRange(["-m", PersistedModel]);
        arguments.Add(text);
        return arguments;
    }

    protected override ProtocolEvent? ReadEvent(JsonElement value) => StringValue(value, "type") switch
    {
        "thread.started" when StringValue(value, "thread_id") is { } id => new(EventKind.SessionId, id),
        "turn.started" => new(EventKind.Started),
        "item.completed" when value.TryGetProperty("item", out var item) &&
                              StringValue(item, "type") == "agent_message" &&
                              StringValue(item, "text") is { } text => new(EventKind.Text, text),
        "turn.completed" => new(EventKind.Completed),
        "error" when StringValue(value, "message") is { } error => new(EventKind.Error, error),
        _ => null,
    };
}
