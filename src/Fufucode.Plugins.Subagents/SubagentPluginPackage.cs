using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Fufucode.Core.Backend;
using Fufucode.Core.Plugins;

namespace Fufucode.Plugins.Subagents;

/// <summary>可选 Subagent 插件：install 后提供单一、默认开启的子代理能力，无档位选择。</summary>
public sealed class SubagentPluginPackage : IPluginPackage
{
    public const string PluginId = "workspace.subagents";
    private readonly object _gate = new();
    private SubagentFactory? _factory;
    private SubagentGeneration? _generation;

    public string Id => PluginId;
    public string Version => "1.0.0";

    public Task InitializeAsync(PluginPackageContext context, CancellationToken cancellationToken = default)
    {
        var generation = new SubagentGeneration();
        var factory = new SubagentFactory(generation);
        lock (_gate)
        {
            _generation = generation;
            _factory = factory;
        }
        NativeSubagentRegistry.Register(factory);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 先撤销并取消该插件代次的所有在途调用，等待执行 lease 退出后才释放取消源。
    /// 可重复调用；若等待被取消，保留 generation 供下次重试，避免提前 Dispose 造成竞态。
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        SubagentFactory? factory;
        SubagentGeneration? generation;
        lock (_gate)
        {
            factory = _factory;
            generation = _generation;
            _factory = null;
        }
        if (factory is not null) NativeSubagentRegistry.Unregister(factory);
        if (generation is null) return;
        generation.Revoke();
        await generation.DrainAsync(cancellationToken).ConfigureAwait(false);
    }

    public IPluginToolHandler? CreateToolHandler(PluginPackageContext context) => null;

    private sealed class SubagentFactory(SubagentGeneration generation) : INativeSubagentFactory
    {
        public INativeSubagentRuntime Create(IPureModelClient modelClient, IPureToolDispatcher capabilities,
            string? defaultModelId, string? workspaceRoot,
            IReadOnlyList<PureToolDefinition> capabilityDefinitions) =>
            new SubagentRuntime(generation, modelClient, capabilities, defaultModelId, workspaceRoot,
                capabilityDefinitions);
    }

    private sealed class SubagentRuntime : INativeSubagentRuntime, INativeSubagentDispatcher
    {
        private readonly SubagentGeneration _generation;
        private readonly IPureModelClient _modelClient;
        private readonly IPureToolDispatcher _capabilities;
        private readonly string? _defaultModelId;
        private readonly string? _workspaceRoot;
        private readonly IReadOnlyList<PureToolDefinition> _capabilityDefinitions;
        private SubagentRunner _runner;

        public SubagentRuntime(SubagentGeneration generation, IPureModelClient modelClient,
            IPureToolDispatcher capabilities, string? defaultModelId, string? workspaceRoot,
            IReadOnlyList<PureToolDefinition> capabilityDefinitions)
        {
            _generation = generation;
            _modelClient = modelClient;
            _capabilities = capabilities;
            _defaultModelId = defaultModelId;
            _workspaceRoot = workspaceRoot;
            _capabilityDefinitions = capabilityDefinitions;
            _runner = CreateRunner();
        }

        public IPureToolDispatcher Inner => _capabilities;

        /// <summary>Shutdown 撤销后立即为 false；旧会话的声明与执行都读取这同一状态。</summary>
        public bool IsActive => _generation.IsActive;

        public Func<PureToolCall, CancellationToken, Task<bool>>? ApprovalGate
        {
            get => _runner.ApprovalGate;
            set => _runner.ApprovalGate = value;
        }
        public Func<IReadOnlyList<PureAgentMessage>>? HistoryProvider
        {
            get => _runner.HistoryProvider;
            set => _runner.HistoryProvider = value;
        }
        public Func<PermissionMode>? ParentMode
        {
            get => _runner.ParentMode;
            set => _runner.ParentMode = value;
        }
        public Action<NativeSubagentProgress>? Progress
        {
            get => _progress;
            set => _progress = value;
        }
        private Action<NativeSubagentProgress>? _progress;

        public IPureToolDispatcher Wrap(IPureToolDispatcher inner) =>
            new SubagentDispatcher(inner, _runner) { ActiveProvider = () => IsActive };

        public IReadOnlyList<NativeSubagentDescriptor> Agents =>
            _runner.Agents.Select(agent => new NativeSubagentDescriptor(
                agent.Name, agent.Description, agent.Mode.ToString().ToLowerInvariant())).ToArray();

        /// <summary>撤销后旧会话即使仍拼装提示词，也不再注入子代理目录。</summary>
        public string BuildPromptSection() => IsActive ? _runner.BuildPromptSection() : string.Empty;

        /// <summary>只读 utility 任务：使用内置 explore 代理，失败时返回 null 由调用方回退。</summary>
        public async Task<string?> RunUtilityAsync(string prompt, CancellationToken cancellationToken)
        {
            if (!IsActive || string.IsNullOrWhiteSpace(prompt)) return null;
            try
            {
                var result = await _runner.RunAsync("explore", prompt, null, cancellationToken).ConfigureAwait(false);
                return result.Success ? result.Content?.Trim() : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        private SubagentRunner CreateRunner()
        {
            var runner = new SubagentRunner(AgentRegistry.Discover(_workspaceRoot), _modelClient,
                _capabilities, _defaultModelId, _workspaceRoot, _capabilityDefinitions)
            {
                Lifetime = _generation
            };
            runner.Progress = progress => _progress?.Invoke(new NativeSubagentProgress(
                progress.JobId, progress.Agent, progress.Kind, progress.Detail));
            return runner;
        }
    }
}
