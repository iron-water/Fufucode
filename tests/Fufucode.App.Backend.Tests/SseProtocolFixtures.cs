using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.App.Backend.Tests;

/// <summary>
/// 按脚本分片回放字节流的 Stream：每次 ReadAsync 只返回一段，
/// 用于验证解析器对网络任意切分（含行中断、多字节 UTF-8 中断）的健壮性。
/// </summary>
internal sealed class ScriptedByteStream(IReadOnlyList<byte[]> chunks) : Stream
{
    private int _index;
    private int _offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        while (_index < chunks.Count)
        {
            var chunk = chunks[_index];
            if (_offset >= chunk.Length)
            {
                _index++;
                _offset = 0;
                continue;
            }

            var take = Math.Min(chunk.Length - _offset, buffer.Length);
            chunk.AsMemory(_offset, take).CopyTo(buffer);
            _offset += take;
            return ValueTask.FromResult(take);
        }

        return ValueTask.FromResult(0);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 记录并脚本化返回 HTTP 响应的处理器：不发任何真实网络请求。
/// </summary>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _responder;

    public ScriptedHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public List<Uri> RequestUris { get; } = new();
    public List<string> RequestBodies { get; } = new();

    public string LastRequestBody => RequestBodies.Count == 0 ? "" : RequestBodies[^1];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        RequestUris.Add(request.RequestUri!);
        RequestBodies.Add(body);
        return _responder(request, body);
    }

    /// <summary>构造 200 流式响应，内容由给定字节分片回放。</summary>
    public static HttpResponseMessage SseResponse(params byte[][] chunks)
    {
        var content = new StreamContent(new ScriptedByteStream(chunks));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>构造指定状态码的 JSON 错误响应。</summary>
    public static HttpResponseMessage JsonError(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// 真实 SSE 报文夹具：按 OpenAI Chat Completions 流式格式生成 data 行。
/// </summary>
internal static class Sse
{
    public const string Done = "data: [DONE]\n\n";

    /// <summary>单条 data 帧（含空行结束）。</summary>
    public static string Frame(string json) => $"data: {json}\n\n";

    /// <summary>把全部文本按 UTF-8 一次性分片。</summary>
    public static byte[][] Utf8(string text) => [Encoding.UTF8.GetBytes(text)];

    /// <summary>把文本切成每片固定字节数的分片（用于模拟任意网络切分）。</summary>
    public static byte[][] Split(string text, int bytesPerChunk)
    {
        var all = Encoding.UTF8.GetBytes(text);
        var list = new List<byte[]>();
        for (var offset = 0; offset < all.Length; offset += bytesPerChunk)
        {
            var size = Math.Min(bytesPerChunk, all.Length - offset);
            list.Add(all[offset..(offset + size)]);
        }
        return list.Count == 0 ? [[]] : list.ToArray();
    }
}

/// <summary>
/// 纯协议回放的流式模型客户端：只实现 IStreamingModelClient 路径，
/// 用真实 SSE 报文经由 ServerSentEvents/解析器产出结果；一次性路径被显式拒绝，
/// 从而证明 Agent Loop 确实走了流式分支而不是悄悄回退。
/// </summary>
internal sealed class SseReplayModelClient : IPureModelClient, IStreamingModelClient
{
    private readonly Queue<IReadOnlyList<string>> _turns = new();

    public List<PureModelRequest> Requests { get; } = new();
    public List<string> TextDeltas { get; } = new();
    public List<string> ToolChunkEvents { get; } = new();
    public List<string> ToolChunkIndexedEvents { get; } = new();
    public List<string> FrameScripts { get; } = new();
    public int NonStreamingCalls { get; private set; }
    public int StreamingCalls { get; private set; }

    public SseReplayModelClient EnqueueTurn(params string[] frames)
    {
        _turns.Enqueue(frames);
        return this;
    }

    public Task<PureModelResponse> GenerateAsync(PureModelRequest request, CancellationToken cancellationToken = default)
    {
        NonStreamingCalls++;
        throw new InvalidOperationException("本夹具只提供流式路径，Agent Loop 不得回退到一次性请求。");
    }

    public async Task<PureModelResponse> GenerateStreamingAsync(
        PureModelRequest request,
        ModelStreamCallbacks? callbacks = null,
        CancellationToken cancellationToken = default)
    {
        StreamingCalls++;
        Requests.Add(request);
        if (_turns.Count == 0)
        {
            throw new InvalidOperationException("夹具脚本已用尽。");
        }

        var frames = _turns.Dequeue();
        var text = string.Concat(frames);
        FrameScripts.Add(text);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var parser = new ChatCompletionSseParser();

        await foreach (var sse in ServerSentEvents.ReadAsync(stream, cancellationToken))
        {
            if (sse.IsDone) break;
            parser.TryApply(sse, callbacks);
        }

        return parser.Completion.ToResponse();
    }

    public ModelStreamCallbacks RecordingCallbacks() => new()
    {
        OnTextDelta = delta => TextDeltas.Add(delta),
        OnToolCallChunk = (id, name, args) => ToolChunkEvents.Add($"{id}|{name}|{args}"),
        OnToolCallChunkIndexed = (index, id, name, args) =>
            ToolChunkIndexedEvents.Add($"{index}|{id ?? ""}|{name ?? ""}|{args}"),
    };
}
