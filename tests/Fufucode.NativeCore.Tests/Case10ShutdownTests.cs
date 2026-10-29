using System.Reflection;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.Subagents;

namespace Fufucode.NativeCore.Tests;

/// <summary>Case 10: plugin shutdown revokes the old runtime, dispatcher and session declarations.</summary>
internal static class Case10ShutdownTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = TestTemp.Create("10");
        try
        {
            await RunGenerationAsync();
            await RunPluginLifecycleAsync();
            await RunInflightRunnerAsync();
            await RunInflightDispatcherAsync();
            await RunIntegrationAsync();
        }
        finally
        {
            TestTemp.Delete(root);
        }

        async Task RunGenerationAsync()
        {
            var generation = InternalAccess.NewGeneration();
            var entered = InternalAccess.GenerationTryEnter(generation, out var token);
            checks.Check(InternalAccess.GenerationActive(generation) && entered,
                "generation accepts a lease while active");
            InternalAccess.GenerationRevoke(generation);
            checks.Check(!InternalAccess.GenerationActive(generation) && token.IsCancellationRequested,
                "revoke flips active state and cancels the issued token");

            var drain = InternalAccess.GenerationDrain(generation, CancellationToken.None);
            await Task.Delay(100);
            checks.Check(!drain.IsCompleted && !InternalAccess.GenerationDisposed(generation),
                "drain waits for the in-flight lease and does not dispose early");
            InternalAccess.GenerationExit(generation);
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            checks.Check(InternalAccess.GenerationDisposed(generation), "drain disposes the CTS only after the last lease exits");
            checks.Check(!InternalAccess.GenerationTryEnter(generation, out _),
                "revoked generation rejects new leases without touching the disposed CTS");
            InternalAccess.GenerationRevoke(generation);
            await InternalAccess.GenerationDrain(generation, CancellationToken.None);
            checks.Check(InternalAccess.GenerationDisposed(generation), "repeat revoke/drain is idempotent and safe");

            var retry = InternalAccess.NewGeneration();
            checks.Check(InternalAccess.GenerationTryEnter(retry, out _), "retry generation accepts a lease");
            InternalAccess.GenerationRevoke(retry);
            using var shutdownCts = new CancellationTokenSource();
            var cancelledDrain = InternalAccess.GenerationDrain(retry, shutdownCts.Token);
            shutdownCts.Cancel();
            try { await cancelledDrain; } catch (OperationCanceledException) { }
            checks.Check(!InternalAccess.GenerationDisposed(retry),
                "cancelled shutdown wait keeps the CTS alive for a later retry");
            InternalAccess.GenerationExit(retry);
            await InternalAccess.GenerationDrain(retry, CancellationToken.None);
            checks.Check(InternalAccess.GenerationDisposed(retry),
                "retried drain releases the CTS after the lease exits");
        }

        async Task RunPluginLifecycleAsync()
        {
            var package = new SubagentPluginPackage();
            await package.InitializeAsync(new PluginPackageContext(SubagentPluginPackage.PluginId, root, null));
            var factory = NativeSubagentRegistry.Factory ??
                          throw new InvalidOperationException("registry did not register factory");
            var runtime = factory.Create(new NoNetworkClient(), new NullDispatcher(), "m", root, []);
            var wrapped = runtime.Wrap(new NullDispatcher());

            checks.Check(runtime.IsActive && ((INativeSubagentDispatcher)runtime).IsActive,
                "runtime reports active before shutdown");
            checks.Check(((INativeSubagentDispatcher)wrapped).IsActive,
                "factory wrapper reads the live runtime state");
            checks.Check(runtime.BuildPromptSection().Contains("可用子代理"),
                "prompt section present before shutdown");
            var listBefore = await wrapped.DispatchAsync(
                new PureToolCall("l1", "subagent", "{\"action\":\"list\"}"));
            checks.Check(listBefore.Success && listBefore.Content.Contains("explore"),
                "old session dispatcher lists agents before shutdown");

            await package.ShutdownAsync();
            checks.Check(NativeSubagentRegistry.Factory is null, "shutdown unregisters the factory");
            checks.Check(!runtime.IsActive && !((INativeSubagentDispatcher)runtime).IsActive,
                "old runtime reports revoked after shutdown");
            checks.Check(!((INativeSubagentDispatcher)wrapped).IsActive,
                "old session wrapper observes revocation dynamically");

            var listAfter = await wrapped.DispatchAsync(
                new PureToolCall("l2", "subagent", "{\"action\":\"list\"}"));
            checks.Check(!listAfter.Success && listAfter.Content.Contains("卸载"),
                "old session dispatcher rejects list after shutdown");
            var runAfter = await wrapped.DispatchAsync(new PureToolCall("r1", "subagent",
                "{\"agent\":\"explore\",\"prompt\":\"x\"}"));
            checks.Check(!runAfter.Success && runAfter.Content.Contains("卸载"),
                "old session dispatcher rejects run after shutdown");
            checks.Check(runtime.BuildPromptSection().Length == 0, "prompt section is empty after shutdown");
            checks.Check(await runtime.RunUtilityAsync("x", CancellationToken.None) is null,
                "utility run falls back to null after shutdown");

            await package.ShutdownAsync();
            await package.ShutdownAsync();
            checks.Check(true, "repeated shutdown is idempotent");

            await package.InitializeAsync(new PluginPackageContext(SubagentPluginPackage.PluginId, root, null));
            checks.Check(NativeSubagentRegistry.Factory is not null && !runtime.IsActive,
                "reinitialize registers a new generation while the old runtime stays revoked");
            await package.ShutdownAsync();
        }

        async Task RunInflightRunnerAsync()
        {
            var generation = InternalAccess.NewGeneration();
            var blocking = new BlockingClient();
            var runner = new SubagentRunner(AgentRegistry.Discover(root, root), blocking,
                new NullDispatcher(), "m", root);
            InternalAccess.SetLifetime(runner, generation);

            var run = runner.RunAsync("explore", "等待撤销", null, CancellationToken.None);
            await blocking.Started.WaitAsync(TimeSpan.FromSeconds(5));
            InternalAccess.GenerationRevoke(generation);
            var drain = InternalAccess.GenerationDrain(generation, CancellationToken.None);

            var completed = false;
            try
            {
                var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
                completed = true;
                checks.Check(!result.Success,
                    "in-flight run returns a failed result after shutdown cancellation");
            }
            catch (OperationCanceledException)
            {
                completed = true;
                checks.Check(true, "in-flight run observes shutdown cancellation");
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("in-flight run failed unexpectedly: " + error.Message, error);
            }
            checks.Check(completed, "in-flight run completed after shutdown");
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            checks.Check(InternalAccess.GenerationDisposed(generation), "generation disposes after the in-flight run exits");
        }

        async Task RunInflightDispatcherAsync()
        {
            var package = new SubagentPluginPackage();
            await package.InitializeAsync(new PluginPackageContext(SubagentPluginPackage.PluginId, root, null));
            var blocking = new BlockingClient();
            var runtime = NativeSubagentRegistry.Factory!.Create(blocking, new NullDispatcher(), "m", root, []);
            var wrapped = runtime.Wrap(new NullDispatcher());

            var dispatch = wrapped.DispatchAsync(new PureToolCall("d1", "subagent",
                "{\"agent\":\"explore\",\"prompt\":\"等待卸载\"}"));
            await blocking.Started.WaitAsync(TimeSpan.FromSeconds(5));

            var shutdown = package.ShutdownAsync();
            checks.Check(!runtime.IsActive, "shutdown revokes the runtime before waiting");
            try { await dispatch.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
            checks.Check(!((INativeSubagentDispatcher)wrapped).IsActive,
                "dispatcher is revoked after shutdown completes");
            await package.ShutdownAsync();
        }

        async Task RunIntegrationAsync()
        {
            var package = new SubagentPluginPackage();
            await package.InitializeAsync(new PluginPackageContext(SubagentPluginPackage.PluginId, root, null));
            var runtime = NativeSubagentRegistry.Factory!.Create(new NoNetworkClient(), new NullDispatcher(),
                "m", root, []);
            var wrapped = runtime.Wrap(new NullDispatcher());
            var engine = new PureCSharpAgentEngine(wrapped, new NoNetworkClient());
            var session = new NativeCSharpChatSession(engine, "subagent-session", "m", subagents: runtime);

            checks.Check(ToolNames(session).Contains(NativeSubagentContract.ToolName),
                "session declares subagent tool while plugin is active");
            checks.Check(ToolDefinitions(session).Any(definition => definition.Name == NativeSubagentContract.ToolName),
                "session declares subagent schema while plugin is active");
            checks.Check(SystemPrompts(session).Any(text => text.Contains("# 可用子代理")),
                "session prompt contains subagent section while plugin is active");

            await package.ShutdownAsync();
            checks.Check(!ToolNames(session).Contains(NativeSubagentContract.ToolName),
                "old session stops declaring subagent tool after shutdown");
            checks.Check(!ToolDefinitions(session).Any(definition => definition.Name == NativeSubagentContract.ToolName),
                "old session stops declaring subagent schema after shutdown");
            checks.Check(!SystemPrompts(session).Any(text => text.Contains("# 可用子代理")),
                "old session prompt stops containing subagent section after shutdown");
            await session.DisposeAsync();
            await package.ShutdownAsync();
        }
    }

    private static IReadOnlyList<string> ToolNames(NativeCSharpChatSession session) =>
        (IReadOnlyList<string>)typeof(NativeCSharpChatSession)
            .GetMethod("BuildToolNames", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, null)!;

    private static IReadOnlyList<PureToolDefinition> ToolDefinitions(NativeCSharpChatSession session) =>
        (IReadOnlyList<PureToolDefinition>)typeof(NativeCSharpChatSession)
            .GetMethod("BuildToolDefinitions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(session, null)!;

    private static IEnumerable<string> SystemPrompts(NativeCSharpChatSession session)
    {
        var method = typeof(NativeCSharpChatSession).GetMethod("BuildTurnMessages",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(string), typeof(IReadOnlyList<PureImageAttachment>) }, null)!;
        var messages = (IReadOnlyList<PureAgentMessage>)method.Invoke(session, new object?[] { "check", null })!;
        return messages.Where(message => message.Role == PureAgentRole.System)
            .Select(message => message.Content ?? string.Empty);
    }

    private sealed class NullDispatcher : IPureToolDispatcher
    {
        public Task<PureToolResult> DispatchAsync(PureToolCall call,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new PureToolResult(call.Id, true, "{}"));
    }

    private sealed class NoNetworkClient : IPureModelClient
    {
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Model/API access prohibited in shutdown tests");
    }

    private sealed class BlockingClient : IPureModelClient
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public async Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("unreachable");
        }
    }
}
