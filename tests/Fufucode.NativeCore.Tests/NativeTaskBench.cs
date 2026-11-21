using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fufucode.App.Abstractions;
using Fufucode.Core.Backend;

namespace Fufucode.NativeCore.Tests;

// A/B runner. One sample runs three independent tasks with fresh fixtures and no shared history.
// Only aggregate metrics leave the runner; never log headers, prompts, reasoning or tool payloads.
internal static class NativeTaskBench
{
    public static async Task<int> RunLiveAsync(string model, int sample)
    {
        var url = Environment.GetEnvironmentVariable("FUMO_BENCH_URL");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
            throw new ArgumentException("Set FUMO_BENCH_URL to the intended loopback router.");
        using var client = new OpenAiCompatibleModelClient(url!, Environment.GetEnvironmentVariable("FUMO_BENCH_TOKEN"), model)
            { MaxAttempts = 1 };
        if (Environment.GetEnvironmentVariable("FUMO_BENCH_ACTOR") is { Length: > 0 } actor)
            client.AdditionalHeaders["x-openai-actor-authorization"] = actor;
        var passed = 0;
        var originalDirectory = Environment.CurrentDirectory;
        string[] tasks = ["lookup", "context", "edit"];
        for (var i = 0; i < tasks.Length; i++)
        {
            var task = tasks[(sample % tasks.Length + i) % tasks.Length];
            var sandbox = TestTemp.Create("task-bench");
            try
            {
                var root = Directory.CreateDirectory(Path.Combine(sandbox, "workspace")).FullName;
                var data = Directory.CreateDirectory(Path.Combine(sandbox, "data")).FullName;
                FileChangeJournal.Configure(data);
                // The production system prompt sees the same actual working directory (.) on both arms.
                Environment.CurrentDirectory = root;
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{task}/{sample}")))[..12];
                var (prompt, verify) = MakeTask(root, task, id);
                string[] tools = task == "edit" ? ["grep_search", "glob_files", "list_dir", "read_file", "edit_file", "write_file"]
                    : ["grep_search", "glob_files", "list_dir", "read_file"];
                var engine = new PureCSharpAgentEngine(new FixtureTools(root), client, maxIterations: 5);
                await using var session = new NativeCSharpChatSession(engine, modelId: model, workspaceRoot: ".",
                    persistFilePath: Path.Combine(data, "session.json")) { HostToolAllowed = tools.Contains };
                session.SetThinkingLevel("low");
                var settled = new TaskCompletionSource<SessionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
                var failedTools = new HashSet<string>(StringComparer.Ordinal);
                session.Event += (_, item) =>
                {
                    if (item is SessionEvent.TurnSettled end) settled.TrySetResult(end.Outcome);
                    if (item is SessionEvent.ToolCallStatusChanged { Status: ToolCallStatus.Failed } failed) failedTools.Add(failed.ToolCallId);
                };
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var timer = Stopwatch.StartNew();
                SessionOutcome? outcome = null;
                try
                {
                    await session.SendAsync(prompt, timeout.Token);
                    outcome = await settled.Task.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { await session.StopAsync(); }
                timer.Stop();
                var history = engine.GetState().History ?? [];
                var assistant = history.Where(m => m.Role == PureAgentRole.Assistant).ToArray();
                var verified = verify(assistant.LastOrDefault()?.Content ?? "");
                var ok = outcome == SessionOutcome.Completed && verified;
                if (ok) passed++;
                // Truncation adds a synthetic assistant notice, not a billable model response.
                var rounds = outcome == SessionOutcome.Truncated ? assistant[..^1] : assistant;
                long? Sum(Func<PureUsage, long?> field) => (outcome is SessionOutcome.Completed or SessionOutcome.Truncated) &&
                    rounds.Length > 0 && rounds.All(m => m.Usage is { } u && field(u).HasValue) ? rounds.Sum(m => field(m.Usage!)) : null;
                var trace = rounds.SelectMany((r, i) => (r.ToolCalls ?? []).Select(c =>
                    $"{i + 1}:{(tools.Contains(c.Name) ? c.Name : "undeclared")}{(failedTools.Contains(c.Id) ? "!" : "")}")).ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new { task, sample, model, passed = ok, verified,
                    status = outcome?.ToString() ?? "Timeout", ms = timer.ElapsedMilliseconds,
                    calls = rounds.Length, tools = trace.Length, toolErrors = failedTools.Count,
                    input = Sum(u => u.InputTokens), output = Sum(u => u.OutputTokens), cached = Sum(u => u.CacheReadTokens), trace }));
            }
            finally { Environment.CurrentDirectory = originalDirectory; TestTemp.Delete(sandbox); }
        }
        return passed == tasks.Length ? 0 : 1;
    }

    private static (string Prompt, Func<string, bool> Verify) MakeTask(string root, string task, string id)
    {
        if (task == "lookup")
        {
            File.WriteAllText(Path.Combine(root, "trace.log"), new string('p', 62_000) + $" release_tag={id} " + new string('q', 38_000));
            return ("在 trace.log 中找到 release_tag= 后的值（截至空格）。只回复这个值，不要猜测。", text => text.Trim() == id);
        }
        if (task == "context")
        {
            File.WriteAllText(Path.Combine(root, "service.log"), $"retry_code=ignore\nJOB_FAILED\nretry_code={id[..6]}\nJOB_FAILED\nretry_code={id[6..]}\nOK\n");
            return ("找出 service.log 中每个 JOB_FAILED 下一行的 retry_code 值。按出现顺序用英文逗号连接，只回复结果。",
                text => text.Trim() == id[..6] + "," + id[6..]);
        }
        var settings = Path.Combine(root, "settings.ini");
        const string original = "[stable]\nversion=1\n[preview]\nversion=1\nkeep=true\n";
        File.WriteAllText(settings, original);
        File.WriteAllText(Path.Combine(root, "release.txt"), id);
        return ("把 settings.ini 的 [preview] 段 version 改成 release.txt 的内容，其余内容保持原样，简短报告。",
            _ => File.ReadAllText(settings) == original.Replace("[preview]\nversion=1", "[preview]\nversion=" + id, StringComparison.Ordinal));
    }

    // FUMO's product read tools can consult files outside the project. Live probes must not:
    // allow only root listings and the generated flat fixture names (including read_file line selectors).
    private sealed class FixtureTools(string root) : IPureToolDispatcher
    {
        private readonly ProductionToolDispatcher _inner = new(root);
        private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly HashSet<string> _files = Directory.GetFiles(root).ToHashSet(PathComparer);

        public Task<PureToolResult> DispatchAsync(PureToolCall call, CancellationToken cancellationToken = default)
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson);
            var path = doc.RootElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : ".";
            var colon = path.LastIndexOf(':');
            if (call.Name == "read_file" && colon > 0 && Regex.IsMatch(path[(colon + 1)..], @"\A-?[0-9]+(?:[-+][0-9]+)?(?:,-?[0-9]+(?:[-+][0-9]+)?)*\z"))
                path = path[..colon];
            var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, path)));
            if ((call.Name is "grep_search" or "glob_files" or "list_dir" && PathComparer.Equals(resolved, root)) || _files.Contains(resolved))
                return _inner.DispatchAsync(call, cancellationToken);
            return Task.FromResult(new PureToolResult(call.Id, false, "Only synthetic fixture paths are available."));
        }
    }

    public static async Task<int> MeasureGrepAsync()
    {
        var root = TestTemp.Create("grep-bench");
        try
        {
            using (var file = File.CreateText(Path.Combine(root, "early.log")))
            {
                file.WriteLine("context\nmarker=early\ncontext");
                for (var i = 0; i < 90_000; i++) file.WriteLine("irrelevant later line in the same build artifact");
            }
            var dispatcher = new ProductionToolDispatcher(root);
            var call = new PureToolCall("bench", "grep_search", """{"path":"early.log","pattern":"marker=early","context_lines":1,"max_matches":1}""");
            var times = new List<double>(); var sizes = new List<long>();
            for (var i = 0; i < 13; i++)
            {
                var before = GC.GetTotalAllocatedBytes(true);
                var start = Stopwatch.GetTimestamp();
                var result = await dispatcher.DispatchAsync(call);
                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                var allocated = GC.GetTotalAllocatedBytes(true) - before;
                if (!result.Success || !result.Content.Contains("marker=early")) return 1;
                if (i >= 2) { times.Add(ms); sizes.Add(allocated); }
            }
            Console.WriteLine(JsonSerializer.Serialize(new { task = "grep-early-context", samples = times.Count,
                medianMs = times.Order().ElementAt(times.Count / 2), medianAllocatedBytes = sizes.Order().ElementAt(sizes.Count / 2) }));
            return 0;
        }
        finally { TestTemp.Delete(root); }
    }
}
