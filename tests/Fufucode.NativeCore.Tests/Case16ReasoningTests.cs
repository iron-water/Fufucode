using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

internal static class Case16ReasoningTests
{
    public static async Task RunAsync(Checks checks)
    {
        StreamAggregation(checks);
        RequestHistory(checks);
        await ToolContinuationAsync(checks);
        await NonStreamingAsync(checks);
        await FallbackAsync(checks);
        await PersistenceAsync(checks);
        LegacyHistory(checks);
        await ThinkingOnlyEofAsync(checks);
    }

    private static string Frame(object delta, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } }) + "\n\n";

    private static HttpResponseMessage Sse(string frames) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(frames, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Json(string reasoning, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = "answer", reasoning_content = reasoning }, finish_reason = finish } },
        }), Encoding.UTF8, "application/json"),
    };

    private static PureModelRequest Request(IReadOnlyList<PureAgentMessage>? messages = null, string model = "auto/deepseek-v4.1-flash") =>
        new(messages ?? [new(PureAgentRole.User, "fixture")], ["read_file"], model);

    private static void StreamAggregation(Checks checks)
    {
        var parser = new ChatCompletionSseParser();
        var deltas = new List<string>();
        foreach (var text in new[] { "fixture-", "reasoning" })
            parser.TryApply(new ServerSentEvent(null, JsonSerializer.Serialize(new
            {
                choices = new[] { new { index = 0, delta = new { reasoning_content = text } } },
            }), null, null), new ModelStreamCallbacks { OnThinkingDelta = deltas.Add });
        var response = parser.Completion.ToResponse();
        checks.Equal("fixture-reasoning", response.ReasoningContent,
            "stream aggregation retains reasoning in the response passed to the agent loop");
        checks.Equal("fixture-reasoning", string.Concat(deltas), "thinking callbacks contain exact deltas once");
        checks.Check(response.Text is null && response.ToolCalls.Count == 0,
            "reasoning stays separate from answer and executable tool calls");
    }

    private static void RequestHistory(Checks checks)
    {
        var history = new PureAgentMessage[]
        {
            new(PureAgentRole.User, "fixture"),
            new(PureAgentRole.Assistant, "prior answer") { ReasoningContent = "prior reasoning" },
            new(PureAgentRole.Assistant, "inspect", ToolCalls: [new("c1", "read_file", "{}")])
                { ReasoningContent = "tool reasoning" },
            new(PureAgentRole.Tool, "payload", ToolCallId: "c1"),
        };
        var json = JsonSerializer.SerializeToElement(OpenAiCompatibleModelClient.BuildRequestBody(Request(history), "auto/deepseek-v4.1-flash", true));
        var messages = json.GetProperty("messages");
        checks.Equal("prior reasoning", messages[1].GetProperty("reasoning_content").GetString(),
            "DeepSeek replays reasoning from assistant turns without tools");
        checks.Equal("tool reasoning", messages[2].GetProperty("reasoning_content").GetString(),
            "DeepSeek replays exact tool-turn reasoning");
        checks.Equal("c1", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString(), "assistant tool ID survives serialization");
        checks.Equal("c1", messages[3].GetProperty("tool_call_id").GetString(), "tool result remains paired");
        checks.Check(!messages[0].TryGetProperty("reasoning_content", out _) && !messages[3].TryGetProperty("reasoning_content", out _),
            "user and tool results do not acquire reasoning fields");
        checks.Equal((long)"inspecttool reasoning{}".Length, NativeHistoryGroups.SizeOf(history[2]),
            "history budget includes reasoning characters");
    }

    private static async Task ToolContinuationAsync(Checks checks)
    {
        var requests = new List<JsonElement>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync()));
            return requests.Count == 1
                ? Sse(Frame(new { reasoning_content = "inspect fixture" }) +
                      Frame(new { tool_calls = new[] { new { index = 0, id = "c1", function = new { name = "read_file", arguments = "{}" } } } }, "tool_calls") + "data: [DONE]\n\n")
                : Sse(Frame(new { reasoning_content = "finish fixture" }) + Frame(new { content = "done" }, "stop") + "data: [DONE]\n\n");
        }));
        var client = new OpenAiCompatibleModelClient("https://fixture.invalid/v1", httpClient: http);
        var tools = new Tools();
        var history = new List<PureAgentMessage> { new(PureAgentRole.User, "fixture") };
        var loop = new NativeAgentLoop(client, tools, maxIterations: 3);
        var result = await loop.ExecuteLoopAsync(history, ["read_file"], "auto/deepseek-v4.1-flash");
        checks.Equal(PureAgentTurnStatus.Completed, result.Status, "real HTTP client and loop finish after tool result");
        checks.Equal(1, tools.Calls, "only parsed tool_calls execute");
        checks.Equal("inspect fixture", history[1].ReasoningContent, "tool assistant history retains reasoning");
        checks.Equal("inspect fixture", requests[1].GetProperty("messages")[1].GetProperty("reasoning_content").GetString(),
            "next real HTTP request receives prior tool reasoning");
        checks.Equal("finish fixture", history[^1].ReasoningContent, "final assistant history retains reasoning for later tool requests");
        checks.Equal("finish fixture", result.Response.ReasoningContent, "turn response retains final reasoning");
        checks.Equal("done", result.Response.Text, "final answer contains no reasoning text");
    }

    private static async Task NonStreamingAsync(Checks checks)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json("nonstream reasoning", "length"))));
        var client = new OpenAiCompatibleModelClient("https://fixture.invalid/v1", httpClient: http);
        var response = await client.GenerateAsync(Request());
        checks.Equal("nonstream reasoning", response.ReasoningContent, "non-streaming response retains reasoning");
        checks.Equal("length", response.FinishReason, "non-streaming preserves upstream finish reason");
        checks.Equal("answer", response.Text, "non-streaming keeps reasoning separate from answer");
    }

    private static async Task FallbackAsync(Checks checks)
    {
        var attempts = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++attempts == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                { Content = new StringContent("unavailable") });
            return Task.FromResult(Json("fallback reasoning", "length"));
        }));
        var client = new OpenAiCompatibleModelClient("https://fixture.invalid/v1", httpClient: http) { MaxAttempts = 1 };
        var thinking = new List<string>();
        var response = await client.GenerateStreamingAsync(Request(), new ModelStreamCallbacks { OnThinkingDelta = thinking.Add });
        checks.Equal(2, attempts, "failed SSE falls back once to non-streaming");
        checks.Equal("fallback reasoning", response.ReasoningContent, "fallback response preserves reasoning");
        checks.Equal("fallback reasoning", thinking.Single(), "fallback thinking reaches UI once");
        checks.Equal("length", response.FinishReason, "fallback does not invent stop reason");
    }

    private static async Task PersistenceAsync(Checks checks)
    {
        var temp = TestTemp.Create("reasoning");
        try
        {
            var file = Path.Combine(temp, "session.json");
            using var http = new HttpClient(new Handler(_ => Task.FromResult(Sse(
                Frame(new { reasoning_content = "saved reasoning" }) + Frame(new { content = "saved answer" }, "stop") + "data: [DONE]\n\n"))));
            var engine = new PureCSharpAgentEngine(modelClient: new OpenAiCompatibleModelClient("https://fixture.invalid/v1", httpClient: http));
            await using var session = new NativeCSharpChatSession(engine, modelId: "auto/deepseek-v4.1-flash", persistFilePath: file);
            var events = new ConcurrentQueue<SessionEvent>();
            var settled = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Event += (_, item) =>
            {
                events.Enqueue(item);
                if (item is SessionEvent.TurnSettled end) settled.TrySetResult(end.Outcome);
            };
            await session.SendAsync("fixture");
            checks.Equal(SessionOutcome.Completed, await settled.Task.WaitAsync(TimeSpan.FromSeconds(10)), "real session persists completed turn");
            await session.StopAsync();
            checks.Equal("saved reasoning", events.OfType<SessionEvent.MessageFinished>()
                .Single(e => e.FinalMessage.Role == MessageRole.Assistant).FinalMessage.Blocks.OfType<ThinkingBlock>().Single().Text,
                "streamed thinking appears once in final display block");
            using var stream = File.OpenRead(file);
            var saved = PersistentSessionManager.ParseNativeMessages(stream)!;
            var assistant = saved.Messages.Single(m => m.Role == PureAgentRole.Assistant);
            checks.Equal("saved reasoning", assistant.ReasoningContent, "session disk round trip preserves reasoning");
            var entries = InternalAccess.NativeEntriesBuild(saved.Messages, stableIds: true);
            var blocks = entries[^1].GetProperty("message").GetProperty("content");
            checks.Equal("thinking", blocks[0].GetProperty("type").GetString(), "restored timeline has thinking block");
            checks.Equal("saved reasoning", blocks[0].GetProperty("thinking").GetString(), "restored timeline replays original reasoning");
            checks.Equal("saved answer", blocks[1].GetProperty("text").GetString(), "restored answer stays separate");
            var replay = JsonSerializer.SerializeToElement(OpenAiCompatibleModelClient.BuildRequestBody(Request(saved.Messages), "auto/deepseek-v4.1-flash", true));
            var replayMessages = replay.GetProperty("messages");
            checks.Equal("saved reasoning", replayMessages[replayMessages.GetArrayLength() - 1].GetProperty("reasoning_content").GetString(),
                "restored session can replay reasoning to provider");
        }
        finally { TestTemp.Delete(temp); }
    }

    private static void LegacyHistory(Checks checks)
    {
        var legacy = JsonSerializer.Deserialize<PureAgentMessage>("{\"Role\":2,\"Content\":\"old answer\"}")!;
        checks.Check(legacy.ReasoningContent is null, "old session JSON remains readable");
        var request = Request([legacy]);
        var deepseek = JsonSerializer.SerializeToElement(OpenAiCompatibleModelClient.BuildRequestBody(request, "auto/deepseek-v4.1-flash", true));
        checks.Equal("", deepseek.GetProperty("messages")[0].GetProperty("reasoning_content").GetString(), "old DeepSeek history gets empty reasoning field without invented content");
        var generic = JsonSerializer.SerializeToElement(OpenAiCompatibleModelClient.BuildRequestBody(request, "generic-model", true));
        checks.Check(!generic.GetProperty("messages")[0].TryGetProperty("reasoning_content", out _), "generic old history gains no provider-specific field");
    }

    private static async Task ThinkingOnlyEofAsync(Checks checks)
    {
        var attempts = 0;
        using var http = new HttpClient(new Handler(_ => Task.FromResult(++attempts == 1
            ? Sse(Frame(new { reasoning_content = "unfinished fixture" }))
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"error\":{\"message\":\"fallback unavailable\"}}") })));
        var client = new OpenAiCompatibleModelClient("https://fixture.invalid/v1", httpClient: http) { MaxAttempts = 1 };
        var history = new List<PureAgentMessage> { new(PureAgentRole.User, "fixture") };
        var result = await new NativeAgentLoop(client).ExecuteLoopAsync(history, [], "fixture");
        checks.Equal(PureAgentTurnStatus.Failed, result.Status,
            "thinking-only EOF without a finish signal cannot complete a task");
        checks.Equal(2, attempts, "incomplete reasoning stream attempts the existing fallback");
        checks.Check(!history.Any(m => m.Role == PureAgentRole.Assistant),
            "incomplete reasoning does not append a completed empty assistant message");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }

    private sealed class Tools : IPureToolDispatcher
    {
        public int Calls { get; private set; }
        public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PureToolResult(call.Id, true, "fixture payload"));
        }
    }
}
