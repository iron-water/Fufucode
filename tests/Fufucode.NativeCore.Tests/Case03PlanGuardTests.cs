using System.Text.Json;
using Fufucode.App.Abstractions;
using Fufucode.Core;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.NativeCore.Tests;

/// <summary>
/// Case 03: Plan guard must cover outer MCP/plugin wrappers and undeclared tools.
/// Uses the real NativeCSharpChatSession/PureCSharpAgentEngine mode wiring, a real
/// ProductionToolDispatcher plus a fake NativeToolComposite provider. No network.
/// </summary>
internal static class Case03PlanGuardTests
{
    private const string PluginTool = "plugin_write";
    private const string ModelId = "test-model";

    public static async Task RunAsync(Checks checks)
    {
        var root = TestTemp.Create("16");
        try
        {
            var probePath = Path.Combine(root, "probe.txt");
            var writeTarget = Path.Combine(root, "should-not-exist.txt");
            File.WriteAllText(probePath, "MARKER-FUMO-15");
            if (File.Exists(writeTarget)) File.Delete(writeTarget);

            await InFlightSwitchAsync(checks, root, writeTarget);
            await ApprovalWindowSwitchAsync(checks, root, writeTarget);
            await UndeclaredToolAsync(checks, root);
            await NormalReadAsync(checks, root, probePath);
            await PlanReadAsync(checks, root, probePath);
            await PlanBlocksDeclaredPluginAsync(checks, root);
            await NormalPluginExecutesAsync(checks, root);
            await InFlightDeclarationExpansionAsync(checks, root);
        }
        finally
        {
            TestTemp.Delete(root);
        }
    }

