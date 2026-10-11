using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fufucode.Core;
using Fufucode.Pi;

/// <summary>
/// A13 item 2, evidence half: does Pi still die with 0xC0000409 (STATUS_STACK_BUFFER_OVERRUN,
/// Windows signed -1073740791) right after an approved write? D-6 saw it once on a shared desktop
/// and never reproduced it. This drives the real bridge + a synthetic model endpoint on the
/// installed runtime and records every process exit it observes. It never claims a crash it
/// did not see, and a clean run is reported as "not reproduced".
/// </summary>
internal static class CrashReproTests
{
    /// <summary>0xC0000409 (STATUS_STACK_BUFFER_OVERRUN) as .NET reports it on Windows.</summary>
    private const int CrashSignature = -1073740791;

    private sealed record Iteration(int Index, string Decision, bool DialogSeen, string Outcome,
        string? LastError, bool ProcessExited, int? ExitCode, bool CrashSignature, bool FileWritten,
        string? StderrTail);

    private sealed record Report(string Runtime, string? NodeVersion, string? PiVersion, int Iterations,
        IReadOnlyList<Iteration> Runs, int CrashCount, int ProcessExitCount);

    public static async Task RunAsync(string root, string node, string cli)
    {
        var scratch = Path.Combine(root, "tmp", "a13-crash-repro");
        Directory.CreateDirectory(scratch);
        var iterations = Math.Clamp(Environment.GetEnvironmentVariable("FUFUCODE_CRASH_REPRO_ITERATIONS") is { } raw &&
            int.TryParse(raw, out var parsed) ? parsed : 4, 1, 50);
        var bridge = Path.Combine(root, "extensions", "fufucode-bridge");
        var runs = new List<Iteration>();
        var index = 0;
        foreach (var decision in new bool?[] { true, false, null })
        {
            for (var repeat = 0; repeat < iterations; repeat++)
            {
                index++;
                var tag = decision is true ? "allow" : decision is false ? "deny" : "timeout";
                var work = Path.Combine(scratch, $"{tag}-{repeat}");
                var agent = Path.Combine(work, "agent");
                Directory.CreateDirectory(work);
                Directory.CreateDirectory(agent);
                var target = Path.Combine(work, "批准结果.txt");
                var script = Path.Combine(work, "stub-script.json");
                File.WriteAllText(script, JsonSerializer.Serialize(new object[]
                {
                    new { type = "tool", name = "write", args = new { path = target, content = "written after approval" } },
                    new { type = "text", text = "Tool turn finished." }
                }));
                string? lastError = null; var dialogSeen = false; var outcome = "unknown";
                await using (var stub = await BridgeIntegrationTests.StartStubAsync(root, node, script))
                {
                    File.WriteAllText(Path.Combine(agent, "models.json"), JsonSerializer.Serialize(new
                    {
                        providers = new Dictionary<string, object>
                        {
                            ["fff-stub"] = new { baseUrl = $"http://127.0.0.1:{stub.Port}", apiKey = "stub-key",
                                api = "openai-completions", models = new[] { new { id = "fff-stub-model", name = "FFF Stub Model" } } }
                        }
                    }));
                    var env = new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = agent };
                    if (decision is null) env["FUFUCODE_BRIDGE_CONFIG"] = Path.Combine(work, "policy.json");
                    if (decision is null) File.WriteAllText(env["FUFUCODE_BRIDGE_CONFIG"], "{\"timeoutMs\":1500}");
                    var options = new PiRpcOptions(node,
                        [cli, "--mode", "rpc", "--no-session", "--no-extensions", "--model", "fff-stub/fff-stub-model",
                            "--extension", bridge], work, EnvironmentVariables: env, RequireApprovalBridge: true);
                    var session = new ChatSessionCoordinator(options);
                    try
                    {
                        await session.StartAsync();
                        var prompt = session.SendPromptAsync("Write the requested file.");
                        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                        while (session.PendingExtensionUiRequests.Count == 0 && DateTime.UtcNow < deadline)
                            await Task.Delay(20);
                        dialogSeen = session.PendingExtensionUiRequests.Count == 1;
                        if (dialogSeen && decision is { } approve)
                            await session.RespondToExtensionUiAsync(session.PendingExtensionUiRequests.Single(),
                                PiExtensionUiResponse.Confirm(approve));
                        if (dialogSeen) await prompt;
                        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
                        while (session.Snapshot.Activity != ActivityState.Idle && DateTime.UtcNow < deadline)
                            await Task.Delay(50);
                        outcome = session.Snapshot.LastResult.ToString();
                    }
                    catch (Exception error)
                    {
                        outcome = "exception";
                        lastError = error.GetBaseException().Message;
                    }
                    lastError ??= session.Snapshot.LastError;
                    var exited = session.Snapshot.FaultKind == SessionFaultKind.ProcessExited;
                    var code = ParseExitCode(lastError);
                    runs.Add(new(index, tag, dialogSeen, outcome, Truncate(lastError), exited, code,
                        code == CrashSignature, File.Exists(target), Truncate(session.Snapshot.FaultKind == SessionFaultKind.ProcessExited ? lastError : null)));
                    await session.DisposeAsync();
                }
            }
        }
        var report = new Report(cli, await VersionAsync(node, ["--version"]), ReadPiVersion(root), iterations, runs,
            runs.Count(r => r.CrashSignature), runs.Count(r => r.ProcessExited));
        var path = Path.Combine(scratch, "report.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
        { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
        Console.WriteLine($"Crash repro report: {path}");
        Console.WriteLine($"runs={runs.Count} processExits={report.ProcessExitCount} crashSignatures={report.CrashCount}");
        foreach (var run in runs)
            Console.WriteLine($"  {run.Index,3} {run.Decision,-7} outcome={run.Outcome,-12} dialog={run.DialogSeen} exit={run.ExitCode?.ToString() ?? "none"} crash={run.CrashSignature} written={run.FileWritten}");
        Console.WriteLine(report.CrashCount == 0
            ? "RESULT not reproduced: 0xC0000409 did not occur in this run"
            : $"RESULT reproduced: {report.CrashCount} run(s) exited with the 0xC0000409 signature");
    }

    /// <summary>PiRpcClient reports the process exit code in its failure message; read it back.</summary>
    private static int? ParseExitCode(string? message)
    {
        if (message is null) return null;
        var marker = "exited with code ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        var end = start;
        while (end < message.Length && (char.IsAsciiDigit(message[end]) || message[end] == '-')) end++;
        return int.TryParse(message[start..end], out var code) ? code : null;
    }

    private static string? Truncate(string? value) => value is null ? null : value.Length <= 600 ? value : value[..600] + "…";

    private static string? ReadPiVersion(string root)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "runtime", "pi", "package.json")));
            return document.RootElement.GetProperty("dependencies")
                .GetProperty("@earendil-works/pi-coding-agent").GetString();
        }
        catch (Exception) { return null; }
    }

    private static async Task<string?> VersionAsync(string executable, IEnumerable<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Could not start " + executable);
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output.Trim();
        }
        catch (Exception) { return null; }
    }
}
