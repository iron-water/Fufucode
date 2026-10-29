using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// Case 01: a malformed data frame in the middle of a tool-call stream must fail the whole
/// streaming round before any read/write tool dispatches. Explicit heartbeats stay ignorable,
/// and prefix/suffix fragments still concatenate into executable arguments when the stream is intact.
/// </summary>
internal static class Case01InvalidJsonTests
{
    private const string Prefix = "{\"path\":\"target.txt\",\"content\":\"";
    private const string Suffix = "changed\"}";

    public static async Task RunAsync(Checks checks)
    {
        await StreamAsyncFailsOnCorruptFrameAsync(checks);
        await AgentLoopRejectsCorruptToolRoundAsync(checks);
        await HeartbeatsPreserveValidToolCallAsync(checks);
    }

    private static async Task StreamAsyncFailsOnCorruptFrameAsync(Checks checks)
    {
        var client = NewClient(_ => SseResponse(Split(CorruptFrames(), 7)));
        try
        {
            await client.StreamAsync(NewRequest(), null);
            checks.Check(false, "StreamAsync rejects corrupt data frame");
        }
        catch (ModelClientException error)
        {
            checks.Check(true, "StreamAsync rejects corrupt data frame");
            checks.Equal(ModelClientErrorKind.StreamError, error.Kind, "corrupt frame maps to StreamError");
            checks.Equal("invalid_sse_json", error.ErrorCode, "corrupt frame carries stable error code");
            checks.Check(error.InnerException is JsonException, "original JSON failure retained as inner exception");
        }
    }

    private static async Task AgentLoopRejectsCorruptToolRoundAsync(Checks checks)
    {
        var workspace = NewWorkspace();
        try
        {
            var target = Path.Combine(workspace, "target.txt");
            await File.WriteAllTextAsync(target, "original");
            var requests = 0;
            var client = NewClient(_ => SseResponse(Split(
                ++requests == 1 ? CorruptFrames() : FinalFrames(), 7)));
            var loop = new NativeAgentLoop(client, new ProductionToolDispatcher(workspace), maxIterations: 2);
            var history = new List<PureAgentMessage> { new(PureAgentRole.User, "read then write target.txt") };
            var started = 0;
            var chunks = 0;
            var completed = false;

            var result = await loop.ExecuteLoopAsync(history, new[] { "read_file", "write_file" }, "fixture", null,
                new ModelStreamCallbacks
                {
                    OnToolCallChunk = (_, _, _) => chunks++,
                    OnCompleted = () => completed = true,
                },
                onToolStarting: _ => started++);

            checks.Equal(PureAgentTurnStatus.Failed, result.Status, "corrupt middle frame fails the whole round");
            checks.Check(result.Error?.Contains("损坏的 JSON") == true, "failure explains the corrupt JSON frame");
            checks.Equal(0, result.ToolResults.Count, "no tool result after corruption");
            checks.Equal(0, started, "completed read call and partial write call never start");
            checks.Equal(0, loop.CompletedModelCalls, "corrupt round is not counted as completed");
            checks.Check(!completed, "completion callback is not fired for corrupt round");
            checks.Equal(1, requests, "corrupt frame neither retries nor falls back to non-streaming");
            checks.Equal("original", await File.ReadAllTextAsync(target), "file content is untouched");
            checks.Equal(1, history.Count, "partial tool call never enters conversation history");
            checks.Equal(2, chunks, "valid read chunk and write prefix are delivered before the corrupt frame");
        }
        finally
        {
            TestTemp.Delete(workspace);
        }
    }

