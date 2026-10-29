using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// 任务 11 验收：OpenAiCompatibleModelClient 的 RequestTimeout 必须覆盖整次尝试，
/// 包括 headers 之后停滞的成功体、错误体与 SSE；用户取消与内部超时必须区分。
/// 全部经假 HTTP 处理器，不发生真实网络请求。
/// </summary>
internal static class Case11TimeoutTests
{
    private static Checks _checks = null!;

    public static async Task RunAsync(Checks checks)
    {
        _checks = checks;
        Console.WriteLine("== fix11: request timeout scope / cancellation semantics ==");
        await NonStreamingSuccessBodyStallAsync();
        await NonStreamingErrorBodyStallAsync();
        await StreamingSseStallAsync();
        await TimeoutDuringHeadersAsync();
        await UserCancellationBodyAsync();
        await UserCancellationStreamingAsync();
        await UserCancellationHeadersAsync();
        await UserCancellationWithoutConfiguredTimeoutAsync();
        await InjectedHttpClientTimeoutStillRetryableAsync();
        await CompletedResponsesWithinTimeoutAsync();
    }

    // 1. 成功响应头已到、body 停滞：整次尝试超时必须生效且不重试。
    private static async Task NonStreamingSuccessBodyStallAsync()
    {
        var handler = new CountingHandler((_, _) =>
            Task.FromResult(StallResponse("{\"choices\":[", HttpStatusCode.OK)));
        var client = Client(handler, TimeSpan.FromMilliseconds(250));
        var watch = Stopwatch.StartNew();
        var error = await CaptureAsync(() => client.GenerateAsync(Request()));
        watch.Stop();

        Check("success body stall => TimeoutException", error is TimeoutException, TypeOf(error));
        Check("success body stall not user cancellation", error is not OperationCanceledException);
        Check("success body stall single attempt", handler.Calls == 1, $"calls={handler.Calls}");
        Check("success body stall honors timeout budget", watch.ElapsedMilliseconds >= 150 && watch.ElapsedMilliseconds < 8000,
            $"{watch.ElapsedMilliseconds}ms");
    }

    // 2. 错误状态头已到、错误体停滞：读错误体也必须使用整次尝试 token。
    private static async Task NonStreamingErrorBodyStallAsync()
    {
        var handler = new CountingHandler((_, _) =>
            Task.FromResult(StallResponse("{\"error\":{\"message\":\"boom\"", HttpStatusCode.InternalServerError)));
        var client = Client(handler, TimeSpan.FromMilliseconds(250));
        var watch = Stopwatch.StartNew();
        var error = await CaptureAsync(() => client.GenerateAsync(Request()));
        watch.Stop();

        Check("error body stall => TimeoutException", error is TimeoutException, TypeOf(error));
        Check("error body stall not mapped to HTTP status error", error is not ModelClientException, TypeOf(error));
        Check("error body stall single attempt", handler.Calls == 1, $"calls={handler.Calls}");
        Check("error body stall honors timeout budget", watch.ElapsedMilliseconds >= 150 && watch.ElapsedMilliseconds < 8000,
            $"{watch.ElapsedMilliseconds}ms");

        var streamHandler = new CountingHandler((_, _) =>
            Task.FromResult(StallResponse("{\"error\":{\"message\":\"boom\"", HttpStatusCode.BadGateway)));
        var streamClient = Client(streamHandler, TimeSpan.FromMilliseconds(250));
        var streamError = await CaptureAsync(() => streamClient.StreamAsync(Request()));
        Check("streaming error body stall => TimeoutException", streamError is TimeoutException, TypeOf(streamError));
        Check("streaming error body stall single attempt", streamHandler.Calls == 1, $"calls={streamHandler.Calls}");
    }

