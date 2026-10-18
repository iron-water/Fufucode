using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fufucode.Core.RemoteControl;

/// <summary>
/// WebSocket 增量事件。手机端在收到 snapshot 后，按后续增量维护本地状态。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RemoteSnapshotUpdate), "snapshot")]
[JsonDerivedType(typeof(RemoteEntryUpdate), "entry")]
[JsonDerivedType(typeof(RemoteApprovalAddedUpdate), "approvalAdded")]
[JsonDerivedType(typeof(RemoteApprovalResolvedUpdate), "approvalResolved")]
[JsonDerivedType(typeof(RemoteStatusUpdate), "status")]
[JsonDerivedType(typeof(RemoteNoticeUpdate), "notice")]
public abstract record RemoteUpdate;

/// <summary>整会话快照（连接建立或重连补齐）。</summary>
public sealed record RemoteSnapshotUpdate(RemoteSessionSnapshot Session) : RemoteUpdate;

/// <summary>按 Id 覆盖一条时间线条目。</summary>
public sealed record RemoteEntryUpdate(RemoteTimelineEntry Entry) : RemoteUpdate;

/// <summary>新增一个待审批工具调用。</summary>
public sealed record RemoteApprovalAddedUpdate(RemoteApprovalRequest Approval) : RemoteUpdate;

/// <summary>一个审批请求已决定（含失效）。</summary>
public sealed record RemoteApprovalResolvedUpdate(RemoteApprovalResolution Resolution) : RemoteUpdate;

/// <summary>活动/连接/队列状态变化。</summary>
public sealed record RemoteStatusUpdate(string Activity, string Connection, bool CanSend, bool CanStop, int QueuedCount) : RemoteUpdate;

/// <summary>提示性消息（例如停止后清空的队列内容）。</summary>
public sealed record RemoteNoticeUpdate(string Message) : RemoteUpdate;

/// <summary>远控线协议 JSON 选项（camelCase，大小写不敏感，含多态判别字段）。</summary>
public static class RemoteWire
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // 枚举按名称上线：手机端 JS 直接按字符串比较（数字枚举会让角色/状态全部判错）。
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// 按多态基类序列化增量更新。必须以 <see cref="RemoteUpdate"/> 为声明类型调用：
    /// System.Text.Json 只在声明类型为多态基类时写入类型判别字段，直接序列化派生类型会省略它，
    /// 导致对端无法还原具体类型。
    /// </summary>
    public static string SerializeUpdate(RemoteUpdate update) =>
        JsonSerializer.Serialize(update, typeof(RemoteUpdate), Options);

    public static T? Deserialize<T>(byte[] body) =>
        body.Length == 0 ? default : JsonSerializer.Deserialize<T>(body, Options);
}
