using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;
using Fufucode.Plugins.Subagents;

namespace Fufucode.NativeCore.Tests;

/// <summary>Case 07: the real plugin factory chain must keep delivery, Plan mode and plan snippet.</summary>
internal static class Case07FactoryChainTests
{
    public static async Task RunAsync(Checks checks)
    {
        var root = TestTemp.Create("07");
        try
        {
            var package = new SubagentPluginPackage();
            await package.InitializeAsync(new PluginPackageContext(SubagentPluginPackage.PluginId, root, null));
            try
            {
                var builtin = new ProductionToolDispatcher(root);
                await using var mcp = McpManager.Create([]);
                IPureToolDispatcher capabilities = new McpGatewayDispatcher(
                    new SkillAwareDispatcher(builtin, SkillRegistry.Discover(root, root)), mcp);
                var runtime = NativeSubagentRegistry.Factory!.Create(new NoNetwork(), capabilities, null, root, []);
                var wrapped = runtime.Wrap(capabilities);
                var engine = new PureCSharpAgentEngine(wrapped, new NoNetwork());
                engine.Mode = PermissionMode.Plan;
                checks.Check(((INativeSubagentDispatcher)wrapped).IsActive, "factory wrapper activity");
                checks.Check(builtin.Mode == PermissionMode.Plan, "factory MCP Skill mode propagation");
                var plan = await wrapped.DispatchAsync(new PureToolCall("plan", "update_plan",
                    "{\"steps\":[{\"title\":\"factory-chain-proof\",\"status\":\"in_progress\"}]}"));
                checks.Check(plan.Success, "plan update through factory wrapper");
                checks.Check(engine.CurrentPlanSnippet?.Contains("factory-chain-proof") == true,
                    "factory plan snippet propagation");
                var blocked = await wrapped.DispatchAsync(new PureToolCall("write", "write_file", "{}"));
                checks.Check(!blocked.Success && blocked.Content.Contains("Plan"),
                    "Plan blocks mutation before argument validation");
                var runner = new SubagentRunner(AgentRegistry.Discover(root), new NoNetwork(), capabilities, null, root, []);
                var active = true;
                var dynamicWrapper = new SubagentDispatcher(capabilities, runner) { ActiveProvider = () => active };
                var dynamicEngine = new PureCSharpAgentEngine(dynamicWrapper, new NoNetwork());
                checks.Check(((INativeSubagentDispatcher)dynamicWrapper).IsActive, "dynamic active provider");
                active = false;
                checks.Check(!((INativeSubagentDispatcher)dynamicWrapper).IsActive, "provider revocation");
                checks.Check(dynamicEngine.CurrentPlanSnippet is null, "revoked wrapper retains existing engine contract");
                var bare = new SubagentDispatcher(capabilities, runner);
                checks.Check(!((INativeSubagentDispatcher)bare).IsActive, "bare wrapper remains inactive");
            }
            finally
            {
                await package.ShutdownAsync();
            }
        }
        finally
        {
            TestTemp.Delete(root);
        }
    }

    private sealed class NoNetwork : IPureModelClient
    {
        public Task<PureModelResponse> GenerateAsync(PureModelRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Model/API access prohibited");
    }
}
