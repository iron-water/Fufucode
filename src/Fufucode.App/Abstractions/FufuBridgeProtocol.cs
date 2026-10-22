using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fufucode.App.Abstractions;

/// <summary>
/// FUFU ⇄ Fufucode Bridge 命名管道协议（JSON Lines，一行一条 UTF-8 JSON）。
/// 管道名 <see cref="PipeName"/>；FUFU 是服务端，Bridge 插件是客户端。
/// 协议版本不一致时 helloAck 的 <c>accepted=false</c> 并由 FUFU 端主动断开。
/// </summary>
public static class FufuBridgeProtocol
{
    public const string PipeName = "fufu.fufucode.v1";          // FUFU←插件（插件写/FUFU 读）
    public const string PipeNameBack = "fufu.fufucode.v1.back"; // FUFU→插件（FUFU 写/插件读）
    public const int ProtocolVersion = 1;

    // Bridge → FUFU
    public const string KindHello = "hello";                 // {kind, pluginVersion, fufucodeVersion, pid}
    public const string KindSnapshot = "snapshot";           // {kind, sessions:[{id,title,project,activity,connection,model,canSend,canStop,queued,isCurrent}]}
    public const string KindEvent = "event";                 // {kind, sessionId, type, summary}
    public const string KindApproval = "approval";           // {kind, requestId, sessionId, tool, message}
    public const string KindApprovalClosed = "approvalClosed";// {kind, requestId}
    public const string KindPing = "ping";                   // {kind}

    // FUFU → Bridge
    public const string KindHelloAck = "helloAck";           // {kind, protocolVersion, accepted, error}
    public const string KindSubmit = "submit";               // {kind, sessionId, text}
    public const string KindApprove = "approve";             // {kind, requestId, approved}
    public const string KindSelect = "select";               // {kind, sessionId}
    public const string KindPong = "pong";                   // {kind}
    public const string KindError = "error";                 // {kind, code, message}

    public static string Write(JsonObject message) => message.ToJsonString();

    public static JsonObject? ReadLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public static string Kind(JsonObject message) =>
        message["kind"]?.GetValue<string>() ?? string.Empty;

    /// <summary>会话目录条目（snapshot.sessions[] 元素与 select/submit 的 id 值）。</summary>
    public sealed record SessionEntry(string Id, string Title, string Project, string Activity,
        string Connection, string? Model, bool CanSend, bool CanStop, int Queued, bool IsCurrent);
}
