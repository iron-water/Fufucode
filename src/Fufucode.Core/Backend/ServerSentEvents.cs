using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fufucode.Core.Backend;

/// <summary>一条解析后的 Server-Sent Events 事件。</summary>
public sealed record ServerSentEvent(string? EventName, string Data, string? Id, string? Retry)
{
    /// <summary>是否为结束哨兵（data 去除空白后等于 [DONE]）。</summary>
    public bool IsDone => string.Equals(Data.Trim(), "[DONE]", StringComparison.Ordinal);

    /// <summary>是否为只有注释/心跳、没有 data 的占位事件。</summary>
    public bool IsEmpty => Data.Length == 0 && EventName is null && Id is null && Retry is null;
}

/// <summary>
/// 最小但完整的 SSE 行协议解析器（对应 W3C EventSource 的分帧规则）：
/// 按 CRLF / LF / CR 切行，忽略 ":" 注释行，"data:" 多行以 "\n" 连接，空行派发事件。
/// </summary>
public static class ServerSentEvents
{
    /// <summary>
    /// 从响应流增量读取 SSE 事件；流结束或收到 [DONE] 后停止。
    /// 增量读取保证长响应不会被整体缓冲，且取消令牌可立即中断。
    /// </summary>
    public static async IAsyncEnumerable<ServerSentEvent> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[4096];
        var pending = new StringBuilder();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        // 有状态解码器：多字节字符被网络分片截断时不会丢字。
        var decoder = encoding.GetDecoder();
        var charBuffer = new char[encoding.GetMaxCharCount(buffer.Length)];
        var state = new EventState();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;

            var charCount = decoder.GetChars(buffer, 0, read, charBuffer, 0, flush: false);
            pending.Append(charBuffer, 0, charCount);

            while (TryTakeLine(pending, out var line))
            {
                if (TryApply(line, state, out var dispatched))
                {
                    yield return dispatched;
                    if (dispatched.IsDone) yield break;
                }
            }
        }

        var remaining = decoder.GetChars(Array.Empty<byte>(), 0, 0, charBuffer, 0, flush: true);
        if (remaining > 0) pending.Append(charBuffer, 0, remaining);

        // 流意外结束时，把已累积但尚未以空行收尾的事件一并派发，避免丢最后一块数据。
        if (pending.Length > 0)
        {
            while (TryTakeLine(pending, out var lastLine))
            {
                if (TryApply(lastLine, state, out var tailLine))
                {
                    yield return tailLine;
                    if (tailLine.IsDone) yield break;
                }
            }

            if (TryApply(pending.ToString(), state, out var tail))
            {
                yield return tail;
                if (tail.IsDone) yield break;
            }
        }

        if (state.HasData)
        {
            yield return state.Build();
        }
    }

    /// <summary>把一行文本喂给解析状态机；返回 true 表示空行派发了一个事件。</summary>
    internal static bool TryApply(string line, EventState state, out ServerSentEvent dispatched)
    {
        dispatched = default!;

        // 去掉行尾 CR（CRLF 与裸 CR 行尾都会走到这里）。
        if (line.EndsWith('\r')) line = line[..^1];

        if (line.Length == 0)
        {
            if (!state.HasContent) return false;
            dispatched = state.Build();
            state.Reset();
            return true;
        }

        if (line[0] == ':') return false; // 注释/心跳行

        var colon = line.IndexOf(':');
        string field;
        string value;
        if (colon < 0)
        {
            field = line;
            value = "";
        }
        else
        {
            field = line[..colon];
            value = line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
        }

        switch (field)
        {
            case "data":
                state.AppendData(value);
                break;
            case "event":
                state.EventName = value;
                break;
            case "id":
                state.Id = value;
                break;
            case "retry":
                state.Retry = value;
                break;
            default:
                // 未知字段按规范忽略。
                break;
        }

        return false;
    }

    private static bool TryTakeLine(StringBuilder pending, out string line)
    {
        for (var i = 0; i < pending.Length; i++)
        {
            var c = pending[i];
            if (c != '\n' && c != '\r') continue;

            // CRLF 视为一个换行：等下一字符到齐再切，避免把 LF 切成空行。
            if (c == '\r' && i == pending.Length - 1) break;

            line = pending.ToString(0, i);
            var consumed = c == '\r' && pending[i + 1] == '\n' ? i + 2 : i + 1;
            pending.Remove(0, consumed);
            return true;
        }

        line = "";
        return false;
    }

    /// <summary>单条事件的累积状态（多行 data、event/id/retry 字段）。</summary>
    internal sealed class EventState
    {
        private readonly StringBuilder _data = new();

        public string? EventName { get; set; }
        public string? Id { get; set; }
        public string? Retry { get; set; }

        public bool HasData => _data.Length > 0;

        /// <summary>是否已有任何字段内容（决定空行是否派发事件）。</summary>
        public bool HasContent => _data.Length > 0;

        public void AppendData(string value)
        {
            if (_data.Length > 0) _data.Append('\n');
            _data.Append(value);
        }

        public ServerSentEvent Build() =>
            new(EventName, _data.ToString(), Id, Retry);

        public void Reset()
        {
            _data.Clear();
            EventName = null;
            Id = null;
            Retry = null;
        }
    }
}
