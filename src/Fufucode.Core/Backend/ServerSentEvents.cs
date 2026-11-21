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

    /// <summary>事件是否既无文本也无元数据。</summary>
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
        var atStart = true;
        var skipLf = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            var count = decoder.GetChars(buffer, 0, read, charBuffer, 0, flush: read == 0);
            var start = atStart && count > 0 && charBuffer[0] == '\uFEFF' ? 1 : 0;
            if (count > 0) atStart = false;
            for (var i = start; i < count; i++)
            {
                var c = charBuffer[i];
                if (skipLf)
                {
                    skipLf = false;
                    if (c == '\n') { start = i + 1; continue; }
                }
                if (c is not ('\n' or '\r')) continue;

                // CR 本身即换行；跨分片时跳过可选 LF，不为已完整的事件等待下次网络读取。
                skipLf = c == '\r';
                var line = pending.Length == 0 ? new string(charBuffer, start, i - start)
                    : pending.Append(charBuffer, start, i - start).ToString();
                pending.Clear();
                start = i + 1;
                if (TryApply(line, state, out var dispatched))
                {
                    yield return dispatched;
                    if (dispatched.IsDone) yield break;
                }
            }
            pending.Append(charBuffer, start, count - start);
            if (read == 0) break;
        }

        // 流意外结束时，把已累积但尚未以空行收尾的事件一并派发，避免丢最后一块数据。
        if (pending.Length > 0)
            TryApply(pending.ToString(), state, out _);

        if (state.HasData)
        {
            yield return state.Build();
        }
    }

    /// <summary>把一行文本喂给解析状态机；返回 true 表示空行派发了一个事件。</summary>
    internal static bool TryApply(string line, EventState state, out ServerSentEvent dispatched)
    {
        dispatched = default!;

        if (line.Length == 0)
        {
            var hasData = state.HasData;
            if (hasData) dispatched = state.Build();
            state.ResetEvent();
            return hasData;
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
                if (!value.Contains('\0')) state.Id = value;
                break;
            case "retry":
                if (value.Length > 0 && value.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0)
                    state.Retry = value;
                break;
            default:
                // 未知字段按规范忽略。
                break;
        }

        return false;
    }

    /// <summary>单条事件的累积状态（多行 data、event/id/retry 字段）。</summary>
    internal sealed class EventState
    {
        private readonly StringBuilder _data = new();

        public string? EventName { get; set; }
        public string? Id { get; set; }
        public string? Retry { get; set; }

        public bool HasData { get; private set; }

        public void AppendData(string value)
        {
            if (HasData) _data.Append('\n');
            _data.Append(value);
            HasData = true;
        }

        public ServerSentEvent Build() =>
            new(EventName, _data.ToString(), Id, Retry);

        // data 和 event 仅属于一条事件；last-event-id 与 retry 属于整条流。
        public void ResetEvent()
        {
            _data.Clear();
            HasData = false;
            EventName = null;
        }
    }
}