    // 3. SSE 已收到有效增量后停滞：attempt token 必须贯穿流读取。
    private static async Task StreamingSseStallAsync()
    {
        const string frame = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"PART\"}}]}\n\n";
        var handler = new CountingHandler((_, _) =>
            Task.FromResult(StallResponse(frame, HttpStatusCode.OK, "text/event-stream", stream: true)));
        var client = Client(handler, TimeSpan.FromMilliseconds(250));
        var deltas = new List<string>();
        var watch = Stopwatch.StartNew();
        var error = await CaptureAsync(() => client.StreamAsync(Request(), new ModelStreamCallbacks
        {
            OnTextDelta = deltas.Add,
        }));
        watch.Stop();

        Check("SSE stall => TimeoutException", error is TimeoutException, TypeOf(error));
        Check("SSE stall keeps already-valid delta", deltas.Contains("PART"), string.Join(",", deltas));
        Check("SSE stall single attempt", handler.Calls == 1, $"calls={handler.Calls}");
        Check("SSE stall honors timeout budget", watch.ElapsedMilliseconds >= 150 && watch.ElapsedMilliseconds < 8000,
            $"{watch.ElapsedMilliseconds}ms");
    }

    // 4. 连接后迟迟不给响应头：超时同样覆盖发送阶段。
    private static async Task TimeoutDuringHeadersAsync()
    {
        var handler = new CountingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = Client(handler, TimeSpan.FromMilliseconds(250));
        var error = await CaptureAsync(() => client.GenerateAsync(Request()));
        Check("headers stall => TimeoutException", error is TimeoutException, TypeOf(error));
        Check("headers stall single attempt", handler.Calls == 1, $"calls={handler.Calls}");
    }

