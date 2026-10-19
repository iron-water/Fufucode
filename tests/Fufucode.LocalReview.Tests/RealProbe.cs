using System.Text;
using System.Text.Json;
using Fufucode.App.Abstractions.Automation;
using Fufucode.App.Integration;
using Fufucode.Core;
using Fufucode.Core.Automation;
using Fufucode.Core.Automation.Harnesses;
using Fufucode.Core.Features.LocalReview;
using Fufucode.Plugins.AutoDev;
using Fufucode.Plugins.AutoDev.Engine.Harnesses;

namespace Fufucode.LocalReview.Tests;

/// <summary>
/// Opt-in real model probe for the local review path, explicitly pinned to the free
/// <c>devin/swe-2</c> model on the runtime already verified in docs/records/harness-backend-2026-10-11.md.
/// It answers one question only: does the review module's transport, terminal-state check and strict
/// parser actually work against a real harness? A model answer is never treated as proof of review
/// quality, and "no findings" is never reported as "code is correct".
/// Usage: dotnet run --project tests/Fufucode.LocalReview.Tests -c Release -- --real-probe
/// Nothing here touches a user repository: the probe builds its own temporary git fixture.
/// </summary>
internal static class RealProbe
{
    private const string Model = "devin/swe-2";
    private const string Provider = "magpie";

    public static async Task<int> RunAsync()
    {
        string node, cli, version;
        try
        {
            (node, cli) = RealSessionBootstrap.ResolveRuntime(AppOptions.Mock with
            { PiNodePath = Environment.GetEnvironmentVariable("FUFUCODE_E1_NODE"),
                PiCliPath = Environment.GetEnvironmentVariable("FUFUCODE_E1_PI_CLI") });
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cli)!, "..", "..", "package.json"))));
            version = manifest.RootElement.GetProperty("version").GetString()!;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            Console.WriteLine("BLOCKED: " + error.Message);
            return 2;
        }
        var root = Path.Combine(Path.GetTempPath(), "fufucode-e1-realprobe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repo = Path.Combine(root, "probe repo 目录");
            Directory.CreateDirectory(repo);
            await Git.Run(repo, "init", "-q", ".");
            await Git.Run(repo, "config", "user.email", "probe@example.invalid");
            await Git.Run(repo, "config", "user.name", "e1-probe");
            await Git.Run(repo, "config", "core.autocrlf", "false");
            await File.WriteAllTextAsync(Path.Combine(repo, "Parser.cs"),
                "class Parser\n{\n    public string Read(string input)\n    {\n        return input.Substring(0);\n    }\n}\n",
                new UTF8Encoding(false));
            await Git.Run(repo, "add", "-A");
            await Git.Run(repo, "commit", "-q", "-m", "baseline");
            // 未提交改动 = 审查目标：一处明显的越界访问缺陷。
            await File.WriteAllTextAsync(Path.Combine(repo, "Parser.cs"),
                "class Parser\n{\n    public string Read(string input)\n    {\n        var head = input.Substring(0, 5);\n        return head;\n    }\n}\n",
                new UTF8Encoding(false));

            var scope = await LocalReviewScopeResolver.ResolveAsync(repo, LocalReviewScopeKind.Workspace);
            Console.WriteLine("scope: " + scope.Status + " files=" + scope.Files.Count +
                " capability=" + scope.SnapshotCapability);
            if (scope.Status != LocalReviewScopeStatus.Resolved) { Console.WriteLine("BLOCKED: scope not resolved"); return 2; }

            var agent = Environment.GetEnvironmentVariable("FUFUCODE_E1_AGENT_DIR");
            var runtime = new HarnessRuntime("local-pi", "pi", node, [cli, "--no-tools"], version,
                agent is null ? null : new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = agent });
            BatchHarnessAdapters.Register();
            ExecutionBaseLibrary.Register(new AutoDevExecutionOwner(_ => runtime));
            var executor = new LocalReviewHarnessExecutor(
                Path.Combine(root, "review-data"), "e1-probe-project", Model, Provider);
            Console.WriteLine($"dispatching: harness=pi model={Model} provider={Provider} runtime={version}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var started = DateTimeOffset.UtcNow;
            var result = await executor.ReviewAsync(scope, timeout.Token);
            var elapsed = DateTimeOffset.UtcNow - started;
            Console.WriteLine($"status={result.Status} complete={result.ReviewComplete} " +
                $"findings={result.Findings.Count} rejected={result.Rejected.Count} " +
                $"stale={result.IsStale} truncated={result.ResponseTruncated} " +
                $"runId={result.RunId} elapsed={elapsed.TotalSeconds:F1}s");
            if (result.Error is not null) Console.WriteLine("error: " + result.Error);
            var inScope = result.Findings.All(f =>
                scope.Files.Any(file => string.Equals(file.Path, f.Path, StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine("all accepted findings reference paths inside the requested scope: " + inScope);
            foreach (var finding in result.Findings)
                Console.WriteLine($"  - [{finding.Priority}] {finding.Location} {finding.Explanation}" +
                    (string.IsNullOrWhiteSpace(finding.Evidence) ? "" : " ｜证据：" + finding.Evidence.Trim()));
            foreach (var rejected in result.Rejected)
                Console.WriteLine($"  ! rejected {rejected.Reason} {rejected.Path}:{rejected.Line} {rejected.Detail}");

            // 判定的是机制，不是审查质量：终态 + 严格解析 + 范围一致性 + 只读。
            var headAfter = (await Git.Run(repo, "rev-parse", "HEAD")).Trim();
            var dirty = await Git.Run(repo, "status", "--porcelain");
            var mechanism = result.ReviewComplete && inScope && result.RunId is not null &&
                headAfter == scope.HeadCommit && dirty.Contains("Parser.cs");
            Console.WriteLine(result.ReviewComplete
                ? "transport/terminal-state/strict-parse: PASS (findings are model output, not a quality claim)"
                : "transport/terminal-state/strict-parse: FAIL");
            Console.WriteLine("read-only check: HEAD unchanged, user modification still present: " +
                (headAfter == scope.HeadCommit && dirty.Contains("Parser.cs")));
            Console.WriteLine("evidence: " + Path.Combine(root, "review-data"));
            Console.WriteLine("If the reviewer found nothing, that is reported as-is; it is not proof of correctness.");
            Environment.SetEnvironmentVariable("E1_PROBE_KEEP", root);
            Console.WriteLine("KEEP: " + root);
            return mechanism ? 0 : 1;
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("E1_KEEP_TMP") is null)
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception error) { Console.WriteLine("临时目录保留：" + error.GetType().Name); }
            }
        }
    }

    private static class Git
    {
        public static async Task<string> Run(string directory, params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git")
            {
                WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception($"git {string.Join(' ', args)} failed: {error}");
            return output;
        }
    }
}
