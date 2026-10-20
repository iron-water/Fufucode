using System.Net.Http;
using System.Text;
using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;
using Fufucode.Core.Diagnostics;
using Fufucode.Plugins.PerformanceMonitor;

namespace Fufucode.PerformanceMonitor.Tests;

/// <summary>
/// 端到端：真实 FUMO 会话（NativeCSharpChatSession）+ 脚本化流式模型 + 真实工具调度器。
/// 确认回合根 span、模型请求、工具、命令与持久化都挂在同一 trace 下，并落进插件接收端。
/// </summary>
internal static class NativePipelineTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "fufucode-perf-tests", Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            var recorder = new PerfRecorder(new PerfSettings { RetentionDays = 0 }, Path.Combine(root, "perf"));
            PerfTracer.Attach(recorder, recorder);
            try
            {
                var model = new ScriptedStreamingModel();
                var dispatcher = new ProductionToolDispatcher(workspace);
                var engine = new PureCSharpAgentEngine(dispatcher, model, maxIterations: 5);
                var persistPath = Path.Combine(root, "session.json");
                using var session = new NativeCSharpChatSession(engine, "session-1", "test-model",
                    persistFilePath: persistPath);
                session.SetExecutionModeAsync("full").GetAwaiter().GetResult();
                var settled = new TaskCompletionSource<SessionOutcome>();
                session.Event += (_, e) =>
                {
                    if (e is SessionEvent.TurnSettled turn) settled.TrySetResult(turn.Outcome);
                };
                session.SendAsync("把 hello 写入 out.txt").GetAwaiter().GetResult();
                var outcome = settled.Task.Wait(TimeSpan.FromSeconds(30)) ? settled.Task.Result : SessionOutcome.Unknown;
                TestUtil.Check(outcome == SessionOutcome.Completed, "session turn completed");
            }
            finally
            {
                PerfTracer.Detach(recorder);
            }

            var spans = recorder.Ring.Snapshot();
            TestUtil.Check(spans.Count(s => s.Name == "engine.turn") == 1, "engine turn recorded exactly once");
            var turn = spans.SingleOrDefault(s => s.Name == "turn");
            TestUtil.Check(turn is not null, "turn span recorded");
            TestUtil.Check(turn!.Tag("backend") == "fumo" && turn.Tag("executionMode") == "full",
                "turn span carries backend and mode");
            TestUtil.Check(turn.Tag("outcome") == "Completed", "turn outcome recorded");
            var children = spans.Where(s => s.ParentSpanId == turn.SpanId).Select(s => s.Name).ToArray();
            TestUtil.Check(children.Contains("context.build"), "context build hangs under the turn");
            TestUtil.Check(children.Contains("engine.turn"), "engine turn hangs under the chat turn");

            var requests = spans.Where(s => s.Name == "model.request").ToArray();
            TestUtil.Check(requests.Length == 2, "two model requests recorded");
            var streams = spans.Where(s => s.Name == "model.stream").ToArray();
            TestUtil.Check(streams.Length == 2, "both model streams recorded");
            TestUtil.Check(streams.All(s => s.LongTag("ttftMs") is not null && s.LongTag("frames") > 0),
                "stream span records ttft and frame count");
            var firstStream = streams.OrderBy(s => s.EndTicks).First();
            TestUtil.Check(firstStream.LongTag("frames") >= 5, "first stream counted all frames");
            TestUtil.Check(streams.Any(s => s.LongTag("promptTokens") == 100), "usage tokens recorded on the stream span");
            // model.stream 在 OpenAiCompatibleModelClient.StreamAsync 内开启，此时外层 model.request 尚未结束，
            // 因此父 span 是某个 model.request 或 agent.iteration。
            // 每个 model.request 恰好包含一条 model.http，且两条 stream 的父链都能追到某个 model.request。
            var http = spans.Where(s => s.Name == "model.http").ToArray();
            TestUtil.Check(http.Length == 2 && requests.All(r => http.Any(h => h.ParentSpanId == r.SpanId)),
                "each model request contains one http span");
            TestUtil.Check(streams.All(s => s.ParentSpanId == http[0].SpanId || s.ParentSpanId == http[1].SpanId),
                "stream span hangs under its http span");

            var toolSpan = spans.SingleOrDefault(s => s.Name == "tool" && s.Tag("tool") == "write_file");
            TestUtil.Check(toolSpan is not null && toolSpan.Tag("ok") == "true", "tool span records write_file success");
            var process = spans.SingleOrDefault(s => s.Name == "tool.process");
            TestUtil.Check(process is not null, "run_command span recorded");
            TestUtil.Check(process!.Tag("command") == "echo done", "command text recorded");
            TestUtil.Check(process.LongTag("exitCode") == 0, "command exit code recorded");
            TestUtil.Check(process.ParentSpanId == spans.Single(s => s.Name == "tool" && s.Tag("tool") == "run_command").SpanId,
                "process span hangs under the run_command tool span");

            var persist = spans.SingleOrDefault(s => s.Name == "persist.history");
            TestUtil.Check(persist is not null && persist.LongTag("messages") > 0, "persist span records message count");
            TestUtil.Check(File.Exists(Path.Combine(root, "session.json")), "session persisted for real");
            TestUtil.Check(spans.Where(s => s.Name != "turn").All(s => s.TraceId == turn.TraceId),
                "every span shares the turn trace");
            TestUtil.Check(File.Exists(Path.Combine(workspace, "out.txt")), "tool actually wrote the file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 第一轮返回两个工具调用（SSE 分片），第二轮返回终态文本。
    /// 继承真实客户端但只替换 HTTP 发送：模型 span（TTFB/TTFT/帧间隔/usage）在真实代码路径上产生。
    /// </summary>
    private sealed class ScriptedStreamingModel : OpenAiCompatibleModelClient
    {
        private int _calls;

        public ScriptedStreamingModel() : base("http://scripted.invalid/v1")
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _calls++;
            var body = new StringBuilder();
            if (_calls == 1)
            {
                Append(body, Chunk("好的，"));
                Append(body, Chunk("我来执行。"));
                Append(body, ToolChunk(0, "call-1", "write_file", "{\"path\":\"out.txt\",\"content\":\"hello\"}"));
                Append(body, ToolChunk(1, "call-2", "run_command", "{\"command\":\"echo done\"}"));
                Append(body, "{\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]," +
                             "\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"total_tokens\":120}}");
            }
            else
            {
                Append(body, Chunk("完成"));
            }
            body.Append("data: [DONE]\n\n");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream"),
            });
        }

        private static void Append(StringBuilder body, string json) => body.Append("data: ").Append(json).Append("\n\n");

        /// <summary>用匿名对象构造合法 JSON，避免手写转义。</summary>
        private static string Chunk(string text) => JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, delta = new { content = text } } },
        });

        private static string ToolChunk(int index, string id, string name, string argumentsJson) =>
            JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new { index, id, function = new { name, arguments = argumentsJson } },
                            },
                        },
                    },
                },
            });
    }
}