    // 5. 用户在响应体阶段取消：必须是取消，不能冒充内部超时，也不能重试。
    private static async Task UserCancellationBodyAsync()
    {
        var handler = new CountingHandler((_, _) =>
            Task.FromResult(StallResponse("{\"choices\":[", HttpStatusCode.OK)));
        var client = Client(handler, TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        var watch = Stopwatch.StartNew();
        var error = await CaptureAsync(() => client.GenerateAsync(Request(), cts.Token));
        watch.Stop();

        Check("user cancel in body => OperationCanceledException", error is OperationCanceledException, TypeOf(error));
        Check("user cancel in body not TimeoutException", error is not TimeoutException, TypeOf(error));
        Check("user cancel in body single attempt", handler.Calls == 1, $"calls={handler.Calls}");
        Check("user cancel in body not delayed by timeout", watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds}ms");
    }

    // 6. 用户在 SSE 阶段取消：同上。
    private static async Task UserCancellationStreamingAsync()
    {
        const string frame = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"PART\"}}]}\n\n";
        var handler = new CountingHandler((_, _) =>
            Task.FromResult(StallResponse(frame, HttpStatusCode.OK, "text/event-stream", stream: true)));
        var client = Client(handler, TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        var error = await CaptureAsync(() => client.StreamAsync(Request(), null, cts.Token));

        Check("user cancel in SSE => OperationCanceledException", error is OperationCanceledException, TypeOf(error));
        Check("user cancel in SSE not TimeoutException", error is not TimeoutException, TypeOf(error));
        Check("user cancel in SSE single attempt", handler.Calls == 1, $"calls={handler.Calls}");
    }

    // 7. 用户在等待响应头时取消。
    private static async Task UserCancellationHeadersAsync()
    {
        var handler = new CountingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = Client(handler, TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        var error = await CaptureAsync(() => client.GenerateAsync(Request(), cts.Token));

        Check("user cancel before headers => OperationCanceledException", error is OperationCanceledException, TypeOf(error));
        Check("user cancel before headers not TimeoutException", error is not TimeoutException, TypeOf(error));
        Check("user cancel before headers single attempt", handler.Calls == 1, $"calls={handler.Calls}");
    }

    // 8. 未配置 RequestTimeout 时取消路径不能空引用/误包装。
    private static async Task UserCancellationWithoutConfiguredTimeoutAsync()
    {
        var handler = new CountingHandler((_, _) =>
            Task.FromResult(StallResponse("{\"choices\":[", HttpStatusCode.OK)));
        var client = new OpenAiCompatibleModelClient("http://unit.test/v1", "k", "m", new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        var error = await CaptureAsync(() => client.GenerateAsync(Request(), cts.Token));

        Check("no configured timeout + user cancel => OperationCanceledException",
            error is OperationCanceledException, TypeOf(error));
        Check("no configured timeout + user cancel not TimeoutException", error is not TimeoutException, TypeOf(error));
    }

    // 9. 回归：注入 HttpClient 自身 Timeout 仍归类为可重试传输失败。
    private static async Task InjectedHttpClientTimeoutStillRetryableAsync()
    {
        var handler = new CountingHandler(async (_, token) =>
        {
            await Task.Delay(400, token);
            return JsonResponse("{\"choices\":[{\"message\":{\"content\":\"late\"}}]}");
        });
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(60) };
        var client = new OpenAiCompatibleModelClient("http://unit.test/v1", "k", "m", http) { MaxAttempts = 2 };

        var error = await CaptureAsync(() => client.GenerateAsync(Request()));
        Check("injected HttpClient timeout => Transport ModelClientException",
            error is ModelClientException { Kind: ModelClientErrorKind.Transport }, TypeOf(error));
        Check("injected HttpClient timeout retried", handler.Calls == 2, $"calls={handler.Calls}");
    }

    // 10. 正常完成的响应不应被超时误伤。
    private static async Task CompletedResponsesWithinTimeoutAsync()
    {
        var handler = new CountingHandler((_, _) =>
            Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}")));
        var client = Client(handler, TimeSpan.FromSeconds(2));
        var response = await client.GenerateAsync(Request());
        Check("completed non-streaming body parsed", response.Text == "ok", response.Text ?? "<null>");
        Check("completed non-streaming single attempt", handler.Calls == 1, $"calls={handler.Calls}");

        const string frames =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"OK\"}}]}\n\n" +
            "data: [DONE]\n\n";
        var sseHandler = new CountingHandler((_, _) =>
            Task.FromResult(JsonResponse(frames, "text/event-stream")));
        var sseClient = Client(sseHandler, TimeSpan.FromSeconds(2));
        var completion = await sseClient.StreamAsync(Request());
        Check("completed SSE parsed", completion.Primary?.Text == "OK", completion.Primary?.Text ?? "<null>");
        Check("completed SSE single attempt", sseHandler.Calls == 1, $"calls={sseHandler.Calls}");
    }

    private static OpenAiCompatibleModelClient Client(HttpMessageHandler handler, TimeSpan timeout) =>
        new("http://unit.test/v1", "k", "m", new HttpClient(handler), timeout);

    private static PureModelRequest Request() => new(
        new[] { new PureAgentMessage(PureAgentRole.User, "hi") },
        Array.Empty<string>(),
        "m");

    private static HttpResponseMessage StallResponse(string prefix, HttpStatusCode status,
        string mediaType = "application/json", bool stream = false) =>
        new(status) { Content = new StallingContent(prefix, mediaType, stream) };

    private static HttpResponseMessage JsonResponse(string body, string mediaType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static string TypeOf(Exception? error) => error?.GetType().Name ?? "<no exception>";

    private static void Check(string name, bool condition, string? detail = null) =>
        _checks.Check(condition, detail is null ? name : $"{name} [{detail}]");

    private sealed class CountingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return responder(request, cancellationToken);
        }
    }

    /// <summary>先写出前缀，随后无限等待；取消时抛出 OperationCanceledException。</summary>
    private sealed class StallingContent : HttpContent
    {
        private readonly byte[] _prefix;

        public StallingContent(string prefix, string mediaType, bool stream)
        {
            _prefix = Encoding.UTF8.GetBytes(prefix);
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            StreamAsIs = stream;
        }

        private bool StreamAsIs { get; }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await SerializeToStreamAsync(stream, context, CancellationToken.None).ConfigureAwait(false);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(_prefix, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(CreateStream());

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(CreateStream());

        private Stream CreateStream() =>
            StreamAsIs
                ? new StallingReadStream(_prefix)
                : new MemoryStream(_prefix, writable: false);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingReadStream(byte[] prefix) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
