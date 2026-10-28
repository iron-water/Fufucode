using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.App.Abstractions;

public sealed record ChatImage(string Data, string MimeType);
public interface IChatAttachments
{
    bool CanSendImages { get; }
    Task SendWithImagesAsync(string text, IReadOnlyList<ChatImage> images, CancellationToken cancellationToken = default);
}

/// <summary>
/// 可选能力：会话能回看自己最后一条用户消息，用于「重发上一条」的 restart 入口。
/// 只有把历史握在自己里的后端（FUMO）能实现；冻结的 <see cref="IChatSession"/> 不因此改动。
/// </summary>
public interface IResumableChatSession
{
    /// <summary>最后一条有内容的用户消息；没有时为 null。</summary>
    string? LastUserPrompt { get; }
}
public interface IExecutionModeSession
{
    bool SupportsExecutionModes { get; }
    string ExecutionMode { get; }
    event System.Action<string>? ExecutionModeChanged;
    Task SetExecutionModeAsync(string mode, CancellationToken cancellationToken = default);
}