    private static async Task HeartbeatsPreserveValidToolCallAsync(Checks checks)
    {
        var workspace = NewWorkspace();
        try
        {
            var target = Path.Combine(workspace, "target.txt");
            await File.WriteAllTextAsync(target, "original");
            var requests = 0;
            var client = NewClient(_ => SseResponse(Split(
                ++requests == 1 ? HeartbeatFrames() : FinalFrames(), 7)));
            var loop = new NativeAgentLoop(client, new ProductionToolDispatcher(workspace), maxIterations: 2);
            var history = new List<PureAgentMessage> { new(PureAgentRole.User, "read then write target.txt") };
            var started = 0;
            var chunks = 0;
            var deltas = new List<string>();

            var result = await loop.ExecuteLoopAsync(history, new[] { "read_file", "write_file" }, "fixture", null,
                new ModelStreamCallbacks
                {
                    OnToolCallChunk = (_, _, _) => chunks++,
                    OnTextDelta = deltas.Add,
                },
                onToolStarting: _ => started++);

            checks.Equal(PureAgentTurnStatus.Completed, result.Status, "explicit heartbeats do not fail the round");
            checks.Equal(2, result.ToolResults.Count, "read and write tool calls both dispatch");
            checks.Check(result.ToolResults[0].Success, "read_file succeeds");
            checks.Equal("original", result.ToolResults[0].Content, "read_file sees the original content");
            checks.Check(result.ToolResults[1].Success, "write_file succeeds");
            checks.Equal("changed", await File.ReadAllTextAsync(target), "prefix and suffix arguments concatenate and execute");
            checks.Equal(5, history.Count, "history contains user, tool assistant, two tool results and final answer");
            checks.Equal(2, requests, "final non-tool round is requested after tool results");
            checks.Equal(2, started, "both tools start exactly once");
            checks.Equal(1, deltas.Count, "final round text delta is streamed");
            checks.Equal("done", result.Response.Text, "final answer text is aggregated");
            checks.Equal(3, chunks, "read chunk plus write prefix and suffix are delivered");
        }
        finally
        {
            TestTemp.Delete(workspace);
        }
    }

    private static string CorruptFrames() =>
        ReadFrame() +
        WritePrefixFrame() +
        Frame("{\"choices\":[{\"delta\":BROKEN}") +
        WriteSuffixFrame() +
        FinishFrame() + Done;

    private static string HeartbeatFrames() =>
        ReadFrame() +
        WritePrefixFrame() +
        ": keep-alive\n\nevent: ping\n\n" + Frame("   ") + Frame(" ping ") +
        WriteSuffixFrame() +
        FinishFrame() + Done;

    private static string FinalFrames() =>
        Frame("{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}") + Done;

    private static string ReadFrame() => ToolCallFrame("read1", "read_file", "{\"path\":\"target.txt\"}", 0);

    private static string WritePrefixFrame() => ToolCallFrame("write1", "write_file", Prefix, 1);

    private static string WriteSuffixFrame() => ToolCallFrame(null, null, Suffix, 1);

    private static string FinishFrame() =>
        Frame("{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}");

    private static string ToolCallFrame(string? id, string? name, string arguments, int index) =>
        Frame(JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new
                    {
                        tool_calls = new[] { new { index, id, function = new { name, arguments } } },
                    },
                },
            },
        }));

    private const string Done = "data: [DONE]\n\n";

    private static string Frame(string json) => "data: " + json + "\n\n";

    private static PureModelRequest NewRequest() => new(
        new List<PureAgentMessage> { new(PureAgentRole.User, "hi") },
        Array.Empty<string>(),
        "fixture");

    private static OpenAiCompatibleModelClient NewClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new("https://fixture.invalid/v1", "offline", "fixture", httpClient: new HttpClient(new ScriptedHandler(responder)));

    private static string NewWorkspace()
    {
        return TestTemp.Create("01");
    }

    private static HttpResponseMessage SseResponse(byte[][] chunks)
    {
        var content = new StreamContent(new ChunkedStream(chunks));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[][] Split(string text, int bytesPerChunk)
    {
        var all = Encoding.UTF8.GetBytes(text);
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < all.Length; offset += bytesPerChunk)
            chunks.Add(all[offset..Math.Min(offset + bytesPerChunk, all.Length)]);
        return chunks.Count == 0 ? [[]] : chunks.ToArray();
    }

    /// <summary>Offline HTTP transport: records nothing, sends no real request.</summary>
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    /// <summary>Replays scripted chunks verbatim; never coalesces or re-splits.</summary>
    private sealed class ChunkedStream(byte[][] chunks) : Stream
    {
        private int _index;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_index == chunks.Length) return ValueTask.FromResult(0);
            var chunk = chunks[_index++];
            if (chunk.Length > buffer.Length) throw new InvalidOperationException("Read buffer smaller than scripted chunk.");
            chunk.CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
