using System.Text.Json;
using Fufucode.App.Abstractions;

namespace Fufucode.Core.Backend;

/// <summary>DSH 的参数与 JSONL 协议；进程生命周期由共享基类负责。</summary>
public sealed class DshChatSession(
    string executablePath, string workingDirectory, string? persistFilePath = null,
    string? initialSessionId = null)
    : CliChatSession(executablePath, workingDirectory, persistFilePath, initialSessionId)
{
    public override string BackendId => "dsh";
    public override string BackendName => "DSH";
    public override BackendPresentationProfile Presentation => BackendPresentationProfile.Dsh;
    public override string? ApprovalDiagnostic => "DeepSeek Harness 审批桥尚未接入。";
    protected override string? PersistedModel => CurrentModel;

    protected override IReadOnlyList<string> CreateArguments(string text, string? sessionId)
    {
        var arguments = new List<string> { "--profile", "headless" };
        if (!string.IsNullOrEmpty(sessionId)) arguments.AddRange(["--session-id", sessionId]);
        arguments.AddRange(["--json", text]);
        return arguments;
    }

    protected override ProtocolEvent? ReadEvent(JsonElement value) => StringValue(value, "type") switch
    {
        "session" when StringValue(value, "sessionId") is { } id => new(EventKind.SessionId, id),
        "status" when StringValue(value, "phase") == "turn_start" => new(EventKind.Started),
        "status" when StringValue(value, "phase") == "turn_end" => new(EventKind.Completed),
        "text" when StringValue(value, "text") is { } text => new(EventKind.Text, text),
        "final" when StringValue(value, "text") is { } text => new(EventKind.FinalText, text),
        _ => null,
    };
}
