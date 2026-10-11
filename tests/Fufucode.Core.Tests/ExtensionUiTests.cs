using System.Collections.Concurrent;
using System.Text.Json;
using Fufucode.Core;
using Fufucode.Pi;

internal static class ExtensionUiTests
{
    public static async Task RunRealAsync(string root, string node, string cli)
    {
        var fixture = Path.Combine(root, "tests", "fixtures", "approval-ui-fixture.ts");
        var work = Path.Combine(root, "tmp", "real approval ui");
        var privateAgent = Path.Combine(work, "pi-agent");
        Directory.CreateDirectory(privateAgent);
        var marker = Path.Combine(work, "marker.txt");
        foreach (var decision in new bool?[] { true, false, null })
        {
            File.WriteAllText(marker, "before");
            var options = new PiRpcOptions(node,
                [cli, "--mode", "rpc", "--no-session", "--no-extensions", "--extension", fixture], work,
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["PI_CODING_AGENT_DIR"] = privateAgent,
                    ["FUFUCODE_APPROVAL_FIXTURE_FILE"] = marker
                });
            await using var session = new ChatSessionCoordinator(options);
            var notifications = new ConcurrentQueue<PiExtensionUiRequestEvent>();
            var seen = new ConcurrentQueue<PiSessionEvent>();
            session.ProtocolEvent += seen.Enqueue;
            session.ExtensionUiNotification += notifications.Enqueue;
            await session.StartAsync();
            var prompt = session.SendPromptAsync("/fufucode-approval-fixture");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (session.PendingExtensionUiRequests.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            if (session.PendingExtensionUiRequests.Count != 1)
                throw new Exception($"Real Pi emitted no fixture confirmation. Events: {string.Join(",", seen.Select(e => e is PiExtensionUiRequestEvent ui ? $"{ui.Type}/{ui.Method}:{ui.Raw.GetRawText()}" : e.Type))}; error: {session.Snapshot.LastError}; approval: {session.ApprovalDiagnostic}");
            var request = session.PendingExtensionUiRequests.Single();
            if (request.Method != "confirm" || request.Payload.GetProperty("title").GetString() != "Approve fixture write?")
                throw new Exception("Real Pi fixture did not issue the expected confirmation.");
            if (decision is { } allow)
                await session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(allow));
            await prompt;
            deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (!notifications.Any(n => n.Method == "notify" &&
                   n.Raw.GetProperty("message").GetString()!.StartsWith("fixture finished:", StringComparison.Ordinal)) &&
                   DateTime.UtcNow < deadline)
                await Task.Delay(10);
            if (!notifications.Any(n => n.Method == "notify" &&
                n.Raw.GetProperty("message").GetString()!.StartsWith("fixture finished:", StringComparison.Ordinal)))
                throw new TimeoutException("Real Pi fixture did not finish after the confirmation.");
            var content = File.ReadAllText(marker);
            if (content != (decision == true ? "approved" : "before") || session.PendingExtensionUiRequests.Count != 0)
                throw new Exception($"Real Pi approval {decision?.ToString() ?? "timeout"} left unexpected marker or pending state.");
            Console.WriteLine($"PASS real Pi approval {decision?.ToString() ?? "timeout"}: marker={content}, pending=0");
        }
        foreach (var action in new[] { "stop", "close" })
        {
            File.WriteAllText(marker, "before");
            var options = new PiRpcOptions(node,
                [cli, "--mode", "rpc", "--no-session", "--no-extensions", "--extension", fixture], work,
                EnvironmentVariables: new Dictionary<string, string>
                {
                    ["PI_CODING_AGENT_DIR"] = privateAgent,
                    ["FUFUCODE_APPROVAL_FIXTURE_FILE"] = marker
                });
            await using var session = new ChatSessionCoordinator(options);
            await session.StartAsync();
            var prompt = session.SendPromptAsync("/fufucode-approval-fixture");
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (session.PendingExtensionUiRequests.Count == 0 && DateTime.UtcNow < until) await Task.Delay(10);
            if (session.PendingExtensionUiRequests.Count != 1) throw new Exception("Fixture dialog did not open.");
            if (action == "stop") await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            else await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(10));
            try { await prompt; } catch (PiRpcException) when (action == "close") { }
            if (File.ReadAllText(marker) != "before" || session.PendingExtensionUiRequests.Count != 0)
                throw new Exception($"{action} left an approval side effect or pending dialog.");
            Console.WriteLine($"PASS real Pi {action} rejects pending extension command without deadlock");
        }

        File.WriteAllText(marker, "before");
        await using (var delayed = new ChatSessionCoordinator(new PiRpcOptions(node,
            [cli, "--mode", "rpc", "--no-session", "--no-extensions", "--extension", fixture], work,
            EnvironmentVariables: new Dictionary<string, string>
            {
                ["PI_CODING_AGENT_DIR"] = privateAgent,
                ["FUFUCODE_APPROVAL_FIXTURE_FILE"] = marker
            })))
        {
            await delayed.StartAsync();
            var prompt = delayed.SendPromptAsync("/fufucode-slow-approval-fixture");
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (delayed.PendingExtensionUiRequests.Count == 0 && DateTime.UtcNow < until) await Task.Delay(10);
            if (delayed.PendingExtensionUiRequests.Count != 1) throw new Exception("Delayed fixture dialog did not open.");
            await Task.Delay(TimeSpan.FromSeconds(11));
            if (prompt.IsCompleted) throw new Exception("Extension command acknowledgement ended before its dialog reply.");
            await delayed.RespondToExtensionUiAsync(delayed.PendingExtensionUiRequests.Single(), PiExtensionUiResponse.Confirm(true));
            await prompt;
            if (File.ReadAllText(marker) != "approved") throw new Exception("Delayed extension command did not finish its approved write.");
            Console.WriteLine("PASS real Pi extension command acknowledges after an 11-second approval wait");
        }
    }

    public static async Task RunAsync(string root, string fake, string dotnet)
    {
        var work = Path.Combine(root, "tmp", "extension-ui-tests");
        Directory.CreateDirectory(work);
        var marker = Path.Combine(work, "marker.txt");
        PiRpcOptions Options() => new(dotnet, [fake, "--approval-path", marker], work);
        static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine($"PASS {name}");
        }
        static async Task WaitFor(Func<bool> condition)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition())
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("Expected extension UI state was not reached.");
                await Task.Delay(10);
            }
        }

        foreach (var (decision, expectedWrite) in new[] { (true, true), (false, false), ((bool?)null, false) })
        {
            if (File.Exists(marker)) File.Delete(marker);
            await using var session = new ChatSessionCoordinator(Options());
            var events = new ConcurrentQueue<PiSessionEvent>();
            var dialogs = new ConcurrentQueue<ExtensionUiRequest>();
            var closed = new ConcurrentQueue<ExtensionUiRequest>();
            var notices = new ConcurrentQueue<PiExtensionUiRequestEvent>();
            session.ProtocolEvent += events.Enqueue;
            session.ExtensionUiRequestReceived += dialogs.Enqueue;
            session.ExtensionUiRequestClosed += closed.Enqueue;
            session.ExtensionUiNotification += notices.Enqueue;
            await session.StartAsync();
            Check(session.ApprovalAvailability == ApprovalAvailability.Unavailable,
                "missing bridge is reported unavailable after get_commands probe");
            await session.SendPromptAsync("approval");
            await WaitFor(() => dialogs.Count == 1 && notices.Count == 1 &&
                events.Any(e => e.Type == "progress_while_approval"));
            var request = dialogs.Single();
            Check(session.Snapshot.PendingUiRequests.Single() == request &&
                request.Payload.GetProperty("message").GetString() == "Exact action: write marker",
                "pending dialog retains exact request and payload");
            Check(session.Snapshot.Activity == ActivityState.Running &&
                (await session.QueryStateAsync()).Success && (await session.QueryCommandsAsync()).Success,
                "stdout and ordinary commands continue while approval is pending");
            Check(session.ApprovalAvailability == ApprovalAvailability.Unavailable &&
                session.ApprovalDiagnostic!.Contains("example failure"),
                "extension_error is visible without claiming approval is active");
            if (decision is { } allow)
                await session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(allow));
            else await WaitFor(() => session.Snapshot.PendingUiRequests.Count == 0);
            await WaitFor(() => session.Snapshot.PendingUiRequests.Count == 0 &&
                session.Snapshot.Activity == ActivityState.Idle);
            Check(closed.Count == 1 && ReferenceEquals(closed.Single(), request),
                "desktop receives dialog closure after reply or timeout");
            var state = (await session.QueryStateAsync()).Data!.Value;
            var reply = state.GetProperty("uiReplies").EnumerateArray().Single();
            Check(reply.GetProperty("id").GetString() == "approval-1" &&
                (decision is { } expected ? reply.GetProperty("confirmed").GetBoolean() == expected :
                    reply.GetProperty("cancelled").GetBoolean()),
                $"approval {(decision is null ? "timeout" : decision.Value ? "allow" : "deny")} uses matching ID and result");
            Check(File.Exists(marker) == expectedWrite && state.GetProperty("uiEffectCount").GetInt32() == (expectedWrite ? 1 : 0),
                "only approved request changes the marker file");
            try
            {
                await session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(true));
                throw new Exception("replayed approval was accepted");
            }
            catch (InvalidOperationException) { Console.WriteLine("PASS answered or expired approval cannot be replayed"); }
        }

        await using (var verified = new ChatSessionCoordinator(new PiRpcOptions(dotnet,
            [fake, "--bridge-mode", "present"], work)))
        {
            var states = new ConcurrentQueue<ApprovalAvailability>();
            verified.SnapshotChanged += snapshot => states.Enqueue(snapshot.ApprovalAvailability);
            Check(verified.ApprovalAvailability == ApprovalAvailability.Unverified,
                "approval starts unverified before process startup");
            await verified.StartAsync();
            Check(states.Contains(ApprovalAvailability.Unverified) &&
                verified.ApprovalAvailability == ApprovalAvailability.Verified &&
                verified.ApprovalDiagnostic is null,
                "registered bridge probe transitions Unverified to Verified");
        }

        await using (var degraded = new ChatSessionCoordinator(new PiRpcOptions(dotnet,
            [fake, "--bridge-mode", "present"], work, RequireApprovalBridge: true)))
        {
            await degraded.StartAsync();
            await degraded.SendPromptAsync("approval");
            await WaitFor(() => degraded.ApprovalAvailability == ApprovalAvailability.Unavailable &&
                degraded.PendingExtensionUiRequests.Count == 1);
            await degraded.RespondToExtensionUiAsync(degraded.PendingExtensionUiRequests.Single(),
                PiExtensionUiResponse.Cancel());
            try { await degraded.SendPromptAsync("another"); throw new Exception("degraded bridge accepted another prompt"); }
            catch (InvalidOperationException) { }
            Check(!degraded.CanSendPrompt,
                "extension error transitions Verified to Unavailable and blocks later prompts");
        }

        await using (var broken = new ChatSessionCoordinator(new PiRpcOptions(dotnet,
            [fake, "--bridge-mode", "broken"], work)))
        {
            try { await broken.StartAsync(); throw new Exception("broken extension was accepted"); }
            catch (PiRpcException) { }
            Check(broken.ApprovalAvailability == ApprovalAvailability.Unavailable &&
                broken.Snapshot.Connection == ConnectionState.Faulted &&
                broken.ApprovalDiagnostic!.Contains("startup failed"),
                "fatal extension startup maps to Unavailable with diagnostic");
        }

        await using (var unknown = new ChatSessionCoordinator(Options()))
        {
            var seen = new ConcurrentQueue<PiSessionEvent>();
            unknown.ProtocolEvent += seen.Enqueue;
            await unknown.StartAsync();
            await unknown.SendPromptAsync("ui_unknown");
            await WaitFor(() => seen.OfType<PiExtensionUiRequestEvent>().Any());
            var state = (await unknown.QueryStateAsync()).Data!.Value;
            Check(unknown.PendingExtensionUiRequests.Count == 0 &&
                state.GetProperty("uiReplies").GetArrayLength() == 0 &&
                unknown.Snapshot.LastError!.Contains("Unknown extension UI method ignored"),
                "unknown method is recorded and ignored without a response");
        }

        await using (var editor = new ChatSessionCoordinator(new PiRpcOptions(dotnet,
            [fake], work, UiDefaultTimeout: TimeSpan.FromMilliseconds(200))))
        {
            var closed = new ConcurrentQueue<ExtensionUiRequest>();
            editor.ExtensionUiRequestClosed += closed.Enqueue;
            await editor.StartAsync();
            await editor.SendPromptAsync("editor_only");
            await WaitFor(() => closed.Count == 1 && editor.PendingExtensionUiRequests.Count == 0);
            JsonElement replies = default;
            for (var i = 0; i < 100; i++)
            {
                replies = (await editor.QueryStateAsync()).Data!.Value.GetProperty("uiReplies");
                if (replies.GetArrayLength() == 1) break;
                await Task.Delay(10);
            }
            Check(replies[0].GetProperty("id").GetString() == "editor-only" &&
                replies[0].GetProperty("cancelled").GetBoolean(),
                "editor without Pi timeout receives a host cancellation deadline");
        }

        await using (var lost = new ChatSessionCoordinator(Options()))
        {
            var opened = new ConcurrentQueue<ExtensionUiRequest>();
            var closed = new ConcurrentQueue<ExtensionUiRequest>();
            lost.ExtensionUiRequestReceived += opened.Enqueue;
            lost.ExtensionUiRequestClosed += closed.Enqueue;
            await lost.StartAsync();
            await lost.SendPromptAsync("approval_crash");
            await WaitFor(() => lost.Snapshot.Connection == ConnectionState.Faulted);
            Check(opened.Count == 1 && closed.Count == 1 &&
                lost.PendingExtensionUiRequests.Count == 0 &&
                lost.ApprovalAvailability == ApprovalAvailability.Unavailable,
                "connection loss rejects and closes pending approval locally");
        }

        await using (var adapter = await PiChatSessionAdapter.ConnectAsync(Options()))
        {
            var ui = (IExtensionUiSession)adapter;
            var notifications = new ConcurrentQueue<PiExtensionUiRequestEvent>();
            ui.ExtensionUiNotification += notifications.Enqueue;
            await adapter.SendAsync("ui_dialogs");
            await WaitFor(() => ui.PendingExtensionUiRequests.Count == 3 && notifications.Count == 1);
            Check(notifications.Single().Method == "setStatus", "fire-and-forget status bypasses pending dialog set");
            var select = ui.PendingExtensionUiRequests.Single(r => r.Method == "select");
            try
            {
                await ui.RespondToExtensionUiAsync(select, PiExtensionUiResponse.WithValue("Unlisted"));
                throw new Exception("unlisted select choice was accepted");
            }
            catch (ArgumentException) { Console.WriteLine("PASS select response is limited to offered options"); }
            await ui.RespondToExtensionUiAsync(select, PiExtensionUiResponse.WithValue("Allow"));
            await ui.RespondToExtensionUiAsync(ui.PendingExtensionUiRequests.Single(r => r.Method == "input"),
                PiExtensionUiResponse.WithValue("typed"));
            await ui.RespondToExtensionUiAsync(ui.PendingExtensionUiRequests.Single(r => r.Method == "editor"),
                PiExtensionUiResponse.Cancel());
            Check(ui.PendingExtensionUiRequests.Count == 0, "select/input/editor replies clear pending dialogs");
        }

        await using (var session = new ChatSessionCoordinator(Options()))
        {
            await session.StartAsync();
            await session.SendPromptAsync("approval_settled");
            await WaitFor(() => session.Snapshot.Activity == ActivityState.Idle && session.Snapshot.PendingUiRequests.Count == 1);
            Check(session.Snapshot.PendingUiRequests.Count == 1,
                "agent_settled does not erase a concurrent approval dialog");
            await session.RespondToExtensionUiAsync(session.Snapshot.PendingUiRequests.Single(), PiExtensionUiResponse.Confirm(false));
        }

        await using (var first = new ChatSessionCoordinator(Options()))
        {
            await first.StartAsync();
            await first.SendPromptAsync("approval");
            await WaitFor(() => first.PendingExtensionUiRequests.Count == 1);
            var old = first.PendingExtensionUiRequests.Single();
            await first.CloseAsync();
            Check(first.PendingExtensionUiRequests.Count == 0, "closing clears pending dialogs");
            await using var second = new ChatSessionCoordinator(Options());
            await second.StartAsync();
            await second.SendPromptAsync("approval");
            await WaitFor(() => second.PendingExtensionUiRequests.Count == 1);
            try
            {
                await second.RespondToExtensionUiAsync(old, PiExtensionUiResponse.Confirm(true));
                throw new Exception("old generation approval was accepted");
            }
            catch (InvalidOperationException) { Console.WriteLine("PASS old generation approval cannot authorize new connection"); }
        }
    }
}
