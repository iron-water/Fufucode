using System;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>
/// 所有原生模型客户端共用的重试规则：只对限流、5xx、传输与明确的上游瞬时流错误重试；
/// 鉴权、参数与内容错误立即失败。退避无随机抖动，便于测试与诊断复现。
/// </summary>
internal static class ModelRetryPolicy
{
    public static bool IsRetryable(Exception error) => error switch
    {
        ModelClientException mapped => mapped.IsRetryable ||
                                       mapped.Kind == ModelClientErrorKind.Transport ||
                                       IsTransientStreamError(mapped),
        _ => false,
    };

    /// <summary>
    /// 网关会把上游断流包装成流内 error 帧（StreamError），而不是 HTTP/传输异常。
    /// 只有明确是连接/上游瞬时故障的文本才重试。
    /// </summary>
    public static bool IsTransientStreamError(ModelClientException error)
    {
        if (error.Kind != ModelClientErrorKind.StreamError) return false;
        var text = string.Join(' ', error.ErrorCode ?? "", error.ErrorBody ?? "", error.Message ?? "");
        return text.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("broke off", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("socket", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("upstream", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("overloaded", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("502", StringComparison.Ordinal) ||
               text.Contains("503", StringComparison.Ordinal) ||
               text.Contains("504", StringComparison.Ordinal);
    }

    /// <summary>400ms、800ms、1.6s…… 封顶 5s。</summary>
    public static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(5000, 400 * Math.Pow(2, attempt - 1)));
        return Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// 包装流式回调并记录“是否已经向调用方交付过可见增量”。
    /// usage 与完成事件不算可见内容：它们不会造成重放重复。
    /// </summary>
    public static ModelStreamCallbacks? TrackDeliveries(ModelStreamCallbacks? callbacks, Action delivered)
    {
        if (callbacks is null) return null;
        return new ModelStreamCallbacks
        {
            OnTextDelta = delta => { delivered(); callbacks.OnTextDelta?.Invoke(delta); },
            OnThinkingDelta = delta => { delivered(); callbacks.OnThinkingDelta?.Invoke(delta); },
            OnToolCallChunk = (id, name, arguments) =>
            {
                delivered();
                callbacks.OnToolCallChunk?.Invoke(id, name, arguments);
            },
            OnToolCallChunkIndexed = (index, id, name, arguments) =>
            {
                delivered();
                callbacks.OnToolCallChunkIndexed?.Invoke(index, id, name, arguments);
            },
            OnUsage = callbacks.OnUsage,
            OnCompleted = callbacks.OnCompleted,
            OnRestart = callbacks.OnRestart,
        };
    }
}