    private static async Task InFlightSwitchAsync(Checks checks, string root, string writeTarget)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (session, _, provider) = NewSession(async (call, _, _) =>
        {
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task;
                return PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("c1", "write_file", Args(new { path = writeTarget, content = "x" }))
                });
            }
            return PureModelResponse.FromText("done");
        }, root);
        using (session)
        {
            var outcome = await DriveAsync(session, async () =>
            {
                await session.SendAsync("go");
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await session.SetExecutionModeAsync("plan");
                release.TrySetResult();
            });
            checks.Check(provider.DispatchCount == 0 && !File.Exists(writeTarget) &&
                         HistoryContains(session, "Plan") && outcome.Outcome == SessionOutcome.Completed,
                "case16 in-flight switch to Plan blocks write_file handler");
        }
    }

    private static async Task ApprovalWindowSwitchAsync(Checks checks, string root, string writeTarget)
    {
        var approvalSeen = new TaskCompletionSource<ExtensionUiRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (session, _, provider) = NewSession((call, _, _) =>
        {
            if (call == 1)
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("c1", "write_file", Args(new { path = writeTarget, content = "y" }))
                }));
            return Task.FromResult(PureModelResponse.FromText("done"));
        }, root);
        session.ExtensionUiRequestReceived += request => approvalSeen.TrySetResult(request);
        using (session)
        {
            var outcome = await DriveAsync(session, async () =>
            {
                await session.SendAsync("go");
                var request = await approvalSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await session.SetExecutionModeAsync("plan");
                await session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(true));
            });
            checks.Check(provider.DispatchCount == 0 && !File.Exists(writeTarget) &&
                         HistoryContains(session, "Plan") && outcome.Outcome == SessionOutcome.Completed,
                "case16 approval-window switch to Plan blocks write_file handler");
        }
    }

    private static async Task UndeclaredToolAsync(Checks checks, string root)
    {
        var (session, _, provider) = NewSession((call, _, _) =>
        {
            if (call == 1)
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("c1", PluginTool, "{}")
                }));
            return Task.FromResult(PureModelResponse.FromText("done"));
        }, root, exposePlugin: false);
        using (session)
        {
            var outcome = await DriveAsync(session, () => session.SendAsync("go"));
            checks.Check(provider.DispatchCount == 0 && HistoryContains(session, "未声明") &&
                         outcome.Outcome == SessionOutcome.Completed,
                "case16 undeclared tool denied with handler 0");
        }
    }

    private static async Task NormalReadAsync(Checks checks, string root, string probePath)
    {
        var (session, _, provider) = NewSession((call, _, _) =>
        {
            if (call == 1)
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("c1", "read_file", Args(new { path = probePath }))
                }));
            return Task.FromResult(PureModelResponse.FromText("done"));
        }, root);
        using (session)
        {
            var outcome = await DriveAsync(session, () => session.SendAsync("go"));
            checks.Check(provider.DispatchCount == 0 && HistoryContains(session, "MARKER-FUMO-15") &&
                         outcome.Outcome == SessionOutcome.Completed,
                "case16 normal mode read_file still executes");
        }
    }

    private static async Task PlanReadAsync(Checks checks, string root, string probePath)
    {
        var (session, _, provider) = NewSession((call, _, _) =>
        {
            if (call == 1)
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("c1", "read_file", Args(new { path = probePath }))
                }));
            return Task.FromResult(PureModelResponse.FromText("done"));
        }, root);
        using (session)
        {
            await session.SetExecutionModeAsync("plan");
            var outcome = await DriveAsync(session, () => session.SendAsync("go"));
            checks.Check(provider.DispatchCount == 0 && HistoryContains(session, "MARKER-FUMO-15") &&
                         outcome.Outcome == SessionOutcome.Completed,
                "case16 Plan mode read_file still executes");
        }
    }

    private static async Task PlanBlocksDeclaredPluginAsync(Checks checks, string root)
    {
        var builtin = new ProductionToolDispatcher(root);
        var provider = new CountingProvider();
        var composite = new NativeToolComposite(builtin, () => new IPureToolDispatcher[] { provider });
        var model = new FakeModel((call, _, _) => Task.FromResult(call == 1
            ? PureModelResponse.FromToolCalls(new[] { new PureToolCall("c1", PluginTool, "{}") })
            : PureModelResponse.FromText("done")));
        var engine = new PureCSharpAgentEngine(composite, model);
        var approvals = 0;
        using var session = new NativeCSharpChatSession(engine, sessionId: "case16-engine",
            modelId: ModelId, pluginTools: provider, supportsImages: false, workspaceRoot: root);
        session.ExtensionUiRequestReceived += _ => Interlocked.Increment(ref approvals);
        await session.SetExecutionModeAsync("plan");
        var result = await engine.RunTurnAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "go") },
            new[] { PluginTool }, ModelId));
        checks.Check(provider.DispatchCount == 0 && approvals == 0 &&
                     result.ToolResults.Single().Content.Contains("Plan"),
            "case16 engine via session Mode blocks declared plugin tool with handler 0");
    }

    private static async Task NormalPluginExecutesAsync(Checks checks, string root)
    {
        var (session, _, provider) = NewSession((call, _, _) =>
        {
            if (call == 1)
                return Task.FromResult(PureModelResponse.FromToolCalls(new[]
                {
                    new PureToolCall("c1", PluginTool, "{}")
                }));
            return Task.FromResult(PureModelResponse.FromText("done"));
        }, root);
        session.ExtensionUiRequestReceived += request =>
            _ = session.RespondToExtensionUiAsync(request, PiExtensionUiResponse.Confirm(true));
        using (session)
        {
            var outcome = await DriveAsync(session, () => session.SendAsync("go"));
            checks.Check(provider.DispatchCount == 1 && HistoryContains(session, "plugin-ok") &&
                         outcome.Outcome == SessionOutcome.Completed,
                "case16 normal mode declared plugin tool executes via composite provider");
        }
    }

    private static async Task InFlightDeclarationExpansionAsync(Checks checks, string root)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builtin = new ProductionToolDispatcher(root);
        var provider = new CountingProvider();
        var composite = new NativeToolComposite(builtin, () => new IPureToolDispatcher[] { provider });
        var model = new FakeModel(async (call, _, _) =>
        {
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task;
                return PureModelResponse.FromToolCalls(new[] { new PureToolCall("c1", PluginTool, "{}") });
            }
            return PureModelResponse.FromText("done");
        });
        var engine = new PureCSharpAgentEngine(composite, model);
        var names = new List<string> { "read_file" };
        var run = engine.RunTurnAsync(new PureModelRequest(
            new[] { new PureAgentMessage(PureAgentRole.User, "go") }, names, ModelId));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        names.Add(PluginTool);
        release.TrySetResult();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
        checks.Check(provider.DispatchCount == 0 && result.ToolResults.Single().Content.Contains("未声明"),
            "case16 in-flight declaration expansion does not grant access");
    }

    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static (NativeCSharpChatSession Session, FakeModel Model, CountingProvider Provider) NewSession(
        Func<int, PureModelRequest, CancellationToken, Task<PureModelResponse>> handler,
        string workspace, bool exposePlugin = true)
    {
        var builtin = new ProductionToolDispatcher(workspace);
        var provider = new CountingProvider();
        var composite = new NativeToolComposite(builtin, () => new IPureToolDispatcher[] { provider });
        var model = new FakeModel(handler);
        var session = new NativeCSharpChatSession(model, composite,
            sessionId: Guid.NewGuid().ToString("N"), modelId: ModelId,
            pluginTools: exposePlugin ? provider : null,
            supportsImages: false, workspaceRoot: workspace);
        return (session, model, provider);
    }

    private static async Task<SessionEvent.TurnSettled> DriveAsync(NativeCSharpChatSession session, Func<Task> action)
    {
        var settled = new TaskCompletionSource<SessionEvent.TurnSettled>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SessionEvent> handler = (_, value) =>
        {
            if (value is SessionEvent.TurnSettled turn) settled.TrySetResult(turn);
        };
        session.Event += handler;
        try
        {
            await action();
            return await settled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            session.Event -= handler;
        }
    }

    private static bool HistoryContains(NativeCSharpChatSession session, string fragment) =>
        session.GetHistoryMessages().Any(message => message.Content.Contains(fragment, StringComparison.Ordinal));

    private sealed class FakeModel(Func<int, PureModelRequest, CancellationToken, Task<PureModelResponse>> handler)
        : IPureModelClient
    {
        private int _calls;

        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default) =>
            handler(Interlocked.Increment(ref _calls), request, cancellationToken);
    }

    private sealed class CountingProvider : IPureToolDispatcher, IPluginToolCatalog
    {
        public int DispatchCount;

        public IReadOnlyList<string> ActiveToolNames { get; } = new[] { PluginTool };

        public IReadOnlyList<PluginToolRegistration> ActiveRegistrations { get; } =
            Array.Empty<PluginToolRegistration>();

        public Task<PureToolResult> DispatchAsync(PureToolCall call,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DispatchCount);
            return Task.FromResult(new PureToolResult(call.Id, true, "plugin-ok"));
        }
    }
}
