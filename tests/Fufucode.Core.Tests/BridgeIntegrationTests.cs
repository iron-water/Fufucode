using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

internal static class BridgeIntegrationTests
{
    internal sealed class StubHost(Process process) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public int Port { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { Process.StandardInput.Close(); } catch (IOException) { }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await Process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }
            Process.Dispose();
        }
    }

    public static async Task RunAsync(string root, string node, string cli)
    {
        var bridge = Path.Combine(root, "extensions", "fufucode-bridge");
        var scratch = Path.Combine(root, "tmp", "bridge-core", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine($"PASS {name}");
        }
        static async Task WaitFor(Func<bool> condition, string description, int seconds = 20)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException(description);
                await Task.Delay(20);
            }
        }

        // A03: both absence and fatal load failure must leave approval unavailable.
        var absentWork = Path.Combine(scratch, "absent");
        var absentAgent = Path.Combine(absentWork, "agent");
        Directory.CreateDirectory(absentAgent);
        await using (var absent = PiSessions.Coordinator(new PiRpcOptions(node,
            [cli, "--mode", "rpc", "--no-session", "--no-extensions"], absentWork,
            EnvironmentVariables: new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = absentAgent },
            RequireApprovalBridge: true)))
        {
            await absent.StartAsync();
            Check(absent.ApprovalAvailability == ApprovalAvailability.Unavailable &&
                absent.ApprovalDiagnostic!.Contains("fufucode-bridge.status"),
                "real Pi without bridge reports approval unavailable");
            try { await absent.SendPromptAsync("Write a file."); throw new Exception("unprotected prompt was sent"); }
            catch (InvalidOperationException) { }
            Check(absent.Snapshot.Activity == ActivityState.Idle && absent.Snapshot.Messages.Count == 0,
                "product mode blocks prompts when approval bridge is unavailable");
        }

        var broken = Path.Combine(scratch, "broken.ts");
        File.WriteAllText(broken, "throw new Error('intentional bridge load failure');\n");
        await using (var failed = PiSessions.Coordinator(new PiRpcOptions(node,
            [cli, "--mode", "rpc", "--no-session", "--no-extensions", "--extension", broken], absentWork,
            EnvironmentVariables: new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = absentAgent })))
        {
            try { await failed.StartAsync(); throw new Exception("Broken extension unexpectedly loaded."); }
            catch (RpcException) { }
            Check(failed.ApprovalAvailability == ApprovalAvailability.Unavailable &&
                failed.Snapshot.Connection == ConnectionState.Faulted &&
                failed.ApprovalDiagnostic is { Length: > 0 },
                "real Pi fatal extension load is diagnosed as unavailable");
        }

        foreach (var decision in new bool?[] { true, false, null })
        {
            var tag = decision is true ? "allow" : decision is false ? "deny" : "timeout";
            var work = Path.Combine(scratch, tag, "project space");
            var agent = Path.Combine(scratch, tag, "agent");
            Directory.CreateDirectory(work);
            Directory.CreateDirectory(agent);
            var target = Path.Combine(work, "批准结果.txt");
            var script = Path.Combine(scratch, tag, "stub-script.json");
            File.WriteAllText(script, JsonSerializer.Serialize(new object[]
            {
                new { type = "tool", name = "write", args = new { path = target, content = "written after approval" } },
                new { type = "text", text = "Tool turn finished." }
            }));
            await using var stub = await StartStubAsync(root, node, script);
            File.WriteAllText(Path.Combine(agent, "models.json"), JsonSerializer.Serialize(new
            {
                providers = new Dictionary<string, object>
                {
                    ["fff-stub"] = new { baseUrl = $"http://127.0.0.1:{stub.Port}", apiKey = "stub-key",
                        api = "openai-completions", models = new[] { new { id = "fff-stub-model", name = "FFF Stub Model" } } }
                }
            }));
            var policy = Path.Combine(scratch, tag, "bridge-policy.json");
            if (decision is null) File.WriteAllText(policy, "{\"timeoutMs\":1500}");
            var env = new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = agent };
            if (decision is null) env["FUFUCODE_BRIDGE_CONFIG"] = policy;
            var options = new PiRpcOptions(node,
                [cli, "--mode", "rpc", "--no-session", "--no-extensions", "--model", "fff-stub/fff-stub-model",
                    "--extension", bridge], work, EnvironmentVariables: env, RequireApprovalBridge: true);
            await using var session = PiSessions.Coordinator(options);
            var dialogs = new ConcurrentQueue<ExtensionUiRequest>();
            session.ExtensionUiRequestReceived += dialogs.Enqueue;
            await session.StartAsync();
            Check(session.ApprovalAvailability == ApprovalAvailability.Verified,
                $"real bridge {tag} registered its readiness probe");
            var prompt = session.SendPromptAsync("Write the requested file.");
            await WaitFor(() => dialogs.Count == 1, $"Bridge {tag} did not ask for tool approval.");
            var request = dialogs.Single();
            Check(request.Method == "confirm" &&
                request.Payload.GetProperty("message").GetString()!.Contains(target, StringComparison.Ordinal),
                $"real bridge {tag} approval includes the exact tool path");
            Check((await session.QueryCommandsAsync()).Success && session.PendingExtensionUiRequests.Count == 1,
                $"real bridge {tag} keeps RPC queries live while approval is pending");
            if (decision is { } approve)
                await session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(approve));
            await prompt;
            await WaitFor(() => session.Snapshot.Activity == ActivityState.Idle &&
                session.Snapshot.LastResult != TaskResult.Unknown, $"Bridge {tag} did not settle.", 40);
            Check(File.Exists(target) == (decision == true),
                $"real bridge {tag} enforces filesystem approval result");
            // Pi starts its timeout before emitting the request; the desktop starts its
            // own deadline after receiving it. Engine settling cannot synchronize those
            // two clocks, and must not erase unrelated pending UI requests.
            await WaitFor(() => session.PendingExtensionUiRequests.Count == 0,
                $"Bridge {tag} desktop approval did not close after its deadline.", 5);
            Check(session.PendingExtensionUiRequests.Count == 0 && File.Exists(target) == (decision == true),
                $"real bridge {tag} closes approval without changing its filesystem decision");
        }
    }

    internal static async Task<StubHost> StartStubAsync(string root, string node, string script)
    {
        var wrapper = Path.Combine(root, "tests", "fixtures", "core-bridge-stub.mjs");
        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(wrapper);
        start.ArgumentList.Add(script);
        var process = Process.Start(start) ?? throw new Exception("Unable to start bridge model stub.");
        try
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (!int.TryParse(line, out var port))
                throw new Exception($"Bridge model stub did not report a port: {await process.StandardError.ReadToEndAsync()}");
            return new StubHost(process) { Port = port };
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }
    }
}
