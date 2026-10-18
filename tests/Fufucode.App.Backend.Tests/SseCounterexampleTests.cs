using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 红/绿对照反例：本文件刻意实现两种常见但错误的流式处理写法，
/// 证明 SSE 与工具分片断言不是「怎么写都能过」的空断言。
/// 反例失败、生产实现通过，才说明断言具有鉴别力。
/// </summary>
internal static class SseCounterexampleTests
{
    public static async Task RunAsync(Checks checks)
    {
        await NaiveDecodeCorruptsMultibyteAsync(checks);
        await NaiveLastFragmentWinsAsync(checks);
        await NaiveDoubleNewlineSplitAsync(checks);
        await NaiveHidesStreamErrorAsync(checks);
    }

    /// <summary>
    /// 反例 1：每个网络分片独立 UTF-8 解码。
    /// 多字节字符被截断时会产生替换字符，聚合文本与原文不一致。
    /// </summary>
    private static async Task NaiveDecodeCorruptsMultibyteAsync(Checks checks)
    {
        var payload = Encoding.UTF8.GetBytes(
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"中文增量\"}}]}") + Sse.Done);
        var chunks = Sse.Split(
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"中文增量\"}}]}") + Sse.Done, 1);

        // 反例实现：逐片 Encoding.UTF8.GetString，不做跨片状态保持。
        var naive = new StringBuilder();
        using (var stream = new ScriptedByteStream(chunks))
        {
            var buffer = new byte[64];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                naive.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }

        checks.Check(!naive.ToString().Contains("中文增量"),
            "反例（红）：逐片独立解码确实会破坏跨片多字节字符");
        checks.Check(naive.ToString().Contains('\uFFFD'),
            "反例（红）：被截断的中文产生 U+FFFD 替换字符");

        // 生产实现：有状态解码器必须还原原文
        await using var production = new ScriptedByteStream(chunks);
        var restored = new StringBuilder();
        await foreach (var sse in ServerSentEvents.ReadAsync(production))
        {
            restored.Append(sse.Data);
        }
        checks.Check(restored.ToString().Contains("中文增量"),
            "生产实现（绿）：同一 1 字节分片输入下中文完整还原");
        checks.Check(!restored.ToString().Contains('\uFFFD'),
            "生产实现（绿）：不产生任何替换字符");
        checks.Check(payload.Length > 0, "夹具确实使用了非空 UTF-8 字节");
    }

    /// <summary>
    /// 反例 2：工具调用只保留最后一个 arguments 分片（常见「覆盖而非追加」缺陷）。
    /// 结果是被截断的非法 JSON，真实工具调度器无法解析。
    /// </summary>
    private static async Task NaiveLastFragmentWinsAsync(Checks checks)
    {
        const string fragments = "{\"path\":\"README.md\"}";
        var pieces = SplitJson(fragments, 6);
        checks.Check(pieces.Count >= 3, "夹具把 arguments 切成至少 3 片");

        // 反例实现：last write wins
        var naiveArguments = "";
        foreach (var piece in pieces) naiveArguments = piece;

        checks.Check(naiveArguments != fragments,
            "反例（红）：只保留末片导致 arguments 丢失前段");
        checks.Throws<System.Text.Json.JsonException>(
            () => System.Text.Json.JsonDocument.Parse(naiveArguments),
            "反例（红）：被截断的参数不是合法 JSON");

        // 生产实现：顺序追加
        var stream = Sse.Utf8(BuildToolCallFrames(pieces));
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(BuildToolCallFrames(pieces), 7)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        var completion = await client.StreamAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "读") }, Array.Empty<string>()));
        var assembled = completion.Primary!.ToolCalls[0].ArgumentsJson;

        checks.Equal(fragments, assembled, "生产实现（绿）：分片顺序追加还原出完整 parameters");
        using var doc = System.Text.Json.JsonDocument.Parse(assembled);
        checks.Equal("README.md", doc.RootElement.GetProperty("path").GetString(),
            "生产实现（绿）：还原出的参数可被真实解析");
        checks.Equal(1, stream.Length, "夹具字节流非空");
    }

    /// <summary>
    /// 反例 3：只按 "\n\n" 切事件（最常见的简化写法）。
    /// 该写法在纯 LF 报文上"碰巧可用"，但真实网关常用 CRLF 分帧（"\r\n\r\n"），
    /// 此时整段响应被当成一个事件，解析必然失败——因此只测 LF 无法暴露该缺陷。
    /// </summary>
    private static async Task NaiveDoubleNewlineSplitAsync(Checks checks)
    {
        var crlfPayload = "data: {\"a\":1}\r\n\r\ndata: [DONE]\r\n\r\n";

        // 反例实现：按 "\n\n" 切事件、剥掉 "data: " 前缀。
        var naiveTokens = crlfPayload.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        checks.Equal(1, naiveTokens.Length,
            "反例（红）：CRLF 报文里不存在 \"\\n\\n\"，两条事件被并成一条");

        var naiveFirst = naiveTokens[0]["data: ".Length..];
        checks.Throws<System.Text.Json.JsonException>(
            () => System.Text.Json.JsonDocument.Parse(naiveFirst),
            "反例（红）：并成一条后解析失败");

        // 同一 naive 写法在纯 LF 报文上是「碰巧通过」的：这正说明只测 LF 无法暴露该缺陷。
        var lfNaive = crlfPayload.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .ToArray();
        checks.Equal(2, lfNaive.Length,
            "反例（红）：换成 LF 报文后同一 naive 写法可切出 2 条，故 LF 单测不足以暴露 CRLF 缺陷");

        // 生产实现：规范解析器必须剥掉 \r
        await using var stream = new ScriptedByteStream(Sse.Utf8(crlfPayload));
        var events = new List<ServerSentEvent>();
        await foreach (var sse in ServerSentEvents.ReadAsync(stream)) events.Add(sse);
        checks.Equal("{\"a\":1}", events[0].Data, "生产实现（绿）：CRLF 下 data 不含残留 \\r");
        using var doc = System.Text.Json.JsonDocument.Parse(events[0].Data);
        checks.Equal(1, doc.RootElement.GetProperty("a").GetInt32(), "生产实现（绿）：剥净后可直接解析");
    }

    /// <summary>
    /// 反例 4：流内出现 error 对象时仍继续当正常流读取。
    /// 结果是把错误当成功返回空回复，掩盖真实故障。
    /// </summary>
    private static async Task NaiveHidesStreamErrorAsync(Checks checks)
    {
        var frames =
            Sse.Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"partial\"}}]}") +
            Sse.Frame("{\"error\":{\"message\":\"quota exceeded\",\"code\":\"insufficient_quota\"}}") +
            Sse.Done;

        // 反例实现：只取 choices，完全无视 error 字段
        var naiveText = new StringBuilder();
        await using (var naiveStream = new ScriptedByteStream(Sse.Utf8(frames)))
        {
            await foreach (var sse in ServerSentEvents.ReadAsync(naiveStream))
            {
                if (sse.IsDone || sse.Data.Length == 0) continue;
                using var doc = System.Text.Json.JsonDocument.Parse(sse.Data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var choice in choices.EnumerateArray())
                    {
                        if (choice.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("content", out var content) &&
                            content.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            naiveText.Append(content.GetString());
                        }
                    }
                }
            }
        }
        checks.Equal("partial", naiveText.ToString(),
            "反例（红）：忽略 error 后只得到残缺文本且被当作成功结果");

        // 生产实现：必须抛出可判定的结构化错误，而不是返回残缺「成功」回复
        var handler = new ScriptedHttpMessageHandler((_, _) =>
            ScriptedHttpMessageHandler.SseResponse(Sse.Split(frames, 8)));
        var client = new OpenAiCompatibleModelClient("https://api.example.com/v1", "sk-test", defaultModelId: "fixture-model",
            httpClient: new HttpClient(handler));

        var error = await checks.CapturesAsync<ModelClientException>(() => client.StreamAsync(
            new PureModelRequest(new[] { new PureAgentMessage(PureAgentRole.User, "hi") }, Array.Empty<string>())));
        checks.Check(error is not null, "生产实现（绿）：流内错误抛出结构化异常");
        checks.Equal("insufficient_quota", error!.ErrorCode, "生产实现（绿）：暴露 error.code 供上游判定");
    }

    private static List<string> SplitJson(string json, int size)
    {
        var list = new List<string>();
        for (var i = 0; i < json.Length; i += size)
        {
            list.Add(json.Substring(i, Math.Min(size, json.Length - i)));
        }
        return list;
    }

    private static string BuildToolCallFrames(IReadOnlyList<string> pieces)
    {
        var builder = new StringBuilder();
        builder.Append(Sse.Frame(
            "{\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_ce\",\"function\":{\"name\":\"read_file\",\"arguments\":\"\"}}]}}]}"));
        foreach (var piece in pieces)
        {
            var escaped = piece.Replace("\\", "\\\\").Replace("\"", "\\\"");
            builder.Append(Sse.Frame(
                $"{{\"choices\":[{{\"index\":0,\"delta\":{{\"tool_calls\":[{{\"index\":0,\"function\":{{\"arguments\":\"{escaped}\"}}}}]}}}}]}}"));
        }
        builder.Append(Sse.Done);
        return builder.ToString();
    }
}
