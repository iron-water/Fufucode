using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Fufucode.Core.Backend;
using static Fufucode.NativeCore.Tests.Case18SubagentEvidenceTests;

namespace Fufucode.NativeCore.Tests;

// Explicit opt-in only. Real router, isolated synthetic files, exact allowlist for shell commands.
internal static class SubagentHandoffLiveProbe
{
    public static async Task<int> RunAsync(string model)
    {
        if (!Uri.TryCreate(Environment.GetEnvironmentVariable("FUMO_BENCH_URL"), UriKind.Absolute, out var url) || !url.IsLoopback)
            throw new ArgumentException("Set FUMO_BENCH_URL to the intended loopback router.");
        using var client = new OpenAiCompatibleModelClient(url.AbsoluteUri, Environment.GetEnvironmentVariable("FUMO_BENCH_TOKEN"), model) { MaxAttempts = 1 };
        if (Environment.GetEnvironmentVariable("FUMO_BENCH_ACTOR") is { Length: > 0 } actor) client.AdditionalHeaders["x-openai-actor-authorization"] = actor;
        var sandbox = TestTemp.Create("live-handoff");
        var original = Environment.CurrentDirectory;
        var patches = new List<string>();
        var timer = Stopwatch.StartNew();
        try
        {
            var repo = Directory.CreateDirectory(Path.Combine(sandbox, "fixture")).FullName;
            Environment.CurrentDirectory = repo;
            FileChangeJournal.Configure(Path.Combine(sandbox, "data"));
            var expected = "sample-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            var nonce = "observation-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            Git(repo, "init", "-q"); Git(repo, "config", "core.autocrlf", "false");
            File.WriteAllText("answer.txt", "unfixed\n"); File.WriteAllText("expected.txt", expected + "\n");
            File.WriteAllText("observation.txt", nonce + "\n");
            Git(repo, "add", "."); Commit("original"); var head = Git(repo, "rev-parse", "HEAD").Trim();
            var definitions = Directory.CreateDirectory(Path.Combine(sandbox, "definitions", ".fumo", "agents")).FullName;
            Agent("author", "patch", "edit_file, run_command"); Agent("reviewer", "read", "read_file"); Agent("verifier", "patch", "run_command");
            var builtin = new ProductionToolDispatcher(repo);
            var allowedCommands = new HashSet<string>(StringComparer.Ordinal) { "git show HEAD:observation.txt" };
            var calls = new List<string>();
            var measured = new Meter(client);
            var runner = new SubagentRunner(AgentRegistry.Discover(Path.Combine(sandbox, "definitions"), sandbox), measured, builtin, model, repo)
            { ApprovalGate = (call, _) => Task.FromResult(Authorized(call)), Progress = p => { if (p.Kind == "tool_start") calls.Add(p.Agent + ":" + p.Detail); } };
            var parent = new SubagentDispatcher(builtin, runner);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var author = await Call(parent, new { agent = "author", prompt = $"Edit the relative file answer.txt: replace unfixed with {expected} and preserve its existing newline. Also execute exactly `git show HEAD:observation.txt` to capture the test sample ID. Do not include its value in your final report; just say what was changed." }, timeout.Token);
            var id = JobId(author); var path = Field(author.Content, "patchPath");
            if (!author.Success || id.Length == 0 || !OwnPatch(path)) return Finish("author", false);
            patches.Add(path);
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            var authorLog = await Read(parent, id, "commands");
            var digestOk = hash == Field(author.Content, "patchSha256"); var baseOk = Field(author.Content, "baseHead") == head;
            var captured = authorLog.Contains(nonce); var copied = author.Content.Contains(nonce);
            if (!digestOk || !baseOk || !captured || copied)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { digestOk, baseOk, captured, copied, denied = authorLog.Contains("拒绝"), exit0 = authorLog.Contains("ExitCode: 0") }));
                return Finish("author-evidence", false);
            }
            Console.WriteLine("LIVE author: actual candidate, pinned baseline and captured sample verified");
            File.WriteAllText("expected.txt", "a different oracle\n"); File.WriteAllText("observation.txt", "replaced observation\n");
            Git(repo, "add", "expected.txt", "observation.txt"); Commit("parent advanced"); var moved = Git(repo, "rev-parse", "HEAD").Trim();
            var review = await Call(parent, new { agent = "reviewer", contextRefs = new[] { id }, prompt = "Use subagent read_evidence twice on the handed job: inspect its patch to identify what replaced unfixed, and its commands to recover the original observation ID. Return both exact values. Do not inspect today's workspace; it has advanced." }, timeout.Token);
            if (!review.Success || calls.Count(c => c == "reviewer:subagent") < 2 || !review.Content.Contains(expected) || !review.Content.Contains(nonce)) return Finish("reviewer", false);
            Console.WriteLine("LIVE reviewer: patch and withheld sample recovered by reference");
            const string compare = "git diff --no-index --exit-code -- expected.txt answer.txt";
            var apply = $"git apply --check \"{path}\" && git apply \"{path}\"";
            allowedCommands.Clear(); allowedCommands.UnionWith([compare, "git rev-parse HEAD", apply]);
            var verified = await Call(parent, new { agent = "verifier", baseRef = id, prompt = $$"""
                Rerun the actual fixture in your working copy. First use run_command with exactly `git rev-parse HEAD`, then `{{compare}}` (exit 1 BEFORE the fix is expected). Using the patchPath in the handed card execute `git apply --check "<patchPath>" && git apply "<patchPath>"` with the actual path substituted. Then rerun the exact same comparison (exit 0 required). Do not change or read the parent working tree. Report the before and after. Only the listed commands are permitted.
                """ }, timeout.Token);
            var replayPath = Field(verified.Content, "patchPath"); if (OwnPatch(replayPath)) patches.Add(replayPath);
            if (!verified.Success || JobId(verified).Length == 0) return Finish("verifier", false);
            var commands = await Read(parent, JobId(verified), "commands");
            var before = commands.IndexOf("ExitCode: 1", StringComparison.Ordinal);
            var after = commands.LastIndexOf("ExitCode: 0", StringComparison.Ordinal);
            return Finish("end-to-end", before >= 0 && after > before && commands.Split(compare).Length == 3 &&
                commands.Contains(head) && Field(verified.Content, "patchSha256") == hash &&
                Git(repo, "rev-parse", "HEAD").Trim() == moved && File.ReadAllText("answer.txt") == "unfixed\n");

            bool Authorized(PureToolCall tool)
            {
                using var d = JsonDocument.Parse(tool.ArgumentsJson); var args = d.RootElement;
                return tool.Name == "edit_file" && args.TryGetProperty("path", out var p) && p.GetString() == "answer.txt" ||
                    tool.Name == "run_command" && (!args.TryGetProperty("cwd", out var cwd) || cwd.ValueKind == JsonValueKind.String && cwd.GetString() is "" or ".") &&
                    args.TryGetProperty("command", out var c) && allowedCommands.Contains(c.GetString() ?? "");
            }
            void Agent(string name, string mode, string tools) => File.WriteAllText(Path.Combine(definitions, name + ".md"),
                $"---\nname: {name}\nmode: {mode}\ntools: {tools}\neffort: low\nmaxTurns: 9\n---\nFollow the assigned fixture task; only use declared tools.");
            void Commit(string message) => Git(repo, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", message);
            int Finish(string stage, bool passed)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { model, stage, passed, ms = timer.ElapsedMilliseconds, measured.Requests,
                    input = measured.UsageComplete ? measured.Input : (long?)null, output = measured.UsageComplete ? measured.Output : (long?)null, tools = calls }));
                return passed ? 0 : 1;
            }
        }
        finally
        {
            Environment.CurrentDirectory = original;
            foreach (var path in patches) File.Delete(path);
            TestTemp.Delete(sandbox);
        }
    }

    private static bool OwnPatch(string path) => Path.GetDirectoryName(path) == Path.GetDirectoryName(Path.Combine(Path.GetTempPath(), "x")) &&
        Path.GetFileName(path).StartsWith("fufucode-subagent-patch-", StringComparison.Ordinal) && File.Exists(path);

    private sealed class Meter(IPureModelClient inner) : IPureModelClient
    {
        public int Requests; public long Input, Output; public bool UsageComplete = true;
        public async Task<PureModelResponse> GenerateAsync(PureModelRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            try
            {
                var response = await inner.GenerateAsync(request, cancellationToken);
                if (response.Usage is { InputTokens: { } input, OutputTokens: { } output }) { Input += input; Output += output; }
                else UsageComplete = false;
                return response;
            }
            catch (Exception e) { UsageComplete = false; Console.Error.WriteLine("Router request failed: " + e.GetType().Name); throw; }
        }
    }
}
